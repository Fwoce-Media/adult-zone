using System.Security.Cryptography;
using System.Text;

namespace AdultZone;

/// <summary>
/// PIN screen lock. The hash is computed exactly as the Python build computed
/// it — PBKDF2-HMAC-SHA256, 200,000 rounds, 16-byte salt, stored as lowercase
/// hex — so a PIN set in either version unlocks the other.
///
/// While locked, the server refuses every content request (see the gate in
/// Program.cs), so hiding the overlay reveals nothing. It is a screen lock,
/// not encryption: the video files themselves are untouched.
///
/// The unlocked state lives only in memory, so the app always starts locked.
/// </summary>
public static class Lock
{
    private const int Iterations = 200_000;
    private const int MinLength = 4;
    private const int MaxLength = 8;
    private const int FailThreshold = 5;
    private const double FailDelaySeconds = 20;

    private static readonly object Gate = new();
    private static bool _unlocked;
    private static int _fails;
    private static DateTime _blockedUntil = DateTime.MinValue;

    private static string Hash(string pin, string saltHex)
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin ?? string.Empty),
            Convert.FromHexString(saltHex),
            Iterations,
            HashAlgorithmName.SHA256,
            32);
        return Convert.ToHexString(derived).ToLowerInvariant();
    }

    public static bool IsEnabled => Db.GetSetting("pin_enabled") == "1";

    public static int PinLength =>
        int.TryParse(Db.GetSetting("pin_length"), out var length) ? length : 0;

    public static bool IsLocked
    {
        get
        {
            lock (Gate) return IsEnabled && !_unlocked;
        }
    }

    private static double BlockedFor()
    {
        var remaining = (_blockedUntil - DateTime.UtcNow).TotalSeconds;
        return remaining > 0 ? Math.Round(remaining, 1) : 0;
    }

    public static bool Verify(string pin)
    {
        var salt = Db.GetSetting("pin_salt");
        var stored = Db.GetSetting("pin_hash");
        if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(stored)) return false;

        try
        {
            var computed = Encoding.ASCII.GetBytes(Hash(pin, salt));
            var expected = Encoding.ASCII.GetBytes(stored.ToLowerInvariant());
            return CryptographicOperations.FixedTimeEquals(computed, expected);
        }
        catch
        {
            return false;
        }
    }

    public static object Unlock(string pin)
    {
        lock (Gate)
        {
            if (BlockedFor() > 0) return new { ok = false, wait = BlockedFor() };

            if (Verify(pin))
            {
                _unlocked = true;
                _fails = 0;
                _blockedUntil = DateTime.MinValue;
                return new { ok = true };
            }

            _fails++;
            if (_fails >= FailThreshold)
            {
                // Back off harder the longer someone keeps guessing.
                var over = _fails - FailThreshold;
                _blockedUntil = DateTime.UtcNow.AddSeconds(FailDelaySeconds * (1 + over));
            }
            return new { ok = false, wait = BlockedFor(), attempts = _fails };
        }
    }

    public static void LockNow()
    {
        lock (Gate) _unlocked = false;
    }

    /// <summary>Returns an error message, or null on success.</summary>
    public static string SetPin(string pin)
    {
        pin = (pin ?? string.Empty).Trim();
        if (pin.Length < MinLength || pin.Length > MaxLength || !pin.All(char.IsAsciiDigit))
            return $"The PIN must be {MinLength} to {MaxLength} digits.";

        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Db.SetSetting("pin_salt", salt);
        Db.SetSetting("pin_hash", Hash(pin, salt));
        Db.SetSetting("pin_length", pin.Length);
        Db.SetSetting("pin_enabled", "1");

        lock (Gate)
        {
            _unlocked = true;
            _fails = 0;
            _blockedUntil = DateTime.MinValue;
        }
        return null;
    }

    public static void ClearPin()
    {
        Db.DeleteSettings("pin_salt", "pin_hash", "pin_length");
        Db.SetSetting("pin_enabled", "0");
        lock (Gate)
        {
            _unlocked = true;
            _fails = 0;
            _blockedUntil = DateTime.MinValue;
        }
    }

    public static object Status()
    {
        lock (Gate)
        {
            return new
            {
                enabled = IsEnabled,
                locked = IsEnabled && !_unlocked,
                length = PinLength,
                wait = BlockedFor()
            };
        }
    }
}

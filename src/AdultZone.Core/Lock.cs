using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdultZone.Core.Data;

namespace AdultZone.Core;

/// <summary>
/// The PIN screen lock. The PIN is kept as a salted PBKDF2 hash, the same
/// form 1.x and 2.x used, so an existing PIN keeps working. Unlocking lasts
/// until the app closes or is locked again.
///
/// It is not encryption: it keeps the library off the screen, not off the disk.
/// </summary>
public static class Lock
{
    const int Iterations = 200_000;
    public const int MinLength = 4;
    public const int MaxLength = 8;
    const int FailThreshold = 5;
    const double FailDelay = 20;

    static bool _unlocked;
    static int _fails;
    static DateTime _blockedUntil = DateTime.MinValue;

    public static bool Enabled => Db.Setting("pin_enabled") == "1" && Db.Setting("pin_hash").Length > 0;
    public static bool Locked => Enabled && !_unlocked;
    public static int Length => Db.SettingInt("pin_length", 4);
    public static bool LengthKnown => Db.Setting("pin_length").Length > 0;
    public static double WaitSeconds => Math.Max(0, (_blockedUntil - DateTime.UtcNow).TotalSeconds);

    static string Hash(string pin, string saltHex) =>
        Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), Convert.FromHexString(saltHex),
                                                      Iterations, HashAlgorithmName.SHA256, 32)).ToLowerInvariant();

    public static bool Verify(string pin)
    {
        var salt = Db.Setting("pin_salt");
        var stored = Db.Setting("pin_hash");
        if (salt.Length == 0 || stored.Length == 0) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(pin, salt)),
                                                           Encoding.ASCII.GetBytes(stored.ToLowerInvariant()));
        }
        catch (FormatException) { return false; }
    }

    /// <summary>True when the PIN opened the lock. After five wrong tries each further one waits longer.</summary>
    public static bool Unlock(string pin)
    {
        if (WaitSeconds > 0) return false;
        if (Verify(pin))
        {
            _unlocked = true;
            _fails = 0;
            _blockedUntil = DateTime.MinValue;
            return true;
        }
        _fails++;
        if (_fails >= FailThreshold)
            _blockedUntil = DateTime.UtcNow.AddSeconds(FailDelay * (1 + _fails - FailThreshold));
        return false;
    }

    public static void LockNow() => _unlocked = false;

    public static bool Valid(string pin) => pin.Length is >= MinLength and <= MaxLength && pin.All(char.IsAsciiDigit);

    public static void SetPin(string pin)
    {
        if (!Valid(pin)) throw new ArgumentException($"The PIN must be {MinLength} to {MaxLength} digits.");
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Db.SetSetting("pin_salt", salt);
        Db.SetSetting("pin_hash", Hash(pin, salt));
        Db.SetSetting("pin_length", pin.Length.ToString(CultureInfo.InvariantCulture));
        Db.SetSetting("pin_enabled", "1");
        _unlocked = true;
        _fails = 0;
    }

    public static void Clear()
    {
        foreach (var key in new[] { "pin_salt", "pin_hash", "pin_length" }) Db.DeleteSetting(key);
        Db.SetSetting("pin_enabled", "0");
        _unlocked = true;
    }
}

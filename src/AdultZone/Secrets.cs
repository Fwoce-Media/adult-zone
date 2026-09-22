using System.Text.Json;

namespace AdultZone;

/// <summary>
/// The PIN and the ThePornDB key, kept apart from the library.
///
/// The library lives beside the program so the whole thing can be moved or
/// copied as one folder. These stay in %USERPROFILE%\.adultzone instead, so
/// copying the program folder elsewhere never carries a PIN hash or an API
/// key with it.
///
/// Also holds a pointer to where the library is, so a build run from Visual
/// Studio opens the same library as the installed program.
/// </summary>
public static class Secrets
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "pin_salt", "pin_hash", "pin_length", "pin_enabled", "tpdb_key", "tpdb_base"
    };

    private static readonly object Gate = new();
    private static Dictionary<string, string> _values;

    public static bool IsSecret(string key) => SecretKeys.Contains(key ?? "");

    public static IEnumerable<string> AllSecretKeys => SecretKeys;

    private static string FilePath => Path.Combine(AppPaths.SecretsDir, "secrets.json");

    private static Dictionary<string, string> Values()
    {
        if (_values != null) return _values;
        try
        {
            if (File.Exists(FilePath))
            {
                _values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                          ?? new Dictionary<string, string>();
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[secrets] could not read {FilePath}: {ex.Message}");
        }
        _values ??= new Dictionary<string, string>();
        return _values;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SecretsDir);
            // Write to a temporary file first so a crash never leaves it half written.
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[secrets] could not save {FilePath}: {ex.Message}");
        }
    }

    public static string Get(string key)
    {
        lock (Gate) return Values().TryGetValue(key, out var value) ? value : null;
    }

    public static bool Has(string key)
    {
        lock (Gate) return Values().ContainsKey(key);
    }

    public static void Set(string key, string value)
    {
        lock (Gate)
        {
            Values()[key] = value ?? "";
            Save();
        }
    }

    public static void Remove(params string[] keys)
    {
        lock (Gate)
        {
            var changed = false;
            foreach (var key in keys) changed |= Values().Remove(key);
            if (changed) Save();
        }
    }
}

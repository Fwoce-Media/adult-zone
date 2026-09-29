using System.Globalization;

namespace AdultZone.Core.Data;

/// <summary>
/// The library database. The tables are exactly 1.x's, so an existing library
/// opens as it is; anything newer is added with ALTER TABLE.
/// </summary>
public static class Db
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE IF NOT EXISTS locations (
            id       INTEGER PRIMARY KEY,
            path     TEXT UNIQUE NOT NULL,
            label    TEXT,
            enabled  INTEGER NOT NULL DEFAULT 1,
            added_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS studios (
            id          INTEGER PRIMARY KEY,
            name        TEXT UNIQUE NOT NULL,
            description TEXT DEFAULT '',
            image       TEXT
        );

        CREATE TABLE IF NOT EXISTS actors (
            id          INTEGER PRIMARY KEY,
            name        TEXT UNIQUE NOT NULL,
            description TEXT DEFAULT '',
            birthdate   TEXT,
            age         INTEGER,
            image       TEXT
        );

        CREATE TABLE IF NOT EXISTS tags (
            id   INTEGER PRIMARY KEY,
            name TEXT UNIQUE NOT NULL
        );

        CREATE TABLE IF NOT EXISTS videos (
            id            INTEGER PRIMARY KEY,
            path          TEXT UNIQUE NOT NULL,
            location_id   INTEGER REFERENCES locations(id) ON DELETE SET NULL,
            title         TEXT NOT NULL,
            description   TEXT DEFAULT '',
            studio_id     INTEGER REFERENCES studios(id) ON DELETE SET NULL,
            release_date  TEXT,
            duration      REAL DEFAULT 0,
            width         INTEGER DEFAULT 0,
            height        INTEGER DEFAULT 0,
            filesize      INTEGER DEFAULT 0,
            views         INTEGER NOT NULL DEFAULT 0,
            favorite      INTEGER NOT NULL DEFAULT 0,
            rating        INTEGER DEFAULT 0,
            thumb         TEXT,
            thumb_custom  INTEGER NOT NULL DEFAULT 0,
            preview       TEXT,
            added_at      TEXT NOT NULL DEFAULT (datetime('now')),
            last_played   TEXT,
            missing       INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS video_actors (
            video_id INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
            actor_id INTEGER NOT NULL REFERENCES actors(id) ON DELETE CASCADE,
            PRIMARY KEY (video_id, actor_id)
        );

        CREATE TABLE IF NOT EXISTS video_tags (
            video_id INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
            tag_id   INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            PRIMARY KEY (video_id, tag_id)
        );

        CREATE INDEX IF NOT EXISTS idx_videos_added   ON videos(added_at DESC);
        CREATE INDEX IF NOT EXISTS idx_videos_views   ON videos(views DESC);
        CREATE INDEX IF NOT EXISTS idx_videos_studio  ON videos(studio_id);
        CREATE INDEX IF NOT EXISTS idx_va_actor       ON video_actors(actor_id);
        CREATE INDEX IF NOT EXISTS idx_vt_tag         ON video_tags(tag_id);
        """;

    static readonly (string Table, string Column, string Decl)[] Migrations =
    {
        ("videos", "preview_width", "INTEGER DEFAULT 0"),
        ("videos", "subsite", "TEXT"),
        ("videos", "position", "REAL DEFAULT 0"),
        ("actors", "banner", "TEXT"),
        ("actors", "source", "TEXT"),
        ("actors", "country", "TEXT"),
        ("actors", "status", "TEXT"),
        ("actors", "hidden", "INTEGER NOT NULL DEFAULT 0"),
        ("actors", "gender", "TEXT"),
        ("actors", "birthplace", "TEXT"),
        ("actors", "ethnicity", "TEXT"),
        ("actors", "hair", "TEXT"),
        ("actors", "eyes", "TEXT"),
        ("actors", "height", "TEXT"),
        ("actors", "weight", "TEXT"),
        ("actors", "measurements", "TEXT"),
        ("actors", "cupsize", "TEXT"),
        ("actors", "tattoos", "TEXT"),
        ("actors", "piercings", "TEXT"),
        ("actors", "fake_boobs", "TEXT"),
        ("actors", "career", "TEXT"),
        ("actors", "astrology", "TEXT"),
        ("actors", "scraped", "INTEGER NOT NULL DEFAULT 0"),
        ("videos", "scraped", "INTEGER NOT NULL DEFAULT 0"),
        ("videos", "kind", "TEXT NOT NULL DEFAULT 'scene'"),
        ("videos", "cover", "TEXT"),
        ("locations", "kind", "TEXT NOT NULL DEFAULT 'scene'"),
        ("studios", "source", "TEXT"),
        ("studios", "logo_fit", "TEXT"),
        ("studios", "logo_zoom", "INTEGER"),
        ("studios", "logo_x", "INTEGER"),
        ("studios", "logo_y", "INTEGER"),
        ("studios", "logo_bg", "TEXT"),
    };

    public static readonly Dictionary<string, string> DefaultSettings = new()
    {
        ["preview_quality"] = "high",
        ["folder_as_studio"] = "1",
        ["two_part_actor"] = "1",
        ["skip_seconds"] = "10",
        ["cast_order"] = "alpha",
        ["home_rows"] = "latest,movies,popular,stars,studios,random",
        ["volume"] = "100",
    };

    static SqliteDb? _conn;

    public static SqliteDb Conn => _conn ?? throw new InvalidOperationException("Db.Init() has not run");

    public static void Init()
    {
        Config.EnsureFolders();
        _conn ??= new SqliteDb(Config.DbPath);
        Conn.Exec("PRAGMA journal_mode=WAL;");
        Conn.Exec("PRAGMA foreign_keys=ON;");
        Conn.Exec(Schema);
        foreach (var (table, column, decl) in Migrations) AddColumn(table, column, decl);
        CarryOverSecrets();
    }

    static void AddColumn(string table, string column, string decl)
    {
        var have = Conn.Query($"PRAGMA table_info({table})").Any(r => r.Str("name") == column);
        if (!have) Conn.Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl};");
    }

    /// <summary>2.x's PIN and API key, moved into the library once.</summary>
    static void CarryOverSecrets()
    {
        if (Setting("secrets_carried") == "1") return;
        var secrets = Config.TwoDotXSecrets();
        foreach (var key in new[] { "pin_salt", "pin_hash", "pin_length", "pin_enabled", "tpdb_key", "tpdb_base" })
            if (secrets.TryGetValue(key, out var value) && value.Length > 0 && Setting(key).Length == 0)
                SetSetting(key, value);
        SetSetting("secrets_carried", "1");
    }

    public static List<Row> Query(string sql, params object?[] args) => Conn.Query(sql, args);
    public static Row? QueryOne(string sql, params object?[] args) => Conn.QueryOne(sql, args);
    public static int Execute(string sql, params object?[] args) => Conn.Execute(sql, args);
    public static long Insert(string sql, params object?[] args) => Conn.Insert(sql, args);

    public static long Count(string sql, params object?[] args) => QueryOne(sql, args)?.Long("c") ?? 0;

    static readonly object TxGate = new();

    /// <summary>Runs <paramref name="work"/> as one transaction; one at a time across threads.</summary>
    public static void InTransaction(Action work)
    {
        lock (TxGate)
        {
            Conn.Exec("BEGIN;");
            try
            {
                work();
                Conn.Exec("COMMIT;");
            }
            catch
            {
                Conn.Exec("ROLLBACK;");
                throw;
            }
        }
    }

    /// <summary>The id of a named studio, actor or tag, made if new. Null for a blank name.</summary>
    public static long? GetOrCreate(string table, string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return null;
        var row = QueryOne($"SELECT id FROM {table} WHERE name = ? COLLATE NOCASE", name);
        if (row != null) return row.Long("id");
        return Insert($"INSERT INTO {table} (name) VALUES (?)", name);
    }

    // ------------------------------------------------------------- settings
    public static event Action? SettingsChanged;

    public static string Setting(string key) =>
        QueryOne("SELECT value FROM settings WHERE key = ?", key)?.Str("value")
        ?? (DefaultSettings.TryGetValue(key, out var d) ? d : "");

    public static bool SettingOn(string key) => Setting(key) is "1" or "true" or "True";

    public static int SettingInt(string key, int fallback) =>
        int.TryParse(Setting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public static void SetSetting(string key, string value)
    {
        Execute("INSERT INTO settings(key, value) VALUES (?, ?) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value);
        SettingsChanged?.Invoke();
    }

    public static void SetSetting(string key, bool on) => SetSetting(key, on ? "1" : "0");

    public static void DeleteSetting(string key) => Execute("DELETE FROM settings WHERE key = ?", key);
}

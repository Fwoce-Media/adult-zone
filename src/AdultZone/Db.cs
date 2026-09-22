using Microsoft.Data.Sqlite;

namespace AdultZone;

/// <summary>
/// The library database. The schema matches the Python build exactly, so an
/// existing library.db opens without migration or conversion.
/// </summary>
public static class Db
{
    public static string ConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

    public static SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=8000;";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }

    private const string Schema = @"
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

CREATE INDEX IF NOT EXISTS idx_videos_added  ON videos(added_at DESC);
CREATE INDEX IF NOT EXISTS idx_videos_views  ON videos(views DESC);
CREATE INDEX IF NOT EXISTS idx_videos_studio ON videos(studio_id);
CREATE INDEX IF NOT EXISTS idx_va_actor      ON video_actors(actor_id);
CREATE INDEX IF NOT EXISTS idx_vt_tag        ON video_tags(tag_id);
";

    /// <summary>Columns added after the original schema, matching the Python build.</summary>
    private static readonly (string Table, string Column, string Definition)[] Migrations =
    {
        ("videos",  "preview_width", "INTEGER DEFAULT 0"),
        ("videos",  "subsite",       "TEXT"),
        ("actors",  "banner",        "TEXT"),
        ("actors",  "source",        "TEXT"),
        ("actors",  "country",       "TEXT"),
        ("actors",  "status",        "TEXT"),
        ("actors",  "hidden",        "INTEGER NOT NULL DEFAULT 0"),
        ("studios", "source",        "TEXT"),
        ("studios", "logo_fit",      "TEXT"),
        ("studios", "logo_zoom",     "INTEGER"),
        ("studios", "logo_x",        "INTEGER"),
        ("studios", "logo_y",        "INTEGER"),
        ("studios", "logo_bg",       "TEXT")
    };

    public static void Init()
    {
        AppPaths.EnsureFolders();
        using var connection = Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = Schema;
            create.ExecuteNonQuery();
        }

        foreach (var (table, column, definition) in Migrations)
        {
            if (HasColumn(connection, table, column)) continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            alter.ExecuteNonQuery();
        }

        LiftSecrets(connection);
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------ helpers

    private static void Bind(SqliteCommand command, object[] parameters)
    {
        for (var i = 0; i < parameters.Length; i++)
            command.Parameters.AddWithValue($"@p{i}", parameters[i] ?? DBNull.Value);
    }

    /// <summary>Every row as a dictionary, which is what the JSON layer wants.</summary>
    public static List<Dictionary<string, object>> Query(string sql, params object[] parameters)
    {
        using var connection = Open();
        return Query(connection, sql, parameters);
    }

    public static List<Dictionary<string, object>> Query(SqliteConnection connection, string sql,
                                                         params object[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);

        var rows = new List<Dictionary<string, object>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public static Dictionary<string, object> QueryOne(string sql, params object[] parameters)
    {
        var rows = Query(sql, parameters);
        return rows.Count > 0 ? rows[0] : null;
    }

    public static Dictionary<string, object> QueryOne(SqliteConnection connection, string sql,
                                                      params object[] parameters)
    {
        var rows = Query(connection, sql, parameters);
        return rows.Count > 0 ? rows[0] : null;
    }

    public static int Execute(string sql, params object[] parameters)
    {
        using var connection = Open();
        return Execute(connection, sql, parameters);
    }

    public static int Execute(SqliteConnection connection, string sql, params object[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Row id of the last insert on this connection.</summary>
    public static long Insert(SqliteConnection connection, string sql, params object[] parameters)
    {
        Execute(connection, sql, parameters);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public static long Scalar(string sql, params object[] parameters)
    {
        using var connection = Open();
        return Scalar(connection, sql, parameters);
    }

    public static long Scalar(SqliteConnection connection, string sql, params object[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        var result = command.ExecuteScalar();
        return result is null || result is DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>Find a named row in studios/actors/tags, creating it when new.</summary>
    public static long GetOrCreate(SqliteConnection connection, string table, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return 0;

        var existing = QueryOne(connection, $"SELECT id FROM {table} WHERE name = @p0 COLLATE NOCASE", name);
        if (existing != null) return Convert.ToInt64(existing["id"]);

        return Insert(connection, $"INSERT INTO {table} (name) VALUES (@p0)", name);
    }

    // The PIN and API key live in Secrets, outside the library folder; every
    // other setting lives in the database. Callers need not know which.

    public static string GetSetting(string key, string fallback = null)
    {
        if (Secrets.IsSecret(key)) return Secrets.Get(key) ?? fallback;
        var row = QueryOne("SELECT value FROM settings WHERE key = @p0", key);
        return row?["value"] as string ?? fallback;
    }

    public static void SetSetting(string key, object value)
    {
        if (Secrets.IsSecret(key))
        {
            Secrets.Set(key, value?.ToString() ?? string.Empty);
            return;
        }
        Execute("INSERT INTO settings (key, value) VALUES (@p0, @p1) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                key, value?.ToString() ?? string.Empty);
    }

    public static void DeleteSettings(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (Secrets.IsSecret(key)) Secrets.Remove(key);
            else Execute("DELETE FROM settings WHERE key = @p0", key);
        }
    }

    /// <summary>
    /// Move any PIN or API key still sitting in the database out to Secrets.
    /// Libraries written by the Python build, or by an earlier C# build, keep
    /// them in the settings table.
    /// </summary>
    private static void LiftSecrets(SqliteConnection connection)
    {
        foreach (var key in Secrets.AllSecretKeys)
        {
            var row = QueryOne(connection, "SELECT value FROM settings WHERE key = @p0", key);
            if (row is null) continue;

            if (!Secrets.Has(key)) Secrets.Set(key, row["value"] as string ?? "");
            Execute(connection, "DELETE FROM settings WHERE key = @p0", key);
        }
    }
}

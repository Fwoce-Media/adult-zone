using Microsoft.Data.Sqlite;

namespace AdultZone;

/// <summary>
/// Moves an existing library from %USERPROFILE%\.adultzone to the Data folder
/// beside the program, the first time the installed program runs.
///
/// It only ever runs when the new location has no library yet, so it can never
/// overwrite one. Where a folder cannot be moved — across drives, say — it is
/// copied instead and the original is left exactly where it was.
/// </summary>
public static class DataMove
{
    private static readonly string[] Files = { "library.db", "library.db-wal", "library.db-shm", "adult-zone.log" };
    private static readonly string[] Folders = { "cache", "images", "webview" };

    public static void Run()
    {
        // A portable copy keeps to its own folder and leaves the machine alone.
        if (AppPaths.IsPortable) return;

        var from = AppPaths.LegacyHome;
        var to = AppPaths.Home;

        if (string.Equals(Path.GetFullPath(from).TrimEnd('\\'), Path.GetFullPath(to).TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(Path.Combine(to, "library.db"))) return;      // already has one
        if (!File.Exists(Path.Combine(from, "library.db"))) return;   // nothing to move

        // Everything moved so far, so a failure part-way can be put back.
        var moved = new List<(string From, string To, bool IsFolder)>();
        var copied = new List<string>();

        try
        {
            Directory.CreateDirectory(to);
            FlushWriteAheadLog(Path.Combine(from, "library.db"));

            // Folders first and the database last: until library.db arrives,
            // the new location does not count as having a library.
            foreach (var name in Folders)
            {
                var source = Path.Combine(from, name);
                if (!Directory.Exists(source)) continue;
                var target = Path.Combine(to, name);
                try
                {
                    Directory.Move(source, target);
                    moved.Add((source, target, true));
                }
                catch (IOException)
                {
                    // Different drive: copy, and keep the original as it was.
                    CopyFolder(source, target);
                    copied.Add(name);
                }
            }

            foreach (var name in Files)
            {
                var source = Path.Combine(from, name);
                if (!File.Exists(source)) continue;
                var target = Path.Combine(to, name);
                File.Move(source, target);
                moved.Add((source, target, false));
            }

            File.WriteAllText(Path.Combine(from, "MOVED.txt"),
                $"Adult Zone moved its library on {DateTime.Now:yyyy-MM-dd HH:mm}.{Environment.NewLine}" +
                $"It now lives in:{Environment.NewLine}  {to}{Environment.NewLine}{Environment.NewLine}" +
                $"This folder keeps only secrets.json, which holds your PIN and API key.{Environment.NewLine}" +
                (copied.Count > 0
                    ? $"These were copied rather than moved, and can be deleted here: {string.Join(", ", copied)}{Environment.NewLine}"
                    : ""));

            AppPaths.Log($"Library moved from {from} to {to}");
        }
        catch (Exception ex)
        {
            // Put back whatever already moved, newest first, then carry on
            // from the old location as if nothing had happened.
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                var (original, current, isFolder) = moved[i];
                try
                {
                    if (isFolder) Directory.Move(current, original);
                    else File.Move(current, original);
                }
                catch (Exception undo)
                {
                    AppPaths.Log($"[move] could not put back {current}: {undo.Message}");
                }
            }
            AppPaths.UseLegacyHome();

            AppPaths.Log($"[move] {ex}");
            System.Windows.Forms.MessageBox.Show(
                "Adult Zone could not move your library to its new folder, so it has " +
                $"left everything where it was.\n\n{ex.Message}\n\n" +
                "Your library is safe and opens as normal. It will try again next time.",
                AppPaths.AppName,
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Fold the write-ahead log into the main file and release every handle,
    /// so the database can be moved as one complete file.
    /// </summary>
    private static void FlushWriteAheadLog(string databasePath)
    {
        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                command.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[move] checkpoint: {ex.Message}");
        }
        finally
        {
            // Pooled connections keep the file open; drop them before moving it.
            SqliteConnection.ClearAllPools();
        }
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var folder in Directory.EnumerateDirectories(source))
            CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
    }
}

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdultZone.Core.Data;
using AdultZone.Core.Library;

namespace AdultZone.Desktop;

/// <summary>
/// The right-click menu on a video: fetch details, favourite, its studio, its
/// cast (a side menu, however many there are), reset what was fetched, delete the file.
/// </summary>
public static class VideoMenu
{
    public static void Attach(FrameworkElement target, MainWindow win, Row v)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => Fill(menu, win, v);
        target.ContextMenu = menu;
    }

    static MenuItem Item(string text, Action? click = null)
    {
        var item = new MenuItem { Header = text };
        if (click != null) item.Click += (_, _) => click();
        return item;
    }

    static void Fill(ContextMenu menu, MainWindow win, Row v)
    {
        var id = v.Long("id") ?? 0;
        var title = v.Str("title");
        var cast = Catalog.Cast(v);
        var favorite = v.Truthy("favorite");
        menu.Items.Clear();
        menu.Items.Add(Item("Fetch details", () => ImportDialog.Show(win, "video", id, title,
            new ImportDialog.Hints(v.Str("studio_name"), v.Str("subsite"), cast.FirstOrDefault()?.Str("name") ?? ""))));
        menu.Items.Add(Item(favorite ? "Remove from favourites" : "Add to favourites", () =>
        {
            Catalog.SetFavorite(id, !favorite);
            win.Refresh(keepScroll: true);
        }));
        if (v.Long("studio_id") is long studio && studio > 0)
            menu.Items.Add(Item("Go to studio", () => win.Navigate(new Location("studio", studio))));
        if (cast.Count == 1)
        {
            var only = cast[0].Long("id") ?? 0;
            menu.Items.Add(Item("Go to " + cast[0].Str("name"), () => win.Navigate(new Location("actor", only))));
        }
        else if (cast.Count > 1)
        {
            var people = Item("Go to actors");
            foreach (var a in cast)
            {
                var actor = a.Long("id") ?? 0;
                people.Items.Add(Item(a.Str("name"), () => win.Navigate(new Location("actor", actor))));
            }
            menu.Items.Add(people);
        }
        menu.Items.Add(Item("Reset scrape", () =>
        {
            if (!Dialogs.Confirm(win, $"Reset “{title}” to its file name?", "Reset")) return;
            Scanner.Reset(id);
            Images.Clear();
            win.WatchScan();
            win.Refresh(keepScroll: true);
        }));
        menu.Items.Add(Item("Delete video", () =>
        {
            // Shift held as it is clicked: gone for good, not to the Recycle Bin.
            var forever = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            Delete(win, id, v.Str("path"), title, forever);
        }));
    }

    static void Delete(MainWindow win, long id, string path, string title, bool forever)
    {
        if (!Dialogs.Confirm(win, forever ? $"Permanently delete “{title}”?" : $"Send “{title}” to the Recycle Bin?", "Delete")) return;
        try
        {
            if (File.Exists(path))
            {
                if (forever) File.Delete(path);
                else Recycle(path);
            }
        }
        catch (Exception ex)
        {
            win.Toast("Could not delete the file: " + ex.Message, true);
            return;
        }
        Catalog.RemoveVideo(id);
        win.Toast(forever ? "Video deleted" : "Video sent to the Recycle Bin");
        win.Refresh(keepScroll: true);
    }

    // ------------------------------------------------------------ recycle bin
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    static void Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted) throw new IOException($"Windows refused (code {result}).");
        if (File.Exists(path)) throw new IOException("the file is still there.");
    }
}

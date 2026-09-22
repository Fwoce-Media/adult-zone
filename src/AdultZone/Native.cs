using System.Runtime.InteropServices;

namespace AdultZone;

/// <summary>
/// Small pieces of Windows integration that make the window feel like a normal
/// desktop program rather than a hosted web page.
/// </summary>
internal static class Native
{
    // Windows 11, and Windows 10 from build 18985. The older builds used 19.
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
                                                    ref int value, int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string id);

    /// <summary>
    /// Paint the title bar dark, so it matches the app instead of sitting on a
    /// white strip above it. Silently does nothing on Windows versions that
    /// do not support it.
    /// </summary>
    public static void UseDarkTitleBar(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        var on = 1;
        try
        {
            if (DwmSetWindowAttribute(window, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(window, DwmUseImmersiveDarkModeOld, ref on, sizeof(int));
        }
        catch
        {
            // Older Windows: the title bar stays light, nothing else changes.
        }
    }

    /// <summary>
    /// Give the process its own identity, so Windows groups and pins it as
    /// Adult Zone rather than lumping it in with other .NET applications.
    /// </summary>
    public static void SetAppId(string id = "Fwoce.AdultZone")
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(id);
        }
        catch
        {
            // Not fatal; only affects taskbar grouping.
        }
    }
}

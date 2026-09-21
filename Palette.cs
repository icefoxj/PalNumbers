using System.Runtime.InteropServices;

namespace PalNumbers;

// ANSI colours for the console output.
//
// The escape sequences travel in the text stream itself rather than through the
// Console.ForegroundColor API: output is buffered, so changing the colour
// through the API would take effect immediately while the text was still
// queued, painting the wrong lines.
//
// Colours switch off when output is redirected, so files and pipes do not get
// littered with escape codes. NO_COLOR turns them off and FORCE_COLOR turns
// them on, following the convention most command-line tools use.
internal static class Palette
{
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    public static bool Enabled { get; }

    public static readonly string Reset;
    public static readonly string Title;
    public static readonly string Label;
    public static readonly string Rule;
    public static readonly string Number;
    public static readonly string Palindrome;
    public static readonly string Iterations;
    public static readonly string Warning;
    public static readonly string Muted;
    public static readonly string Highlight;
    public static readonly string Border;

    static Palette()
    {
        Enabled = Decide();

        if (Enabled && OperatingSystem.IsWindows())
        {
            EnableVirtualTerminal();
        }

        Reset      = Paint("\e[0m");
        Title      = Paint("\e[1;96m");     // bright cyan, bold
        Label      = Paint("\e[96m");       // cyan
        Rule       = Paint("\e[90m");       // grey
        Number     = Paint("\e[97m");       // bright white
        Palindrome = Paint("\e[92m");       // green
        Iterations = Paint("\e[93m");       // yellow
        Warning    = Paint("\e[91m");       // red
        Muted      = Paint("\e[90m");       // grey
        Highlight  = Paint("\e[1;30;102m"); // black on green, bold
        Border     = Paint("\e[1;92m");     // bright green, bold
    }

    private static string Paint(string sequence) => Enabled ? sequence : string.Empty;

    private static bool Decide()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORCE_COLOR")))
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            return false;
        }

        return !Console.IsOutputRedirected;
    }

    private static void EnableVirtualTerminal()
    {
        nint handle = GetStdHandle(StandardOutputHandle);

        if (handle != 0 && handle != -1 && GetConsoleMode(handle, out uint mode))
        {
            SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
    }

    // Turns off QuickEdit selection on the console this process is attached to.
    //
    // With QuickEdit on — the default in the classic Windows console — a click
    // in the window starts a selection and the system suspends output: the next
    // write blocks until the selection is cleared. For a program that prints
    // from its main loop that is not a paused display, it is a paused program.
    // The whole sequence stops mid-block, and even Ctrl+C has no effect,
    // because the thread that would notice the cancellation is the one stuck
    // writing. Seen in production: a stray click froze a run for hours while the
    // reprocessing threads, which never touch the console, carried on.
    //
    // The cost is losing mouse selection in that window. Windows Terminal has
    // its own selection, which does not suspend the program.
    public static void PreventOutputFreeze()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
        {
            return;
        }

        nint handle = GetStdHandle(StandardInputHandle);

        if (handle != 0 && handle != -1 && GetConsoleMode(handle, out uint mode))
        {
            // ENABLE_EXTENDED_FLAGS has to go along, or clearing QuickEdit is
            // ignored.
            SetConsoleMode(handle, (mode & ~EnableQuickEditMode) | EnableExtendedFlags);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}

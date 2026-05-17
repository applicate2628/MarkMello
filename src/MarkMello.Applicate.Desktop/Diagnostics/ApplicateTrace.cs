using System.Diagnostics;

namespace MarkMello.Applicate.Desktop.Diagnostics;

internal static class ApplicateTrace
{
    [Conditional("DEBUG")]
    public static void ModeToggle(string message)
        => Console.Error.WriteLine($"[mode-toggle] {DateTime.Now:HH:mm:ss.fff} {message}");
}

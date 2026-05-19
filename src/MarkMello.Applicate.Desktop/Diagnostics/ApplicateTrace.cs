using System.Diagnostics;

namespace MarkMello.Applicate.Desktop.Diagnostics;

internal static class ApplicateTrace
{
    [Conditional("DEBUG")]
    public static void ModeToggle(string message)
        => Console.Error.WriteLine($"[mode-toggle] {DateTime.Now:HH:mm:ss.fff} {message}");

    /// <summary>
    /// Always-on (Release-safe) diagnostic emitter for short-lived bug-hunting
    /// runs. Retained as a general-purpose facility per design D9; per-bug
    /// call sites must be removed in the same commit cycle as the fix
    /// (bug-hunting Rule 7). Tags reserved for future use include
    /// <c>[HOST]</c> and <c>[FAILURE]</c>; categories are not pre-defined.
    /// </summary>
    public static void Diag(string tag, string evt, string fields = "")
        => Console.Error.WriteLine($"[{tag} {DateTime.Now:HH:mm:ss.fff}] {evt}{(fields.Length > 0 ? " " + fields : "")}");
}

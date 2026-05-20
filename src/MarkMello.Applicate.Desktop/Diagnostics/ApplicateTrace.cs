using System.Diagnostics;
using MarkMello.Domain.Diagnostics;

namespace MarkMello.Applicate.Desktop.Diagnostics;

internal static class ApplicateTrace
{
    /// <summary>
    /// Process-anchored stopwatch shared across layers (started inside
    /// <see cref="StartupMarkEmitter"/>'s static initializer). Re-exposed
    /// here so Applicate-overlay code can read elapsed-ms without taking a
    /// new dependency import in every call site.
    /// </summary>
    public static Stopwatch ProcessStart => StartupMarkEmitter.ProcessStart;

    /// <summary>
    /// Force the shared <see cref="StartupMarkEmitter.ProcessStart"/>
    /// stopwatch to begin now. Call from <c>Program.Main</c>'s first line so
    /// subsequent <see cref="DiagMs"/> markers measure from process start.
    /// </summary>
    public static void Touch() => StartupMarkEmitter.Touch();

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

    /// <summary>
    /// Emit a startup/perf marker anchored to <see cref="ProcessStart"/>.
    /// Output shape: <c>[group HH:mm:ss.fff] event ms=&lt;elapsed&gt;</c> —
    /// matching the round-2 perf-engineer plan acceptance contract
    /// "<c>&lt;group&gt; &lt;event&gt; ms=&lt;float&gt;</c>". Delegates to
    /// <see cref="StartupMarkEmitter.Emit"/> so the same Stopwatch and
    /// output format are shared across <c>Applicate.Desktop</c> and
    /// <c>Presentation</c> (which cannot reach this type directly).
    /// </summary>
    /// <param name="group">Marker group (e.g. <c>startup-pre-window</c>).</param>
    /// <param name="evt">Specific event name within the group.</param>
    /// <param name="extraFields">Optional additional <c>key=value</c> fields
    /// appended after <c>ms=&lt;elapsed&gt;</c>.</param>
    public static void DiagMs(string group, string evt, string extraFields = "")
        => StartupMarkEmitter.Emit(group, evt, extraFields);
}

using System.Diagnostics;

namespace MarkMello.Domain.Diagnostics;

/// <summary>
/// Process-anchored startup/perf marker emitter shared across layers.
///
/// One <see cref="Stopwatch"/> is started during type initialization so that
/// every marker recorded via <see cref="Emit"/> reports milliseconds since
/// the same origin — the moment any project first touches this type.
/// <c>Program.Main</c> calls <see cref="Touch"/> on its first line so the
/// origin coincides with process start (round-2 perf-engineer plan item C).
///
/// Multiple layers cannot reach the Applicate-overlay's <c>ApplicateTrace</c>
/// directly (assembly direction: Applicate -> Presentation -> Domain), so
/// the shared anchor and emit format live here in <c>Domain.Diagnostics</c>
/// and each layer ships a thin wrapper that delegates to <see cref="Emit"/>.
///
/// Output shape:
///     <c>[group HH:mm:ss.fff] event ms=&lt;elapsed&gt; [extraFields]</c>
/// — matching the existing <c>[startup] AppBootstrap 0.8 ms</c> /
/// <c>[startup] FirstWindow 1647.9 ms</c> pattern in cold-start logs while
/// adding the <c>group</c>/<c>event</c>/<c>ms=</c> shape required by the
/// round-2 plan acceptance contract ("<c>&lt;group&gt; &lt;event&gt; ms=&lt;float&gt;</c>").
/// </summary>
public static class StartupMarkEmitter
{
    /// <summary>
    /// Single process-anchored stopwatch. Started on first reference to this
    /// type (CLR static initializer guarantee).
    /// </summary>
    public static readonly Stopwatch ProcessStart = Stopwatch.StartNew();

    /// <summary>
    /// No-op call that forces this type's static initializer (and therefore
    /// <see cref="ProcessStart"/>) to run at the moment of the call. Invoke
    /// from <c>Program.Main</c>'s first line so subsequent <see cref="Emit"/>
    /// markers measure elapsed milliseconds from process start.
    /// </summary>
    public static void Touch()
    {
        // Body intentionally empty — referencing ProcessStart is sufficient.
        _ = ProcessStart;
    }

    /// <summary>
    /// Write a startup/perf marker to <see cref="System.Console.Error"/>.
    /// </summary>
    /// <param name="group">Marker group (e.g. <c>startup-pre-window</c>).</param>
    /// <param name="evt">Event within the group.</param>
    /// <param name="extraFields">Optional additional <c>key=value</c>
    /// fields appended after <c>ms=&lt;elapsed&gt;</c>.</param>
    public static void Emit(string group, string evt, string extraFields = "")
    {
        var ms = ProcessStart.Elapsed.TotalMilliseconds;
        var suffix = extraFields.Length > 0 ? $" ms={ms:F1} {extraFields}" : $" ms={ms:F1}";
        System.Console.Error.WriteLine(
            $"[{group} {System.DateTime.Now:HH:mm:ss.fff}] {evt}{suffix}");
    }
}

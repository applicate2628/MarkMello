using MarkMello.Domain.Diagnostics;

namespace MarkMello.Presentation.Diagnostics;

/// <summary>
/// Thin Presentation-layer wrapper around the shared
/// <see cref="StartupMarkEmitter"/> so <c>App.axaml.cs</c> and
/// <c>MainWindow.axaml.cs</c> can emit startup markers without taking a
/// dependency on Applicate-overlay diagnostics (assembly direction:
/// Applicate -&gt; Presentation -&gt; Domain).
/// </summary>
internal static class StartupDiag
{
    public static void DiagMs(string group, string evt, string extraFields = "")
        => StartupMarkEmitter.Emit(group, evt, extraFields);
}

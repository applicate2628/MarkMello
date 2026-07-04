using System;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Raised by the WebView document view when the user clicks a GFM task-list
/// checkbox. Carries the checkbox's 0-based source line, its identity key, and
/// the new checked state so the host can toggle <c>[ ]</c>/<c>[x]</c> in the
/// document — refusing when the key no longer matches the line (stale view).
/// </summary>
public sealed class ApplicateWebTaskToggleEventArgs(int line, bool isChecked, string? key) : EventArgs
{
    /// <summary>0-based source line of the task item's marker.</summary>
    public int Line { get; } = line;

    /// <summary>New checked state after the click.</summary>
    public bool Checked { get; } = isChecked;

    /// <summary>
    /// Identity key of the item's raw source line
    /// (<see cref="MarkMello.Domain.TaskListIdentity.ComputeKey"/>), or
    /// <c>null</c> when the renderer could not compute one — the write-back
    /// refuses a null key (fail-closed).
    /// </summary>
    public string? Key { get; } = key;
}

using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Table-of-Contents surface for the shell. The Applicate renderer scans
/// the rendered document for headings and pushes them through the
/// <c>headings-updated</c> IPC; the Applicate-side host view forwards the
/// list into <see cref="DocumentHeadings"/>. The shell binds a column to
/// this collection and exposes <see cref="IsTocVisible"/> as the
/// composite visibility predicate (viewer mode AND user preference AND
/// non-empty headings).
///
/// <para>Architectural note: the TOC lives at the shell level rather than
/// inside the renderer because the user wants a panel that spans the full
/// content-area height with its own scroll, resizable column, and that
/// hides in edit mode. A renderer-side TOC could not satisfy any of those
/// requirements without competing with the document body for layout.</para>
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTocVisible))]
    [NotifyPropertyChangedFor(nameof(HasDocumentHeadings))]
    private ObservableCollection<DocumentHeading> _documentHeadings = new();

    /// <summary>
    /// Currently active heading id reported by the renderer's
    /// IntersectionObserver. The TOC row whose <see cref="DocumentHeading.Id"/>
    /// equals this value paints with the "active" style.
    /// </summary>
    [ObservableProperty]
    private string _activeHeadingId = string.Empty;

    /// <summary>
    /// User-controlled TOC visibility preference. Toggled by the menu entry
    /// and (when added) the keyboard shortcut. Default true so first-time
    /// users see the TOC immediately when they open a document with headings.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTocVisible))]
    private bool _isTocPreferredVisible = true;

    /// <summary>
    /// Current width of the TOC column. Two-way-bound to the GridSplitter
    /// the Applicate-side ApplicateMainWindow installs. Held as a VM
    /// property so the value survives layout passes; settings persistence
    /// is a v0.3.3 backlog item.
    /// </summary>
    [ObservableProperty]
    private double _tocColumnWidth = 240.0;

    /// <summary>
    /// True when the TOC column should be visible. Composite predicate:
    /// the active document must be a viewer (not edit mode), the user
    /// must not have hidden the TOC, and the renderer must have reported
    /// at least one heading for the current document.
    /// </summary>
    public bool IsTocVisible
        => IsViewer
           && !IsEditMode
           && IsTocPreferredVisible
           && DocumentHeadings.Count > 0;

    public bool HasDocumentHeadings => DocumentHeadings.Count > 0;

    /// <summary>
    /// Toggle the TOC visibility preference. Bound to the menu entry
    /// "Table of contents". When the document has no headings the toggle
    /// still works but the column stays hidden because of
    /// <see cref="IsTocVisible"/>'s composite predicate.
    /// </summary>
    [RelayCommand]
    private void ToggleToc()
    {
        IsTocPreferredVisible = !IsTocPreferredVisible;
        // Close the app menu overlay so the user sees the column transition.
        if (IsAppOverlayOpen)
        {
            ShellOverlay = ShellOverlayKind.None;
        }
    }

    /// <summary>
    /// Request that the renderer scrolls the document to the heading
    /// identified by <paramref name="headingId"/>. The Applicate-side
    /// viewer subscribes to <see cref="ScrollToHeadingRequested"/> and
    /// forwards the request through the shared WebView host.
    /// </summary>
    [RelayCommand]
    private void ScrollToHeading(string? headingId)
    {
        if (string.IsNullOrEmpty(headingId))
        {
            return;
        }
        ScrollToHeadingRequested?.Invoke(this, headingId);
    }

    /// <summary>
    /// Request that the renderer toggles its in-document find bar. The
    /// Applicate-side viewer subscribes to <see cref="OpenFindBarRequested"/>
    /// and forwards the request through the shared WebView host.
    /// </summary>
    [RelayCommand]
    private void OpenFindBar()
    {
        OpenFindBarRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised by <see cref="ScrollToHeadingCommand"/>. Applicate-side
    /// surface subscribes and converts the request to a renderer IPC call.
    /// </summary>
    public event EventHandler<string>? ScrollToHeadingRequested;

    /// <summary>
    /// Raised by <see cref="OpenFindBarCommand"/>. Applicate-side surface
    /// subscribes and converts the request to a renderer IPC call.
    /// </summary>
    public event EventHandler? OpenFindBarRequested;

    /// <summary>
    /// Replace the heading collection with <paramref name="headings"/> and
    /// notify dependent computed properties. Called from the Applicate-side
    /// host adapter whenever the renderer reports a new heading list.
    /// </summary>
    public void UpdateDocumentHeadings(System.Collections.Generic.IReadOnlyList<DocumentHeading> headings)
    {
        ArgumentNullException.ThrowIfNull(headings);

        // ObservableCollection<T> raises CollectionChanged but NOT
        // PropertyChanged for our derived properties (IsTocVisible,
        // HasDocumentHeadings). Replace via clear+add inside one notification
        // so the GridSplitter / column visibility binding fires once after
        // the collection is fully populated.
        DocumentHeadings.Clear();
        foreach (var heading in headings)
        {
            DocumentHeadings.Add(heading);
        }
        OnPropertyChanged(nameof(IsTocVisible));
        OnPropertyChanged(nameof(HasDocumentHeadings));
        // Clear active heading id when the document changes; the
        // renderer's IntersectionObserver emits a fresh active-heading-
        // changed shortly after this on its first scroll.
        ActiveHeadingId = string.Empty;
    }

    // IsTocVisible propagation when IsEditMode / State changes lives in the
    // main MainWindowViewModel partial via OnIsEditModeChanged / OnStateChanged
    // (partial methods can only be implemented once across all partials of
    // the same class). The companion OnPropertyChanged(nameof(IsTocVisible))
    // calls are added there.
}

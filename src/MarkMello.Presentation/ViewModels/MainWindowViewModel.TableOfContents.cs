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
/// composite visibility predicate (open viewer surface AND user preference AND
/// non-empty headings).
///
/// <para>Architectural note: the TOC lives at the shell level rather than
/// inside the renderer because the user wants a panel that spans the full
/// content-area height with its own scroll and resizable column. A
/// renderer-side TOC could not satisfy those requirements without competing
/// with the document body for layout.</para>
/// </summary>
public partial class MainWindowViewModel
{
    private string? _pendingScrollToHeadingId;

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
    /// the active document must have a viewer surface, the user
    /// must not have hidden the TOC, and the renderer must have reported
    /// at least one heading for the current document.
    /// </summary>
    public bool IsTocVisible
        => IsViewer
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
        ActiveHeadingId = headingId;
        _pendingScrollToHeadingId = headingId;
        ScrollToHeadingRequested?.Invoke(this, headingId);
    }

    /// <summary>
    /// Apply the renderer's current active heading unless a user-initiated TOC
    /// click is still scrolling toward its requested target. Smooth scrolling
    /// can pass intermediate headings through the active-zone observer; those
    /// transient ids must not steal the visual selection from the row the user
    /// explicitly clicked.
    /// </summary>
    public void UpdateActiveHeadingFromRenderer(string? headingId)
    {
        if (string.IsNullOrEmpty(headingId))
        {
            return;
        }

        if (!string.IsNullOrEmpty(_pendingScrollToHeadingId)
            && !string.Equals(headingId, _pendingScrollToHeadingId, StringComparison.Ordinal))
        {
            return;
        }

        _pendingScrollToHeadingId = null;
        ActiveHeadingId = headingId;
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

        // Replace the collection wholesale so the TOC panel rebuilds rows once
        // via DocumentHeadings PropertyChanged. Mutating the existing
        // ObservableCollection per heading makes the panel handle N
        // CollectionChanged events and rebuild all rows on every item.
        DocumentHeadings = new ObservableCollection<DocumentHeading>(headings);
        // Clear active heading id when the document changes; the
        // renderer's IntersectionObserver emits a fresh active-heading-
        // changed shortly after this on its first scroll.
        _pendingScrollToHeadingId = null;
        ActiveHeadingId = string.Empty;
    }

    // IsTocVisible propagation when IsEditMode / State changes lives in the
    // main MainWindowViewModel partial via OnIsEditModeChanged / OnStateChanged
    // (partial methods can only be implemented once across all partials of
    // the same class). The companion OnPropertyChanged(nameof(IsTocVisible))
    // calls are added there.
}

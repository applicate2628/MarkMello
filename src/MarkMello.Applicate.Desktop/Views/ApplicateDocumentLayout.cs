using Avalonia;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Canonical layout constants and helpers for the document column hosted by
/// <see cref="ApplicateViewerView"/> and <see cref="ApplicateEditPreviewView"/>.
///
/// The two consumer surfaces previously embedded the same magic numbers
/// (scrollbar gutter literal, content-width floor, document horizontal padding
/// formula) inline; the audit memo at
/// <c>work-items/active/2026-05-19-renderer-flash-cleanup/audit.md</c> flagged
/// these as single-source-of-truth violations (F-01, F-02, F-06). This class
/// is the new owner. Adding a constant or method here keeps both consumers in
/// sync without per-call-site copies.
/// </summary>
internal static class ApplicateDocumentLayout
{
    /// <summary>
    /// Avalonia resource key that exposes the canonical scrollbar track width.
    /// Lives in <c>Themes/ApplicateScrollBars.axaml</c>; the same resource
    /// drives the ScrollBar style's Width / MinWidth so the slot reservation
    /// and the painted bar always agree.
    /// </summary>
    public const string ScrollBarSizeResourceKey = "ScrollBarSize";

    /// <summary>
    /// Fallback gutter width used when <see cref="ScrollBarSizeResourceKey"/>
    /// is not resolvable through the active application resources (typical
    /// in headless test environments that do not load
    /// <c>ApplicateScrollBars.axaml</c>). Matches the canonical resource value
    /// so the test and the runtime layouts collapse to the same width when
    /// the resource lookup succeeds.
    /// </summary>
    public const double DefaultScrollBarSize = 12.0;

    /// <summary>
    /// Lower bound for the user-controlled document content width. The
    /// width-resizer drag, the AvailableContentWidth clamp, and the renderer's
    /// own <c>minMaxWidth</c> all consume this value via the same constant,
    /// so the viewer slot and the renderer never disagree about the floor.
    /// </summary>
    public const double MinManualContentWidth = 320.0;

    /// <summary>
    /// Returns the canonical scrollbar gutter as a right-side
    /// <see cref="Thickness"/> the consumer can apply to the WebView slot.
    /// Reads <see cref="ScrollBarSizeResourceKey"/> from the active
    /// application resources; falls back to <see cref="DefaultScrollBarSize"/>
    /// when the lookup fails (headless tests, early-bootstrap call before
    /// Themes load).
    /// </summary>
    public static Thickness GetWebSlotScrollBarGutter()
        => new(0, 0, ResolveScrollBarSize(), 0);

    /// <summary>
    /// Resolves the canonical scrollbar size from the active application
    /// resources. Identical to reading
    /// <c>{DynamicResource ScrollBarSize}</c> in XAML but available from
    /// C#-only consumer constructors that have no XAML root.
    /// </summary>
    public static double ResolveScrollBarSize()
    {
        var resources = Avalonia.Application.Current?.Resources;
        if (resources is not null
            && resources.TryGetResource(ScrollBarSizeResourceKey, null, out var value)
            && value is double width
            && double.IsFinite(width)
            && width > 0)
        {
            return width;
        }

        return DefaultScrollBarSize;
    }

    /// <summary>
    /// Returns the canonical document column padding the edit-preview surface
    /// should apply: the horizontal sides come from
    /// <see cref="ReadingLayoutMetrics.GetDocumentHorizontalPadding"/> split
    /// symmetrically between left and right; the vertical sides keep the
    /// edit-preview's own top/bottom values so the preview reserves enough
    /// air around the rendered content for the toolbar gap and end-of-doc
    /// breathing room.
    /// </summary>
    public static Thickness CalculatePreviewDocumentPadding(
        ReadingPreferences preferences,
        double verticalTop,
        double verticalBottom)
    {
        var horizontal = ReadingLayoutMetrics.GetDocumentHorizontalPadding(preferences);
        var side = SysMath.Max(0, horizontal) / 2.0;
        return new Thickness(side, verticalTop, side, verticalBottom);
    }
}

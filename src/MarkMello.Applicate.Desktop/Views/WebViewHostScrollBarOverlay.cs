using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Avalonia-rendered ScrollBar overlay that replaces Chromium's WebKit
/// scrollbar for a given <see cref="ApplicateWebMarkdownDocumentView"/>.
/// One instance per WebView. The overlay sits at the right edge of the
/// WebView's parent surface and runs entirely on the Avalonia layout pass,
/// so thumb-drag tracks the mouse without the Win32→IPC→Chromium latency
/// that produces the "mouse outpaces thumb" + "sideways release zone"
/// artifacts of the native WebKit scrollbar.
///
/// Architecture (per Codex consultant, .scratch/codex-prompts/option-a-
/// avalonia-scrollbar-overlay-blueprint.md):
///
///   Chromium-authority + Avalonia-mirror:
///   - Wheel/touch/keyboard/programmatic scroll inside the WebView stays
///     native — overflow-y:auto remains on the renderer body.
///   - ScrollStateChanged fires from the renderer (rAF-coalesced) → we
///     mirror scrollTop/scrollHeight/clientHeight into ScrollBar.Value/
///     Maximum/ViewportSize. The Avalonia ScrollBar is a passive mirror.
///   - Only when the user grabs the Avalonia thumb does outbound traffic
///     occur: ScrollBar.Scroll fires → ScrollToProgress(percent) → renderer
///     window.scrollTo. WebView echoes that back via ScrollStateChanged but
///     during active drag we GATE the inbound mirror so it doesn't fight
///     the user's drag.
///
/// Drag-gate state machine:
///   - First ScrollEventType.ThumbTrack → enter drag mode, suppress mirror.
///   - Subsequent ThumbTrack events → forward ScrollToProgress, keep gate.
///   - ThumbPosition → final commit, send ScrollToProgress, keep gate.
///   - EndScroll → start 200ms grace timer that swallows trailing IPC echoes.
///   - First/Last/SmallIncrement/LargeIncrement/SmallDecrement/LargeDecrement
///     → command-style events from track click / keyboard; forward
///     ScrollToProgress but DO NOT enter the drag gate.
///   - Fallback exits: PointerCaptureLost, lost focus, window deactivate,
///     and a watchdog ("no ThumbTrack for one grace interval") clear the
///     gate so it never gets stuck on.
/// </summary>
internal sealed class WebViewHostScrollBarOverlay : IDisposable
{
    private static readonly TimeSpan DragGraceWindow = TimeSpan.FromMilliseconds(200);

    private readonly ApplicateWebMarkdownDocumentView _view;
    private readonly ScrollBar _scrollBar;
    private bool _isThumbDragging;
    private DateTime _dragGateExpiresAt;
    private bool _suppressOurOwnEcho;
    private bool _hostScrollbarActivated;
    private bool _disposed;

    public WebViewHostScrollBarOverlay(ApplicateWebMarkdownDocumentView view)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));

        _scrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            // AllowAutoHide=false disables Avalonia's default pointer-over
            // expansion / out-of-area collapse, matching the always-visible
            // behavior of the source-pane TextBox scrollbar so the two
            // panes look symmetric in edit mode.
            AllowAutoHide = false,
            // Permanent mount — never hide. See comment in
            // OnViewScrollStateChanged about tab-switch flash.
            IsVisible = true,
            IsHitTestVisible = true,
            Focusable = false,
            // Explicitly clear inherited Transitions so Avalonia's default
            // ScrollBar theme can't animate Opacity / Width on hover or
            // visibility change. User reports scrollbar fade-out during
            // tab switch in edit mode — likely Avalonia theme transition.
            Transitions = null,
            Opacity = 1,
            Margin = new Thickness(0, 0, 0, 0)
        };

        _scrollBar.Scroll += OnScrollBarScroll;
        _scrollBar.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        _scrollBar.LostFocus += OnLostFocus;
        _view.ScrollStateChanged += OnViewScrollStateChanged;
        _scrollBar.TemplateApplied += OnScrollBarTemplateApplied;
    }

    // Avalonia's Fluent ScrollBar theme installs a DoubleTransition on each
    // template part's Opacity (TrackRect, PART_LineUpButton,
    // PART_LineDownButton). With AllowAutoHide=false the parts stay at
    // Opacity=1, but on each visual-tree reattach the template re-initialises
    // at Opacity=0 and animates back to 1, producing an unwanted scrollbar
    // fade-in. Clearing the template parts' Transitions in TemplateApplied
    // makes the 0→1 jump instant. (The actual flicker user saw on tab change
    // turned out to be _webRenderMask z-order — see ApplicateEditPreviewView
    // construction. This Transitions=null clear was kept as defense in depth.)
    private static readonly string[] FaderTemplateParts =
        ["TrackRect", "PART_LineUpButton", "PART_LineDownButton"];

    private void OnScrollBarTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        foreach (var name in FaderTemplateParts)
        {
            if (e.NameScope.Find<Control>(name) is { } part)
            {
                part.Transitions = null;
                part.Opacity = 1;
            }
        }
    }

    /// <summary>The Avalonia <see cref="ScrollBar"/> control to mount in the host visual tree.</summary>
    public ScrollBar Control => _scrollBar;

    /// <summary>
    /// Activate Chromium-side scrollbar hiding via the existing
    /// <c>host-scrollbar</c> renderer message. Idempotent — safe to call
    /// multiple times. The renderer toggles
    /// <c>:root[data-mm-host-scrollbar="on"]</c> which hides
    /// <c>::-webkit-scrollbar</c> via the rule in renderer.css.
    /// </summary>
    public void ActivateHostScrollbarMode() => _view.SetHostScrollbarMode(true);

    private void OnViewScrollStateChanged(object? sender, ApplicateWebDocumentScrollEventArgs e)
    {
        // First-event activation: by the time the renderer fires its first
        // scroll-state message, renderer.js has loaded and its host-message
        // handler exists. Sending host-scrollbar=on now guarantees the
        // CSS rule that hides the WebKit scrollbar will apply. Idempotent —
        // safe to call repeatedly but we gate on a flag to avoid IPC chatter.
        if (!_hostScrollbarActivated)
        {
            _hostScrollbarActivated = true;
            _view.SetHostScrollbarMode(true);
        }

        // Drag gate: while the user is actively dragging the Avalonia thumb,
        // refuse to overwrite ScrollBar.Value with stale renderer-side
        // scrollTop. The grace window after EndScroll absorbs the trailing
        // IPC-lag echo. If no ThumbTrack fires within the grace window, the
        // gate auto-clears (watchdog).
        if (_isThumbDragging)
        {
            if (DateTime.UtcNow > _dragGateExpiresAt)
            {
                _isThumbDragging = false;
            }
            else
            {
                return;
            }
        }

        var maximum = System.Math.Max(0, e.ScrollHeight - e.ClientHeight);
        // NEVER hide the overlay — keep it permanently mounted. Hiding on
        // any zero state (maximum, clientHeight, or both) caused the
        // scrollbar to vanish and reappear during tab-switch document
        // swap, because the renderer transiently fires (0, 0, 0) between
        // documents. Permanent mount means the overlay is stable through
        // any renderer state, only its Maximum/Value/ViewportSize geometry
        // adapts. When there's nothing to scroll, the ScrollBar renders
        // an empty track with no active thumb.
        _scrollBar.IsVisible = true;
        _scrollBar.Minimum = 0;
        _scrollBar.Maximum = maximum;
        _scrollBar.ViewportSize = e.ClientHeight > 0 ? e.ClientHeight : 1;
        _suppressOurOwnEcho = true;
        try
        {
            _scrollBar.Value = System.Math.Clamp(e.ScrollTop, 0, maximum);
        }
        finally
        {
            _suppressOurOwnEcho = false;
        }
    }

    private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
    {
        if (_suppressOurOwnEcho)
        {
            return;
        }

        var maximum = _scrollBar.Maximum;
        if (maximum <= 0)
        {
            return;
        }

        switch (e.ScrollEventType)
        {
            case ScrollEventType.ThumbTrack:
                // User is actively dragging. Enter the drag gate and refresh
                // its expiry on every drag tick so the watchdog can't trip
                // mid-drag. Forward the new position to renderer.
                _isThumbDragging = true;
                _dragGateExpiresAt = DateTime.UtcNow + DragGraceWindow;
                SendProgress(e.NewValue, maximum);
                break;

            case ScrollEventType.EndScroll:
                // Drag finished. Keep the gate open for one grace interval
                // so the trailing renderer-side scroll echo (which always
                // arrives slightly after the last user input due to IPC) is
                // absorbed instead of snapping the thumb back to a slightly-
                // earlier position. The OnViewScrollStateChanged auto-clear
                // handles the actual exit — see watchdog branch there.
                _dragGateExpiresAt = DateTime.UtcNow + DragGraceWindow;
                break;

            case ScrollEventType.SmallIncrement:
            case ScrollEventType.SmallDecrement:
            case ScrollEventType.LargeIncrement:
            case ScrollEventType.LargeDecrement:
                // Command-style events from track-clicks or keyboard arrows.
                // Forward to renderer but do NOT engage the drag gate —
                // these are discrete jumps, the user is not dragging.
                SendProgress(e.NewValue, maximum);
                break;
        }
    }

    private void SendProgress(double value, double maximum)
    {
        var percent = System.Math.Clamp(value / maximum * 100.0, 0, 100);
        _view.ScrollToProgress(percent);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Fallback exit: Avalonia normally fires EndScroll on capture loss,
        // but on focus changes, window deactivation, or alt-tab the event
        // sequence can be truncated. Force-clear the gate here so the next
        // ScrollStateChanged isn't suppressed forever.
        _isThumbDragging = false;
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        _isThumbDragging = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _view.ScrollStateChanged -= OnViewScrollStateChanged;
        _scrollBar.Scroll -= OnScrollBarScroll;
        _scrollBar.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost);
        _scrollBar.LostFocus -= OnLostFocus;
        if (_view is not null)
        {
            // Best-effort: tell the renderer to restore WebKit scrollbar if
            // the overlay was uninstalled. In permanent-mount usage Dispose
            // only fires at window close, so this is mostly cosmetic.
            try
            {
                _view.SetHostScrollbarMode(false);
            }
            catch
            {
                // Swallow — window already closed, view disposed, etc.
            }
        }
    }
}

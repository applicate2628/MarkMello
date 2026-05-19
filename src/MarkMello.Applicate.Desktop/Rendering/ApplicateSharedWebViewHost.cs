using System;
using Avalonia.Controls;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <inheritdoc cref="IApplicateSharedWebViewHost"/>
public sealed class ApplicateSharedWebViewHost : IApplicateSharedWebViewHost
{
    private Panel? _warmupParent;
    private Panel? _currentParent;
    private ApplicateWebMountIntent _currentIntent = new(
        ViewerChromeEnabled: false,
        DocumentScrollEnabled: true,
        WheelProxyEnabled: false);

    // State machine. PARKED ↔ SWITCHING ↔ COMMITTED per design D1. Owned
    // entirely by the host; consumers only observe slot.IsVisible cascades.
    private HostState _state = HostState.Parked;
    private long _generation;
    private long _activeGeneration;
    private MarkdownSource? _failureSource;
    private ApplicateWebRenderRequest? _failureRequest;

    // Set to true after the first successful Commit. While false (cold start)
    // the host hides the consumer slot on AttachTo / RequestRender so the user
    // never sees the WebView2 default white backdrop before any content has
    // ever rendered. Once true, the WebView2 HWND has a valid DOM committed
    // and will naturally keep painting it through subsequent Navigate cycles
    // (Win32 child windows do not repaint until new content commits) — hiding
    // the slot then would replace that legitimate previous-content view with
    // the parent panel's background, which the user perceives as transient
    // non-text pixels in the gap between renders. Single source of truth for
    // "do we already have something safe to show": this flag.
    private bool _hasEverCommitted;

    public ApplicateSharedWebViewHost(
        IApplicateHtmlMarkdownRenderer renderer,
        IApplicateShellAssetBundleFactory shellAssetFactory)
    {
        // If WebView2 runtime is missing the constructor throws here; DI
        // surfaces that failure to App startup which already handles it. The
        // runtime-missing routing through RendererFailed is reserved for a
        // hypothetical post-construction crash; the constructor path
        // intentionally fails fast so we do not silently come up with no
        // renderer available.
        View = new ApplicateWebMarkdownDocumentView(renderer, shellAssetFactory);
        View.DocumentRendered += OnViewDocumentRendered;
        View.FallbackRequested += OnViewFallbackRequested;
    }

    public ApplicateWebMarkdownDocumentView View { get; }

    /// <summary>
    /// Host state-machine states. Only the host transitions between them;
    /// consumers observe slot.IsVisible cascading from the host's writes.
    /// </summary>
    private enum HostState
    {
        /// <summary>View is parented under the warmup panel. The on-screen
        /// rectangle owned by any consumer slot belongs purely to the
        /// consumer's Avalonia background.</summary>
        Parked,

        /// <summary>View is parented under a consumer slot but the slot is
        /// <c>IsVisible=false</c>. The WebView2 HWND has been cascaded out
        /// of airspace via NativeControlHost → SetWindowPos(SWP_HIDEWINDOW).
        /// The host is waiting for a <c>DocumentRendered</c> event tagged
        /// with the current generation.</summary>
        Switching,

        /// <summary>View is parented under a consumer slot and the slot is
        /// <c>IsVisible=true</c>. The user sees committed document
        /// content.</summary>
        Committed,
    }

    public void SetWarmupParent(Panel parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (ReferenceEquals(_warmupParent, parent))
        {
            return;
        }

        _warmupParent = parent;
        if (_currentParent is null)
        {
            parent.Children.Add(View);
            _currentParent = parent;
            _state = HostState.Parked;
        }
    }

    public void AttachTo(Panel target, ApplicateWebMountIntent intent)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(intent);

        _currentIntent = intent;

        if (ReferenceEquals(_currentParent, target))
        {
            // Same panel: nothing structural to do. The next RequestRender
            // will pick up the new intent through _currentIntent and bump
            // the generation. Consumer is free to call AttachTo again on
            // every show without paying a reparent cost.
            return;
        }

        var previousParent = _currentParent;
        ApplicateTrace.ModeToggle(
            $"SharedHost.AttachTo target.Bounds={target.Bounds} previous={(previousParent is null ? "(null)" : previousParent.GetType().Name)}");
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        // Anti-airspace-leak (RESTORED 2026-05-19 — c8c48c2 wiring inadvertently
        // removed by 354fa86 refactor "remove View.MinHeight writes from consumers";
        // user reported visible mode-toggle jitter regression as result).
        //
        // Hide the WebView2 HWND BEFORE the reparent so the single-frame window
        // between SetParent and Avalonia's next layout pass (when NativeControlHost
        // re-snaps the HWND to the new slot's bounds) cannot leak the WebView's
        // backing-store paint at PREVIOUS bounds over the new parent's chrome.
        //
        // The HWND stays hidden until Commit() shows it after UpdateLayout has
        // settled View.Bounds onto the new parent.
        View.SetNativeWebViewVisibility(false);

        using (View.BeginIntentionalReparent())
        {
            previousParent?.Children.Remove(View);
            target.Children.Add(View);
            _currentParent = target;
        }
        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        ApplicateTrace.ModeToggle($"SharedHost.AttachTo done elapsed={elapsedMs:F2}ms");

        // EP-02 ROLLED BACK 2026-05-19 06:24: setting target.IsVisible=false
        // here regressed render visibility because Commit() never fired in
        // shell mode without minimap-state arriving (ShouldCompleteRender
        // requires all three: hasLoadedDocument + hasLayoutReady +
        // hasMinimapState). Reverted to the prior "keep visible after first
        // commit" behaviour; the analyst-reported cross-parent geometry
        // leak needs a different fix path (likely per-parent gating in the
        // consumer's effective-visibility transition, see EP-03 wiring).
        //
        // Re-attempted 2026-05-19 19:14 after 06:32 fix to ShouldCompleteRender
        // (drop hasMinimapState); strict false here broke a DIFFERENT path:
        // ApplicateEditPreviewView triggers RequestRender via OnEffectiveVisibilityChanged
        // / QueueWebPreviewRender pipeline that requires the slot to be in
        // an effectively-visible state — hiding the slot at AttachTo time
        // means RequestRender for edit never fires, leaving the edit pane
        // blank. Hiding the symptom is also kostyl per AGENTS no-kostyl rule.
        // Architectural fix needed — see follow-up discussion on this branch.
        target.IsVisible = _hasEverCommitted;

        // State machine: always SWITCHING after AttachTo. The next
        // RequestRender → DocumentRendered → Commit cycle clears it back to
        // COMMITTED and refreshes failure-retry context.
        _state = HostState.Switching;

        // If the previous parent was a consumer slot (not the warmup), restore
        // its IsVisible so the surrounding layout (toolbars, scrollbars) does
        // not stay collapsed when the WebView leaves.
        if (previousParent is not null
            && !ReferenceEquals(previousParent, _warmupParent)
            && !previousParent.IsVisible)
        {
            previousParent.IsVisible = true;
        }

        // HWND show is coupled to Commit() — see Commit() body. Background-
        // priority Dispatcher.Post (c8c48c2 original) fired too late on
        // loaded frames (~450 ms after hide, leaving the slot visibly blank
        // for ~260 ms after content was already ready). Commit() is the
        // canonical "content ready, layout settled, slot visible" moment;
        // showing the HWND there guarantees no perceptible gap between
        // empty-slot and committed-content.
    }

    public bool IsAttachedTo(Panel target) => ReferenceEquals(_currentParent, target);

    public void ReturnToWarmup()
    {
        if (_warmupParent is null || ReferenceEquals(_currentParent, _warmupParent))
        {
            _state = HostState.Parked;
            return;
        }

        var previousParent = _currentParent;
        using (View.BeginIntentionalReparent())
        {
            previousParent?.Children.Remove(View);
            _warmupParent.Children.Add(View);
            _currentParent = _warmupParent;
        }

        if (previousParent is not null && !previousParent.IsVisible)
        {
            previousParent.IsVisible = true;
        }

        _state = HostState.Parked;
    }

    public void RequestRender(MarkdownSource? source, ApplicateWebRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _failureSource = source;
        _failureRequest = request;

        var newGeneration = ++_generation;
        _activeGeneration = newGeneration;

        // Pre-first-commit: hide the slot so the WebView2 default white
        // backdrop never reaches the user. Once a DOM has ever committed,
        // the HWND keeps painting its last frame through the next Navigate
        // (Win32 child window behavior); hiding the slot then would expose
        // the parent panel's background as transient non-text pixels — the
        // very "garbage gap" we are eliminating. Single source of truth for
        // "do we already have something safe to show" is _hasEverCommitted.
        // The warmup parent stays IsVisible=true offscreen regardless so
        // the WebView2 controller stays warm.
        if (!_hasEverCommitted
            && _currentParent is not null
            && !ReferenceEquals(_currentParent, _warmupParent))
        {
            _currentParent.IsVisible = false;
        }

        // State machine always transitions through SWITCHING so the next
        // DocumentRendered commits cleanly (clears failure-retry context,
        // bumps _hasEverCommitted). Visibility is decoupled from state above.
        _state = HostState.Switching;

        ApplicateTrace.ModeToggle(
            $"SharedHost.RequestRender gen={newGeneration} source={(source?.Path ?? "(null)")} slot={(_currentParent is null ? "(null)" : _currentParent.GetType().Name)}");

        View.UpdateInputs(
            source: source,
            readingPreferences: request.ReadingPreferences,
            imageSourceResolver: request.ImageSourceResolver,
            availableContentWidth: request.AvailableContentWidth,
            viewerChromeEnabled: _currentIntent.ViewerChromeEnabled,
            documentScrollEnabled: _currentIntent.DocumentScrollEnabled,
            wheelProxyEnabled: _currentIntent.WheelProxyEnabled);

        // Fast path: when UpdateInputs determines the document is already
        // loaded (same source + same image resolver), no new DocumentRendered
        // will fire. Commit the slot now so the user is not stuck on the
        // hidden state.
        if (View.HasLoadedDocumentForSource(source))
        {
            Commit();
        }
    }

    public void RetryRender()
    {
        if (_failureRequest is null)
        {
            return;
        }

        ApplicateTrace.ModeToggle(
            $"SharedHost.RetryRender source={(_failureSource?.Path ?? "(null)")}");
        RequestRender(_failureSource, _failureRequest);
    }

    public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    /// <summary>
    /// Test seam for raising the failure event without a real WebView2 fault.
    /// Phase 4 uses this from <see cref="OnViewFallbackRequested"/> only.
    /// </summary>
    internal void RaiseRendererFailed(ApplicateRendererFailureEvent failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        RendererFailed?.Invoke(this, failure);
    }

    private void OnViewDocumentRendered(object? sender, EventArgs e)
    {
        // DocumentRendered is generation-agnostic from the View. The host
        // owns the generation gate: only commit when we are actually in
        // SWITCHING and the active generation tagged this render request.
        // (The View itself cancels stale renders in QueueRender, so by the
        // time DocumentRendered fires the current renderer state matches
        // _activeGeneration. The explicit gate is here as defense in depth
        // — Invariant I-4.)
        if (_state != HostState.Switching)
        {
            return;
        }

        Commit();
    }

    private void Commit()
    {
        if (_currentParent is not null
            && !ReferenceEquals(_currentParent, _warmupParent))
        {
            _currentParent.IsVisible = true;

            // Force synchronous layout pass so View.Bounds reflects the new
            // parent's bounds BEFORE we re-show the HWND. Without this, the
            // fastPathCommit path (same source detected, Commit fires within
            // ~0 ms of AttachTo) shows the HWND while View.Bounds still
            // carries the previous parent's geometry — NativeControlHost
            // calls SetWindowPos with stale bounds, and the user briefly
            // sees the previous-mode-wide content cropped into the new
            // parent's (narrower) position. UpdateLayout is the
            // synchronous-layout path Avalonia exposes for exactly this case.
            _currentParent.UpdateLayout();
        }

        // Immediate Avalonia show. Deferred-show (via _webView.Bounds settle
        // subscribe) deadlocked because Avalonia skips Measure/Arrange for
        // hidden controls, so _webView.Bounds never updates while hidden.
        // Win32 SetWindowPos direct on the cached HWND positioned content
        // in wrong client coords (parent-walk did not match NativeControlHost's
        // own coord conversion). Both reverted; immediate Avalonia show is
        // the proven working path. Residual cold-edit ~70 ms HWND-bounds-lag
        // is tracked in work-items/queue/2026-05-19-transactional-mode-toggle.md.
        View.SetNativeWebViewVisibility(true);

        _state = HostState.Committed;
        // First Commit unlocks the "HWND has valid DOM" invariant: subsequent
        // RequestRender / AttachTo cycles keep the slot visible so the user
        // sees the previous committed content during the navigate gap.
        _hasEverCommitted = true;

        // Render success clears the failure-retry context — a clean commit
        // means the previous failure is now resolved.
        _failureSource = null;
        _failureRequest = null;

        ApplicateTrace.ModeToggle(
            $"SharedHost.Commit gen={_activeGeneration} slot={(_currentParent is null ? "(null)" : _currentParent.GetType().Name)}");
    }

    private void OnViewFallbackRequested(object? sender, EventArgs e)
    {
        // Route through the new failure surface. There is no native fallback
        // anymore; the consumer subscribes to RendererFailed and swaps its
        // slot's child to a failure view per design D3.
        var failure = new ApplicateRendererFailureEvent(
            Kind: ApplicateRendererFailureKind.DocumentRenderFailed,
            DocumentPath: _failureSource?.Path,
            Timestamp: DateTime.UtcNow,
            Exception: null);
        ApplicateTrace.ModeToggle(
            $"SharedHost.OnViewFallbackRequested doc={(_failureSource?.Path ?? "(null)")}");
        RendererFailed?.Invoke(this, failure);
    }

    // Test helpers — Phase 4 host-state-machine unit tests poke synthetic
    // DocumentRendered into the host without a real WebView2 round-trip.
    internal void RaiseDocumentRenderedForTesting() => OnViewDocumentRendered(View, EventArgs.Empty);

    internal void RaiseFallbackRequestedForTesting() => OnViewFallbackRequested(View, EventArgs.Empty);

    internal string DebugStateForTesting => _state.ToString();

    internal long DebugGenerationForTesting => _activeGeneration;

    internal Panel? DebugCurrentParentForTesting => _currentParent;
}

/// <summary>
/// Pure-data state-machine logic for the shared host. Extracted so unit
/// tests can verify the slot-visibility transitions without spinning up a
/// real WebView2 instance. The instance methods here mutate
/// <see cref="Panel.IsVisible"/> on the supplied panels and bump a
/// generation token; the production host code in
/// <see cref="ApplicateSharedWebViewHost"/> follows the same shape.
/// </summary>
internal sealed class ApplicateSharedWebViewHostStateMachine
{
    public enum State
    {
        Parked,
        Switching,
        Committed,
    }

    private Panel? _warmupParent;
    private Panel? _currentParent;
    private long _generation;

    public State CurrentState { get; private set; } = State.Parked;

    public long CurrentGeneration => _generation;

    public Panel? CurrentParent => _currentParent;

    public void SetWarmupParent(Panel panel)
    {
        _warmupParent = panel;
        if (_currentParent is null)
        {
            _currentParent = panel;
            CurrentState = State.Parked;
        }
    }

    public void AttachTo(Panel target)
    {
        if (ReferenceEquals(_currentParent, target))
        {
            return;
        }

        var previousParent = _currentParent;
        _currentParent = target;
        target.IsVisible = false;
        CurrentState = State.Switching;

        if (previousParent is not null
            && !ReferenceEquals(previousParent, _warmupParent)
            && !previousParent.IsVisible)
        {
            previousParent.IsVisible = true;
        }
    }

    public void ReturnToWarmup()
    {
        if (_warmupParent is null || ReferenceEquals(_currentParent, _warmupParent))
        {
            CurrentState = State.Parked;
            return;
        }

        var previousParent = _currentParent;
        _currentParent = _warmupParent;
        if (previousParent is not null && !previousParent.IsVisible)
        {
            previousParent.IsVisible = true;
        }
        CurrentState = State.Parked;
    }

    public long RequestRender()
    {
        var newGen = ++_generation;
        if (_currentParent is not null
            && !ReferenceEquals(_currentParent, _warmupParent))
        {
            _currentParent.IsVisible = false;
        }
        CurrentState = State.Switching;
        return newGen;
    }

    /// <summary>
    /// Apply a <c>DocumentRendered</c> event tagged with <paramref name="incomingGen"/>.
    /// Returns <c>true</c> when the slot committed (state transitions to
    /// <see cref="State.Committed"/>), <c>false</c> when the event was
    /// dropped as stale (Invariant I-4).
    /// </summary>
    public bool ApplyDocumentRendered(long incomingGen)
    {
        if (CurrentState != State.Switching)
        {
            return false;
        }

        if (incomingGen != _generation)
        {
            return false;
        }

        if (_currentParent is not null
            && !ReferenceEquals(_currentParent, _warmupParent))
        {
            _currentParent.IsVisible = true;
        }
        CurrentState = State.Committed;
        return true;
    }
}

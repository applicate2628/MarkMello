using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <inheritdoc cref="IApplicateSharedWebViewHost"/>
public sealed class ApplicateSharedWebViewHost :
    IApplicateSharedWebViewHost,
    IApplicateHostRevealEndpoint,
    IApplicateTransactionRendererSettleProbeRequester
{
    private const int RendererFrameWaitSkipDocumentContentLength = 1024 * 1024;

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
    private long _activeTransactionGeneration;
    private bool _activeTransactionSkipsRendererFrameSettle;
    private long _minimapSettledTransactionGeneration;
    private long _rendererSettledTransactionGeneration;
    private MarkdownSource? _failureSource;
    private ApplicateWebRenderRequest? _failureRequest;
    private ApplicateMode _currentMode = ApplicateMode.Edit;
    private long _transactionNativeRevealGeneration;
    private bool _transactionNativeRevealPending;

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

    // Mode-toggle reveal gate (2026-05-20). Set to true at AttachTo when the
    // target panel actually differs from the previous parent — i.e. the WebView
    // is being relocated to a slot whose bounds may differ. Cleared at Commit
    // after the reveal sequence settles. The gate only takes effect when both
    // this flag and _hasEverCommitted are true; on the cold path the legacy
    // immediate-show behaviour stays (no previous-content backing store to
    // protect, no reflow to wait for). Re-entrancy: a new AttachTo or
    // RequestRender lands while a probe is in flight invalidates the wait via
    // the activeGeneration token that the compositor observes.
    private bool _reparentedThisCycle;

    public ApplicateSharedWebViewHost(
        IApplicateHtmlMarkdownRenderer renderer,
        IApplicateShellAssetBundleFactory shellAssetFactory)
        : this(renderer, shellAssetFactory, new ApplicateRenderedBodyCache())
    {
    }

    internal ApplicateSharedWebViewHost(
        IApplicateHtmlMarkdownRenderer renderer,
        IApplicateShellAssetBundleFactory shellAssetFactory,
        ApplicateRenderedBodyCache renderedBodyCache)
    {
        ApplicateTrace.DiagMs("startup-webview", "shared-host-ctor-start");
        // If WebView2 runtime is missing the constructor throws here; DI
        // surfaces that failure to App startup which already handles it. The
        // runtime-missing routing through RendererFailed is reserved for a
        // hypothetical post-construction crash; the constructor path
        // intentionally fails fast so we do not silently come up with no
        // renderer available.
        View = new ApplicateWebMarkdownDocumentView(renderer, shellAssetFactory, renderedBodyCache);
        View.DocumentRendered += OnViewDocumentRendered;
        View.MinimapSettled += OnViewMinimapSettled;
        View.ModeToggleTransactionSettled += OnViewModeToggleTransactionSettled;
        View.FallbackRequested += OnViewFallbackRequested;
        ApplicateTrace.DiagMs("startup-webview", "shared-host-ctor-end");
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
            ApplicateTrace.DiagMs("startup-webview", "warmup-parent-attached");
            _currentParent = parent;
            _state = HostState.Parked;
        }
    }

    public void AttachTo(Panel target, ApplicateWebMountIntent intent)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(intent);

        _currentIntent = intent;
        _currentMode = intent.ViewerChromeEnabled ? ApplicateMode.Viewer : ApplicateMode.Edit;

        if (ReferenceEquals(_currentParent, target))
        {
            // Same panel: nothing structural to do. The next RequestRender
            // will pick up the new intent through _currentIntent and bump
            // the generation. Consumer is free to call AttachTo again on
            // every show without paying a reparent cost.
            return;
        }

        var transactionalAttach = ApplicateModeTransactionContext.GetTransactionGeneration(target) > 0;
        var previousParent = _currentParent;
        ApplicateTrace.ModeToggle(
            $"SharedHost.AttachTo target.Bounds={target.Bounds} previous={(previousParent is null ? "(null)" : previousParent.GetType().Name)}");
        ApplicateTrace.DiagMs(
            "pane-seq",
            "host-attachto-start",
            $"targetBounds={target.Bounds.Width:F0}x{target.Bounds.Height:F0} previous={(previousParent is null ? "null" : previousParent.GetType().Name)} hasEverCommitted={_hasEverCommitted} transactionAttach={transactionalAttach}");
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        HostAttachStarting?.Invoke(
            this,
            new ApplicateHostAttachStartingEventArgs(target, transactionalAttach));

        // Anti-airspace-leak (RESTORED 2026-05-19 — c8c48c2 wiring inadvertently
        // removed by 354fa86 refactor "remove View.MinHeight writes from consumers";
        // user reported visible mode-toggle jitter regression as result).
        //
        // Raise the host-reveal intent BEFORE the reparent so the single-frame
        // window between SetParent and Avalonia's next layout pass (when
        // NativeControlHost re-snaps the HWND to the new slot's bounds) cannot
        // leak the WebView's backing-store paint at PREVIOUS bounds over the
        // new parent's chrome.
        //
        // The HWND stays hidden until Commit() shows it after UpdateLayout has
        // settled View.Bounds onto the new parent.
        using (View.BeginIntentionalReparent())
        {
            previousParent?.Children.Remove(View);
            target.Children.Add(View);
            _currentParent = target;
        }
        // Mark the cycle as a real reparent. The reveal gate in Commit() only
        // fires when this flag AND _hasEverCommitted are both true: the WebView
        // has previously committed a document (so its HWND would paint
        // stale-bounds content if revealed immediately), and the slot has just
        // been relocated (so the renderer must reflow). Pure same-panel
        // AttachTo calls (handled by the ReferenceEquals early-return above)
        // never reach this line, so they retain the existing fast-path behaviour.
        _reparentedThisCycle = true;
        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        ApplicateTrace.ModeToggle($"SharedHost.AttachTo done elapsed={elapsedMs:F2}ms");
        ApplicateTrace.DiagMs(
            "pane-seq",
            "host-attachto-end",
            $"reparentMs={elapsedMs:F2} targetIsVisible={target.IsVisible} reparented={_reparentedThisCycle}");

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
        HostAttachCompleted?.Invoke(
            this,
            new ApplicateHostAttachCompletedEventArgs(
                target,
                transactionalAttach,
                _hasEverCommitted));

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
        if (previousParent is not null && !ReferenceEquals(previousParent, _warmupParent))
        {
            previousParent.Opacity = 1.0;
        }

        _state = HostState.Parked;
    }

    public void RequestRender(MarkdownSource? source, ApplicateWebRenderRequest request)
        => RequestRender(source, request, transactionGeneration: 0);

    public void RequestRender(MarkdownSource? source, ApplicateWebRenderRequest request, long transactionGeneration)
        => RequestRender(
            source,
            request,
            transactionGeneration,
            keepColdParentVisibleForInactivePrime: false);

    public void RequestInactivePrimeRender(MarkdownSource? source, ApplicateWebRenderRequest request)
        => RequestRender(
            source,
            request,
            transactionGeneration: 0,
            keepColdParentVisibleForInactivePrime: true);

    private void RequestRender(
        MarkdownSource? source,
        ApplicateWebRenderRequest request,
        long transactionGeneration,
        bool keepColdParentVisibleForInactivePrime)
    {
        ArgumentNullException.ThrowIfNull(request);

        _failureSource = source;
        _failureRequest = request;
        _activeTransactionGeneration = transactionGeneration;
        _minimapSettledTransactionGeneration = 0;
        _rendererSettledTransactionGeneration = 0;

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
        // the WebView2 controller stays warm. Inactive edit-prime is another
        // offscreen-visible path: hiding its child panel prevents Chromium
        // from reaching layout-ready, so that caller explicitly keeps the
        // current parent visible while the outer slot is outside the window.
        var transactionalRequest = transactionGeneration > 0;
        HostRenderStarting?.Invoke(
            this,
            new ApplicateHostRenderStartingEventArgs(
                _currentParent,
                _warmupParent,
                View.Source,
                source,
                View.HasLoadedDocumentForReveal,
                transactionalRequest,
                keepColdParentVisibleForInactivePrime,
                _hasEverCommitted));

        // State machine always transitions through SWITCHING so the next
        // DocumentRendered commits cleanly (clears failure-retry context,
        // bumps _hasEverCommitted). Visibility is decoupled from state above.
        _state = HostState.Switching;

        ApplicateTrace.ModeToggle(
            $"SharedHost.RequestRender gen={newGeneration} source={(source?.Path ?? "(null)")} slot={(_currentParent is null ? "(null)" : _currentParent.GetType().Name)}");

        var skipRendererFrameWait = ShouldSkipRendererFrameWait(source, transactionGeneration);
        View.UpdateInputs(
            source: source,
            readingPreferences: request.ReadingPreferences,
            imageSourceResolver: request.ImageSourceResolver,
            availableContentWidth: request.AvailableContentWidth,
            viewerChromeEnabled: _currentIntent.ViewerChromeEnabled,
            documentScrollEnabled: _currentIntent.DocumentScrollEnabled,
            wheelProxyEnabled: _currentIntent.WheelProxyEnabled,
            deferLivePreferencesUntilModeSettleProbe: transactionGeneration > 0,
            skipFrameWaitUntilRenderReady: skipRendererFrameWait);
        _activeTransactionSkipsRendererFrameSettle =
            ShouldSkipRendererFrameSettleForTransaction(transactionGeneration);

        if (transactionGeneration > 0 && !_currentIntent.ViewerChromeEnabled)
        {
            RaiseMinimapSettled(transactionGeneration, state: null);
        }

        // Fast path: when UpdateInputs determines the document is already
        // loaded (same source + same image resolver), no new DocumentRendered
        // will fire. Commit the slot now so the user is not stuck on the
        // hidden state.
        if (View.HasLoadedDocumentForSource(source))
        {
            Commit();
            // Same-source no-op: action=None produced no QueueRender, so no new
            // DocumentRendered and no fresh DocumentRevealReady will fire. Commit()
            // gave the reveal cover its commit signal; re-emit reveal-ready (valid
            // ONLY because the source is already loaded AND fully painted, per
            // HasLoadedDocumentForSource) so a same-content reload's cover resolves
            // promptly instead of waiting out its 8s idle fallback. gen==0 leaves the
            // mode-toggle (Ctrl+E) transactional reveal owned by the bridge.
            if (transactionGeneration == 0)
            {
                View.RaiseDocumentRevealReadyForLoadedSource(source);
            }
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

    public void CommitInPlaceSourceSwap(MarkdownSource source)
    {
        // Keep a pending RetryRender truthful: if the failure snapshot is the
        // same document, a later retry must render the just-written content.
        if (_failureSource is not null
            && string.Equals(_failureSource.Path, source.Path, StringComparison.OrdinalIgnoreCase))
        {
            _failureSource = source;
        }

        View.CommitInPlaceSourceSwap(source);
    }

    public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    public event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled;

    public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

    public event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled;

    public event EventHandler<ApplicateTransactionRendererSettleProbeEventArgs>? TransactionRendererSettleProbeReady;

    private event EventHandler<ApplicateHostAttachStartingEventArgs>? HostAttachStarting;

    private event EventHandler<ApplicateHostAttachCompletedEventArgs>? HostAttachCompleted;

    private event EventHandler<ApplicateHostRenderStartingEventArgs>? HostRenderStarting;

    private event EventHandler<ApplicateHostCommitPreparingEventArgs>? HostCommitPreparing;

    TimeSpan IApplicateHostRevealEndpoint.RendererSettleFallbackTimeout =>
        ApplicateAirspaceCompositor.HostRendererSettleFallbackTimeout;

    string IApplicateHostRevealEndpoint.RendererThemeName => View.RendererThemeName;

    event EventHandler<ApplicateHostAttachStartingEventArgs>? IApplicateHostRevealEndpoint.AttachStarting
    {
        add => HostAttachStarting += value;
        remove => HostAttachStarting -= value;
    }

    event EventHandler<ApplicateHostAttachCompletedEventArgs>? IApplicateHostRevealEndpoint.AttachCompleted
    {
        add => HostAttachCompleted += value;
        remove => HostAttachCompleted -= value;
    }

    event EventHandler<ApplicateHostRenderStartingEventArgs>? IApplicateHostRevealEndpoint.RenderStarting
    {
        add => HostRenderStarting += value;
        remove => HostRenderStarting -= value;
    }

    event EventHandler<ApplicateHostCommitPreparingEventArgs>? IApplicateHostRevealEndpoint.CommitPreparing
    {
        add => HostCommitPreparing += value;
        remove => HostCommitPreparing -= value;
    }

    event EventHandler? IApplicateHostRevealEndpoint.DocumentRenderVisualReady
    {
        add => View.DocumentRenderVisualReady += value;
        remove => View.DocumentRenderVisualReady -= value;
    }

    event EventHandler? IApplicateHostRevealEndpoint.RendererRevealSettled
    {
        add => View.ModeToggleSettled += value;
        remove => View.ModeToggleSettled -= value;
    }

    void IApplicateHostRevealEndpoint.ParkNativeWebViewForReparent()
        => View.ParkNativeWebViewForReparent();

    void IApplicateHostRevealEndpoint.SetNativeWebViewVisibility(bool isVisible)
        => View.SetNativeWebViewVisibility(isVisible);

    void IApplicateHostRevealEndpoint.PrepareNativeWebViewHiddenPaint()
        => View.PrepareNativeWebViewForHiddenPaint();

    void IApplicateHostRevealEndpoint.CompleteNativeWebViewHiddenPaint()
        => View.CompleteNativeWebViewHiddenPaint();

    void IApplicateHostRevealEndpoint.PostRendererRevealMessage(object message)
        => View.PostRendererRevealMessage(message);

    void IApplicateHostRevealEndpoint.RequestRendererSettleProbe()
        => View.RequestModeToggleSettleProbe();

    void IApplicateHostRevealEndpoint.RequestTransactionRendererSettleProbe(
        long transactionGeneration,
        bool skipFrameWait)
        => View.RequestModeToggleSettleProbe(transactionGeneration, skipFrameWait);

    void IApplicateTransactionRendererSettleProbeRequester.RequestTransactionRendererSettleProbe(
        long transactionGeneration,
        bool skipFrameWait)
        => View.RequestModeToggleSettleProbe(transactionGeneration, skipFrameWait);

    internal static bool ShouldSkipRendererFrameSettleForTransaction(long transactionGeneration)
        // Transactional Commit() is already gated by UpdateInputs' synchronous
        // preference application or by DocumentRendered's layout/minimap quorum.
        // Waiting for another renderer rAF while the target WebView HWND is
        // intentionally hidden can deadlock tab switches.
        => transactionGeneration > 0;

    internal static bool ShouldSkipRendererFrameWait(MarkdownSource? source, long transactionGeneration)
        => transactionGeneration > 0
            || source?.Content.Length > RendererFrameWaitSkipDocumentContentLength;

    public void SuppressNativeRendererForModeSwitch(ApplicateMode displayedMode)
    {
        if (displayedMode != _currentMode)
        {
            ApplicateTrace.ModeToggle(
                $"SharedHost.SuppressNativeRendererForModeSwitch skipped displayed={displayedMode} current={_currentMode}");
            return;
        }

        ApplicateTrace.ModeToggle(
            $"SharedHost.SuppressNativeRendererForModeSwitch displayed={displayedMode} gen={_activeGeneration}");
        View.ResetHostShortcutsForModeSwitch();
        View.SetNativeWebViewVisibility(false);
    }

    public void RestoreNativeRendererAfterModeSwitchSuppression(ApplicateMode displayedMode)
    {
        if (displayedMode != _currentMode)
        {
            ApplicateTrace.ModeToggle(
                $"SharedHost.RestoreNativeRendererAfterModeSwitchSuppression skipped displayed={displayedMode} current={_currentMode}");
            return;
        }

        ApplicateTrace.ModeToggle(
            $"SharedHost.RestoreNativeRendererAfterModeSwitchSuppression displayed={displayedMode} gen={_activeGeneration}");
        View.SetNativeWebViewVisibility(true);
    }

    /// <summary>
    /// Test seam for raising the failure event without a real WebView2 fault.
    /// Phase 4 uses this from <see cref="OnViewFallbackRequested"/> only.
    /// </summary>
    internal void RaiseRendererFailed(ApplicateRendererFailureEvent failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        RendererFailed?.Invoke(this, failure);
    }

    public async Task PreWarmShellAsync(CancellationToken cancellationToken = default)
    {
        // Pre-warm runs on the Avalonia UI thread end-to-end. NavigateToShellAsync
        // calls _webView.Navigate(...) which is Avalonia-controlled and must run
        // on the UI thread; ApplicateTrace emission is thread-agnostic but we
        // keep the whole flow on UI to avoid cross-thread state-write hazards
        // against _shellNavigated and _shellReady.
        //
        // The caller (ApplicateMainWindow.Opened handler) is already on the UI
        // thread when Opened fires, but we guard with CheckAccess+Post so any
        // background-thread caller is normalised to UI thread automatically.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => PreWarmShellAsync(cancellationToken),
                DispatcherPriority.Background);
            return;
        }

        ApplicateTrace.DiagMs("startup-webview", "shell-prewarm-start");
        try
        {
            await View.EnsureShellReadyAsync(cancellationToken).ConfigureAwait(true);
            // EnsureShellReadyAsync resolves _shellReady's TCS via the
            // document-ready IPC at OnWebMessageReceived; by the time the
            // call returns, the shell is fully navigated AND the IPC has
            // fired. The 502 ms navigate-shell to shell-ready gap has been
            // paid HERE, before any user RequestRender lands.
            ApplicateTrace.DiagMs("startup-webview", "shell-prewarm-ready");
        }
        catch (OperationCanceledException)
        {
            // Cancellation is silent. The lazy QueueRenderShellAsync path
            // owns the retry at the next user render. No marker emitted —
            // cancellation is not a failure mode.
        }
        catch (Exception ex)
        {
            ApplicateTrace.DiagMs("startup-webview", "shell-prewarm-failed", "ex=" + ex.GetType().Name);
            // Swallow so the host stays usable. The lazy path at
            // QueueRenderShellAsync:521 retries the shell navigation on the
            // next user render; if that ALSO fails it routes through
            // FallbackRequested -> OnViewFallbackRequested -> RendererFailed.
        }
    }

    public async Task WaitForShellReadyAsync(CancellationToken cancellationToken = default)
    {
        // Normalise off-UI callers exactly like PreWarmShellAsync. The View's
        // EnsureShellReadyAsync writes _shellNavigated and (when it has to
        // drive navigation) calls _webView.Navigate(...) which is Avalonia-
        // controlled and must run on the UI thread. The fast-path
        // (_shellNavigated == true) inside the View still awaits a TCS Task
        // that completes asynchronously, so even already-ready calls are safe
        // to drive through a UI-thread post.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => WaitForShellReadyAsync(cancellationToken),
                DispatcherPriority.Background);
            return;
        }

        try
        {
            // Delegates to the same internal entry point PreWarmShellAsync
            // uses. EnsureShellReadyAsync is idempotent against _shellNavigated
            // and against a pre-existing _shellReady TCS, so cache-hit callers
            // do not race with PreWarmShellAsync and never issue a duplicate
            // shell navigation. When the shell is already navigated and the
            // document-ready IPC has fired, the underlying await returns
            // immediately.
            await View.EnsureShellReadyAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Surface cancellation to the caller. The View leaves
            // _shellNavigated == false on cancel so the lazy
            // QueueRenderShellAsync path can retry.
            throw;
        }
        catch
        {
            // Asset-bundle load failed, file write failed, or Navigate threw.
            // The View has already restored _shellNavigated == false so the
            // lazy path will retry at the next user RequestRender. Swallow
            // here so the cache-hit consumer can fall through and let the
            // standard render pipeline observe the failure through
            // FallbackRequested -> RendererFailed -> failure-view surface.
        }
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
        ApplicateTrace.DiagMs(
            "pane-seq",
            "host-commit-enter",
            $"parent={(_currentParent is null ? "null" : _currentParent.GetType().Name)} reparented={_reparentedThisCycle} hasEverCommitted={_hasEverCommitted} viewBounds={View.Bounds.Width:F0}x{View.Bounds.Height:F0}");

        var transactionalCommit = _activeTransactionGeneration > 0;
        var armRevealGate = !transactionalCommit && _reparentedThisCycle && _hasEverCommitted;
        var modeSwitchDuration = CurrentModeSwitchDuration();
        HostCommitPreparing?.Invoke(
            this,
            new ApplicateHostCommitPreparingEventArgs(
                _currentParent,
                _warmupParent,
                transactionalCommit,
                _activeTransactionGeneration,
                _hasEverCommitted,
                modeSwitchDuration));
        _reparentedThisCycle = false;

        if (transactionalCommit)
        {
            _transactionNativeRevealGeneration = _activeTransactionGeneration;
            _transactionNativeRevealPending = true;
        }

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
            $"SharedHost.Commit gen={_activeGeneration} slot={(_currentParent is null ? "(null)" : _currentParent.GetType().Name)} revealGate={armRevealGate}");
        CommitCompleted?.Invoke(
            this,
            new ApplicateCommitCompletedEventArgs(
                _currentMode,
                _currentParent?.Bounds ?? default,
                _activeTransactionGeneration));

        if (_activeTransactionGeneration > 0 && _currentMode == ApplicateMode.Viewer)
        {
            View.RequestMinimapSettleProbe(_activeTransactionGeneration);
        }
        if (_activeTransactionGeneration > 0)
        {
            TransactionRendererSettleProbeReady?.Invoke(
                this,
                new ApplicateTransactionRendererSettleProbeEventArgs(
                    _activeTransactionGeneration,
                    _activeTransactionSkipsRendererFrameSettle));
        }
    }

    public bool RevealNativeWebViewForCommittedTransaction(long transactionGeneration)
    {
        if (transactionGeneration <= 0
            || !_transactionNativeRevealPending
            || transactionGeneration != _transactionNativeRevealGeneration
            || transactionGeneration != _activeTransactionGeneration
            || _state != HostState.Committed)
        {
            return false;
        }

        _transactionNativeRevealPending = false;
        _transactionNativeRevealGeneration = 0;
        View.CompleteNativeWebViewHiddenPaint();
        return true;
    }

    private void OnViewMinimapSettled(object? sender, ApplicateWebMinimapSettledEventArgs e)
    {
        RaiseMinimapSettled(e.TransactionGeneration, e.State);
    }

    private void OnViewModeToggleTransactionSettled(
        object? sender,
        ApplicateWebModeToggleSettledEventArgs e)
    {
        RaiseRendererSettled(e.TransactionGeneration);
    }

    private bool RaiseMinimapSettled(long transactionGeneration, ApplicateWebMinimapStateEventArgs? state)
    {
        if (transactionGeneration <= 0
            || transactionGeneration != _activeTransactionGeneration
            || _minimapSettledTransactionGeneration == transactionGeneration)
        {
            return false;
        }

        _minimapSettledTransactionGeneration = transactionGeneration;
        var args = state is null
            ? ApplicateMinimapSettledEventArgs.NotApplicable(transactionGeneration)
            : new ApplicateMinimapSettledEventArgs(transactionGeneration, state);
        MinimapSettled?.Invoke(this, args);
        return true;
    }

    private bool RaiseRendererSettled(long transactionGeneration)
    {
        if (transactionGeneration <= 0
            || transactionGeneration != _activeTransactionGeneration
            || _rendererSettledTransactionGeneration == transactionGeneration)
        {
            return false;
        }

        _rendererSettledTransactionGeneration = transactionGeneration;
        RendererSettled?.Invoke(this, new ApplicateRendererSettledEventArgs(transactionGeneration));
        return true;
    }

    private TimeSpan CurrentModeSwitchDuration()
    {
        var preferences = _failureRequest?.ReadingPreferences ?? ReadingPreferences.Default;
        return ApplicateMotion.ModeSwitchDuration(preferences);
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
    private long _activeTransactionGeneration;
    private long _minimapSettledTransactionGeneration;
    private long _rendererSettledTransactionGeneration;
    private long _transactionNativeRevealGeneration;
    private bool _transactionNativeRevealPending;
    private ApplicateMode _currentMode = ApplicateMode.Edit;

    public State CurrentState { get; private set; } = State.Parked;

    public long CurrentGeneration => _generation;

    public Panel? CurrentParent => _currentParent;

    public bool NativeWebViewVisible { get; private set; }

    public event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled;

    public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

    public event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled;

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
        => AttachTo(target, transactionGeneration: 0);

    public void AttachTo(Panel target, long transactionGeneration)
    {
        if (ReferenceEquals(_currentParent, target))
        {
            return;
        }

        var previousParent = _currentParent;
        _currentParent = target;
        if (transactionGeneration <= 0)
        {
            target.Opacity = 0.0;
            target.IsVisible = false;
        }
        else
        {
            NativeWebViewVisible = false;
        }
        CurrentState = State.Switching;

        if (previousParent is not null
            && !ReferenceEquals(previousParent, _warmupParent)
            && !previousParent.IsVisible)
        {
            previousParent.IsVisible = true;
            previousParent.Opacity = 1.0;
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
        if (previousParent is not null)
        {
            if (!previousParent.IsVisible)
            {
                previousParent.IsVisible = true;
            }
            previousParent.Opacity = 1.0;
        }
        CurrentState = State.Parked;
    }

    public long RequestRender()
        => RequestRender(
            transactionGeneration: 0,
            mode: ApplicateMode.Viewer);

    public long RequestRender(
        long transactionGeneration,
        ApplicateMode mode,
        bool minimapApplicable = true,
        bool fastPathCommit = false)
    {
        var newGen = ++_generation;
        _activeTransactionGeneration = transactionGeneration;
        _minimapSettledTransactionGeneration = 0;
        _rendererSettledTransactionGeneration = 0;
        _currentMode = mode;
        if (transactionGeneration <= 0
            && _currentParent is not null
            && !ReferenceEquals(_currentParent, _warmupParent))
        {
            _currentParent.IsVisible = false;
        }
        if (transactionGeneration > 0)
        {
            NativeWebViewVisible = false;
        }
        CurrentState = State.Switching;
        if (transactionGeneration > 0 && (mode == ApplicateMode.Edit || !minimapApplicable))
        {
            ApplyMinimapSettled(transactionGeneration, state: null);
        }
        if (fastPathCommit)
        {
            ApplyDocumentRendered(newGen);
        }
        return newGen;
    }

    public bool ApplyMinimapSettled(long transactionGeneration, ApplicateWebMinimapStateEventArgs? state)
    {
        if (transactionGeneration <= 0
            || transactionGeneration != _activeTransactionGeneration
            || _minimapSettledTransactionGeneration == transactionGeneration)
        {
            return false;
        }

        _minimapSettledTransactionGeneration = transactionGeneration;
        var args = state is null
            ? ApplicateMinimapSettledEventArgs.NotApplicable(transactionGeneration)
            : new ApplicateMinimapSettledEventArgs(transactionGeneration, state);
        MinimapSettled?.Invoke(this, args);
        return true;
    }

    public bool ApplyRendererSettled(long transactionGeneration)
    {
        if (transactionGeneration <= 0
            || transactionGeneration != _activeTransactionGeneration
            || _rendererSettledTransactionGeneration == transactionGeneration)
        {
            return false;
        }

        _rendererSettledTransactionGeneration = transactionGeneration;
        RendererSettled?.Invoke(this, new ApplicateRendererSettledEventArgs(transactionGeneration));
        return true;
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
            _currentParent.Opacity = 1.0;
        }
        if (_activeTransactionGeneration > 0)
        {
            _transactionNativeRevealGeneration = _activeTransactionGeneration;
            _transactionNativeRevealPending = true;
        }
        else
        {
            NativeWebViewVisible = true;
        }
        CurrentState = State.Committed;
        CommitCompleted?.Invoke(
            this,
            new ApplicateCommitCompletedEventArgs(
                _currentMode,
                _currentParent?.Bounds ?? default,
                _activeTransactionGeneration));
        return true;
    }

    public bool RevealNativeWebViewForCommittedTransaction(long transactionGeneration)
    {
        if (transactionGeneration <= 0
            || !_transactionNativeRevealPending
            || transactionGeneration != _transactionNativeRevealGeneration
            || transactionGeneration != _activeTransactionGeneration
            || CurrentState != State.Committed)
        {
            return false;
        }

        _transactionNativeRevealPending = false;
        _transactionNativeRevealGeneration = 0;
        NativeWebViewVisible = true;
        return true;
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Failure class for the shared host's <see cref="IApplicateSharedWebViewHost.RendererFailed"/>
/// event. Routes the three cases the design D3 identifies through one
/// discriminator so consumers (and the failure view) can pick the right
/// title, body, and retry policy.
/// </summary>
public enum ApplicateRendererFailureKind
{
    /// <summary>The WebView2 runtime is missing or its environment failed to
    /// initialise. Terminal for the session; retry is not offered.</summary>
    WebView2RuntimeMissing,

    /// <summary>A specific document failed to render — RenderAsync threw or
    /// the navigation completed unsuccessfully. Retry is offered.</summary>
    DocumentRenderFailed,

    /// <summary>Navigation aborted or a stale generation completed after a
    /// newer one started. Internal no-op per design D3 / Invariant I-4;
    /// the failure view normally should not be shown for this class.</summary>
    StaleNavigation,
}

/// <summary>
/// Mounting intent passed to <see cref="IApplicateSharedWebViewHost.AttachTo"/>.
/// The host forwards the flags to the underlying WebView when issuing the
/// next render request so the renderer JS can pick the correct chrome,
/// scroll, and wheel-proxy behaviour for viewer vs. edit-preview.
/// </summary>
/// <param name="ViewerChromeEnabled">Whether the renderer should paint the
/// viewer-chrome (toolbar / minimap rail). Edit-preview sets this to
/// <c>false</c>.</param>
/// <param name="DocumentScrollEnabled">Whether the renderer-internal
/// scroll surface is active. Edit-preview keeps this <c>true</c>.</param>
/// <param name="WheelProxyEnabled">Whether wheel events are proxied to the
/// host instead of consumed by the renderer body.</param>
public sealed record ApplicateWebMountIntent(
    bool ViewerChromeEnabled,
    bool DocumentScrollEnabled,
    bool WheelProxyEnabled);

/// <summary>
/// User-facing mode represented by the slot currently targeted by the shared
/// WebView host. The bridge uses this as a transaction payload, not as a
/// replacement for the view-model's edit-mode state.
/// </summary>
public enum ApplicateMode
{
    Viewer,
    Edit,
}

/// <summary>
/// Render request for the currently-attached slot. The host uses
/// <see cref="ApplicateWebMountIntent"/> from the prior <see cref="IApplicateSharedWebViewHost.AttachTo"/>
/// call to fold chrome / scroll / wheel-proxy flags into the underlying
/// <c>UpdateInputs</c> call.
/// </summary>
public sealed record ApplicateWebRenderRequest(
    ReadingPreferences ReadingPreferences,
    IImageSourceResolver? ImageSourceResolver,
    double AvailableContentWidth);

/// <summary>
/// Host-level minimap readiness signal tagged with the bridge's transaction
/// generation. <see cref="State"/> is null when the target mode has no minimap
/// participant and the transaction should treat minimap as not applicable.
/// </summary>
public sealed class ApplicateMinimapSettledEventArgs(
    long transactionGeneration,
    ApplicateWebMinimapStateEventArgs? state) : EventArgs
{
    public long TransactionGeneration { get; } = transactionGeneration;

    public ApplicateWebMinimapStateEventArgs? State { get; } = state;

    public bool IsApplicable => State is not null;

    public static ApplicateMinimapSettledEventArgs NotApplicable(long transactionGeneration)
        => new(transactionGeneration, null);
}

/// <summary>
/// Host-level commit signal emitted when the WebView has committed content for
/// the current parent and the host has run its existing layout/HWND commit path.
/// </summary>
public sealed class ApplicateCommitCompletedEventArgs(
    ApplicateMode mode,
    Rect bounds,
    long transactionGeneration) : EventArgs
{
    public ApplicateMode Mode { get; } = mode;

    public Rect Bounds { get; } = bounds;

    public long TransactionGeneration { get; } = transactionGeneration;
}

/// <summary>
/// Host-level renderer paint-settle signal emitted after the renderer acks
/// the transaction-scoped mode-settle probe.
/// </summary>
public sealed class ApplicateRendererSettledEventArgs(long transactionGeneration) : EventArgs
{
    public long TransactionGeneration { get; } = transactionGeneration;
}

/// <summary>
/// Failure context emitted by <see cref="IApplicateSharedWebViewHost.RendererFailed"/>.
/// Carries the failure class plus enough provenance to drive the failure
/// view and the diagnostics-copy payload.
/// </summary>
/// <param name="Kind">Failure class. See <see cref="ApplicateRendererFailureKind"/>.</param>
/// <param name="DocumentPath">Absolute path of the document whose render
/// failed, when known. May be null for runtime-missing failures.</param>
/// <param name="Timestamp">Capture moment of the failure event. UTC is
/// preferred for diagnostics payloads.</param>
/// <param name="Exception">Optional exception captured at the failure site.
/// Intended for the diagnostics-copy payload; consumers must not render
/// exception details into user-facing strings without redaction.</param>
public sealed record ApplicateRendererFailureEvent(
    ApplicateRendererFailureKind Kind,
    string? DocumentPath,
    DateTime Timestamp,
    Exception? Exception = null);

internal interface IApplicateModeRevealSignal
{
    event EventHandler? RevealCompleted;

    void SuppressNativeRendererForModeSwitch();
}

/// <summary>
/// Owns the single application-wide WebView2-backed document view.
///
/// The host parks the view in an offscreen "warmup" panel supplied via
/// <see cref="SetWarmupParent"/> so its WebView2 controller initialises
/// without showing the load state to the user. When a consumer (viewer
/// surface, edit-mode preview) wants the WebView to show in its slot it
/// calls <see cref="AttachTo"/>; the host reparents the view into the
/// consumer panel inside a single intentional-reparent scope so the WebView2
/// adapter, DOM, scroll, and viewport state survive.
///
/// Phase 4 introduces the slot-visibility invariant (design D1 / D7):
///
/// <list type="bullet">
///   <item><term>PARKED</term><description>Slot is the warmup panel.
///   <see cref="ApplicateWebMarkdownDocumentView"/> stays parented offscreen
///   to keep the WebView2 controller hot.</description></item>
///   <item><term>SWITCHING</term><description>Slot is the consumer slot but
///   it is held <c>IsVisible=false</c>. Avalonia's <c>NativeControlHost</c>
///   cascades that to <c>SetWindowPos(SWP_HIDEWINDOW)</c> on the WebView2
///   HWND, so the on-screen rectangle is owned by the parent Avalonia
///   background only — there is no native HWND in the airspace to leak a
///   stale frame.</description></item>
///   <item><term>COMMITTED</term><description>The WebView fired
///   <c>DocumentRendered</c> for the generation the host issued. The slot
///   transitions to <c>IsVisible=true</c>, the user sees the freshly painted
///   document, no flash.</description></item>
/// </list>
///
/// The host tags each render request with a monotonically increasing
/// generation token; <c>DocumentRendered</c> events with a stale generation
/// are dropped silently (Invariant I-4).
/// </summary>
public interface IApplicateModeTransactionHost
{
    /// <summary>
    /// Raised when the WebView pipeline fails — runtime missing, per-document
    /// render failure, or stale-navigation abort. Consumers subscribe to
    /// route the failure to their slot's failure-view surface.
    /// </summary>
    event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    /// <summary>
    /// Raised once per positive transaction generation when the minimap
    /// reservation has either reported its first state or is not applicable.
    /// </summary>
    event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled;

    /// <summary>
    /// Raised when the host reaches its renderer commit point for a render
    /// request, tagged with the transaction generation supplied by the bridge.
    /// </summary>
    event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

    /// <summary>
    /// Raised once per positive transaction generation after the renderer has
    /// applied settle-probe preferences and passed its post-paint ack point.
    /// </summary>
    event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled;

    /// <summary>
    /// Reveal the native WebView HWND for a committed bridge-owned mode
    /// transaction. Returns <c>false</c> when the generation is stale or the
    /// host is not waiting for a bridge reveal.
    /// </summary>
    bool RevealNativeWebViewForCommittedTransaction(long transactionGeneration);

    /// <summary>
    /// Hide the native renderer that belongs to the mode currently displayed
    /// before the bridge mutates the outer slot layout for a transaction.
    /// </summary>
    void SuppressNativeRendererForModeSwitch(ApplicateMode displayedMode);

    /// <summary>
    /// Restore the temporarily hidden displayed-mode native renderer after the
    /// bridge has finished the protected outer slot layout mutation.
    /// </summary>
    void RestoreNativeRendererAfterModeSwitchSuppression(ApplicateMode displayedMode);
}

public interface IApplicateSharedWebViewHost : IApplicateModeTransactionHost
{
    /// <summary>
    /// The shared WebView. Consumers may subscribe to non-render events
    /// (scroll state, minimap state, width drag, wheel, viewer interaction)
    /// directly and must unsubscribe on detach. Never null after host
    /// construction.
    /// </summary>
    ApplicateWebMarkdownDocumentView View { get; }

    /// <summary>
    /// Hide this host's native WebView HWND during a mode switch.
    /// </summary>
    void SuppressNativeRendererForModeSwitch();

    /// <summary>
    /// Register the offscreen warmup panel. Called once at app startup by
    /// the fork-owned main window. The view is mounted into this panel
    /// immediately so its WebView2 adapter can initialise without showing
    /// the load state to the user.
    /// </summary>
    void SetWarmupParent(Panel parent);

    /// <summary>
    /// Reparent the shared view into <paramref name="target"/> for the given
    /// mount intent. Idempotent against the currently-attached panel: a
    /// second call with the same panel updates the mount intent but does not
    /// re-reparent. The slot enters <c>SWITCHING</c> (or stays in
    /// <c>COMMITTED</c> if the next render request determines no work is
    /// needed); the host owns the slot's <c>IsVisible</c> transitions from
    /// this point forward until <see cref="ReturnToWarmup"/>.
    /// </summary>
    void AttachTo(Panel target, ApplicateWebMountIntent intent);

    /// <summary>
    /// Return the shared view to the warmup panel. The host transitions to
    /// <c>PARKED</c>; any active consumer slot is released and its
    /// <c>IsVisible</c> is restored to <c>true</c> so the parent panel can
    /// continue laying out its own content. Safe to call when already
    /// parked.
    /// </summary>
    /// <summary>Returns true when the host's WebView is currently parented under the given target panel.</summary>
    bool IsAttachedTo(Panel target);

    void ReturnToWarmup();

    /// <summary>
    /// Issue a new render generation against the currently-attached slot.
    /// The host bumps its generation token, transitions the slot to
    /// <c>SWITCHING</c> (hiding the slot), forwards <c>UpdateInputs</c> to
    /// the underlying WebView, and waits for the matching
    /// <c>DocumentRendered</c> before transitioning to <c>COMMITTED</c>.
    /// </summary>
    /// <param name="source">Document source to render. <c>null</c> requests
    /// an empty document (about:blank-equivalent).</param>
    /// <param name="request">Render parameters folded with the current
    /// mount intent.</param>
    void RequestRender(MarkdownSource? source, ApplicateWebRenderRequest request);

    /// <summary>
    /// Issue a render request tagged with the bridge-owned mode-transaction
    /// generation. The two-argument overload remains for non-transactional
    /// callers and maps to generation 0.
    /// </summary>
    void RequestRender(MarkdownSource? source, ApplicateWebRenderRequest request, long transactionGeneration);

    /// <summary>
    /// Prime an inactive, offscreen consumer slot before the user-visible mode
    /// switch. Unlike the normal cold render path, this keeps the attached
    /// parent visible so WebView2 can produce layout and DocumentRendered
    /// signals while the outer slot is positioned outside the viewport.
    /// </summary>
    void RequestInactivePrimeRender(MarkdownSource? source, ApplicateWebRenderRequest request);

    /// <summary>
    /// Retry the last failed render in the current slot. Re-uses the source
    /// and request from the in-flight generation at the failure moment so
    /// transient renderer-side faults can recover without consumer
    /// involvement. No-op when there is no captured failure context or the
    /// failure was terminal (<see cref="ApplicateRendererFailureKind.WebView2RuntimeMissing"/>).
    /// </summary>
    void RetryRender();

    /// <summary>
    /// Pre-warm the renderer shell at app boot so the first user
    /// <see cref="RequestRender"/> does not pay the ~502 ms
    /// <c>navigate-shell → shell-ready</c> gap on the user-visible critical
    /// path (PE r2 item A). The host parks the view under the warmup panel
    /// and stays in <c>PARKED</c> throughout — no consumer slot is touched,
    /// no visibility is flipped. Idempotent: re-entrant calls after the
    /// shell is already navigated return immediately. Safe to call before
    /// any consumer has attached.
    ///
    /// <para>Emits diagnostic markers in the <c>startup-webview</c> group:
    /// <c>shell-prewarm-start</c> on entry, <c>shell-prewarm-ready</c> after
    /// <c>document-ready</c> IPC has unlocked the shell-ready TCS, or
    /// <c>shell-prewarm-failed</c> with the exception type on failure. On
    /// failure the lazy <see cref="ApplicateWebMarkdownDocumentView"/>
    /// shell-init path takes over at the next user render — the failure is
    /// not propagated past this Task.</para>
    /// </summary>
    Task PreWarmShellAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Await the shell-ready rendezvous, driving shell navigation if it has
    /// not started yet. Returns a completed task when the shell has already
    /// navigated and the <c>document-ready</c> IPC has fired; otherwise
    /// converges on the same in-flight TCS the pre-warm and lazy shell-init
    /// paths complete (idempotent — never issues a duplicate Navigate).
    ///
    /// <para>Used by the document-load fast-path (EarlyDocumentCache hit) to
    /// defer publication of Document / State changes until the renderer
    /// pipeline can actually consume them, closing the cache-hit race where
    /// Document=true is published before the WebView2 environment finishes
    /// initialising and a later session-restoration / edit-mode reconcile
    /// reparents the renderer mid-pipeline.</para>
    ///
    /// <para>Safe to call from any thread; the underlying shell-ready TCS is
    /// created with <c>TaskCreationOptions.RunContinuationsAsynchronously</c>,
    /// so callers can <c>await</c> on the UI thread without re-entrancy
    /// hazards. Returns immediately when shell mode is disabled or the view
    /// has been disposed — neither case represents a real readiness signal,
    /// but blocking would deadlock the consumer.</para>
    /// </summary>
    Task WaitForShellReadyAsync(CancellationToken cancellationToken = default);
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Behavioural tests against the REAL <see cref="ApplicateSharedWebViewHost"/>
/// (not the pure-data <c>ApplicateSharedWebViewHostStateMachine</c> mirror, and
/// not a source-text idiom). Design:
/// work-items/active/2026-07-21-bug-register-remainder/design-real-host-test-seam.md
/// (Option D).
///
/// The host is constructed headless under the shared Avalonia headless session.
/// Its state machine and failure routing are driven synchronously through the
/// existing internal <c>Raise*ForTesting</c> seams (revived by these tests) and
/// observed through the <c>Debug*ForTesting</c> observers. The real render
/// pipeline (which needs a live WebView2) is deliberately NOT exercised: the
/// spy renderer and the stub shell-asset factory both return never-completing
/// tasks, so the host's fire-and-forget <c>QueueRender</c> parks at its first
/// <c>await</c> before ever touching <c>_webView.Navigate</c>. Every test body
/// is fully synchronous, so no parked continuation runs mid-assertion.
///
/// I-A (no-timers): all drive is synchronous seam calls + immediate Debug reads;
/// no Task.Delay / Thread.Sleep / Timer anywhere in this file.
/// </summary>
public sealed class ApplicateSharedWebViewHostRealHostTests
{
    private static readonly MarkdownSource DocA =
        new(@"C:\docs\a.md", "a.md", "# Alpha\n\nbody a");

    private static readonly MarkdownSource DocB =
        new(@"C:\docs\b.md", "b.md", "# Beta\n\nbody b");

    private static ApplicateWebRenderRequest Request() =>
        new(ReadingPreferences.Default, ImageSourceResolver: null, AvailableContentWidth: 800);

    private static ApplicateWebMountIntent ViewerIntent() =>
        new(ViewerChromeEnabled: true, DocumentScrollEnabled: true, WheelProxyEnabled: false);

    private static ApplicateSharedWebViewHost NewHost() =>
        new(new ParkingRenderer(), new ParkingShellAssetFactory(), new ApplicateRenderedBodyCache());

    private static void RunOnHost(Action<ApplicateSharedWebViewHost> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() => body(NewHost()), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    // ----- Step 1: the GATING construction smoke (design §0 / I-D / FM-1) -------
    // If the real host cannot be constructed headless, the whole Option-D
    // approach fails and the design's single ASSUMPTION (UNVERIFIED) is wrong.
    // This is the go/no-go for everything below.

    [Fact]
    public void RealHostConstructsHeadlessWithoutALiveWebView2()
    {
        RunOnHost(host =>
        {
            Assert.NotNull(host.View);
            Assert.Equal("Parked", host.DebugStateForTesting);
            Assert.Equal(0, host.DebugGenerationForTesting);
            Assert.Null(host.DebugCurrentParentForTesting);
        });
    }

    // ----- Probe: driving the real host does not touch the render pipeline ------
    // RequestRender forwards UpdateInputs -> QueueRender (fire-and-forget). With a
    // parking renderer + parking factory the async render parks before any
    // _webView.Navigate, so the synchronous body must neither throw nor see a
    // spurious RendererFailed fire.

    [Fact]
    public void DrivingTheRealHostRaisesNoSpuriousRendererFailure()
    {
        RunOnHost(host =>
        {
            var failures = 0;
            host.RendererFailed += (_, _) => failures++;

            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());
            host.RequestRender(DocA, Request());

            Assert.Equal(0, failures);
            Assert.Equal("Switching", host.DebugStateForTesting);
        });
    }

    // ----- State machine on the real host (real Parked/Switching/Committed) -----

    [Fact]
    public void RealHostCommitsOnDocumentRenderedAndTracksSlot()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            Assert.Same(warmup, host.DebugCurrentParentForTesting);

            host.AttachTo(slot, ViewerIntent());
            Assert.Equal("Switching", host.DebugStateForTesting);
            Assert.Same(slot, host.DebugCurrentParentForTesting);

            host.RequestRender(DocA, Request());
            Assert.Equal("Switching", host.DebugStateForTesting);

            host.RaiseDocumentRenderedForTesting();

            Assert.Equal("Committed", host.DebugStateForTesting);
            Assert.Same(slot, host.DebugCurrentParentForTesting);
        });
    }

    [Fact]
    public void RealHostDropsStaleDocumentRenderedAfterASecondRequest()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            var firstGen = host.DebugGenerationForTesting;
            host.RaiseDocumentRenderedForTesting();
            Assert.Equal("Committed", host.DebugStateForTesting);

            // A new request re-enters Switching and bumps the generation.
            host.RequestRender(DocB, Request());
            var secondGen = host.DebugGenerationForTesting;
            Assert.True(secondGen > firstGen);
            Assert.Equal("Switching", host.DebugStateForTesting);

            // Commit is generation-agnostic from the View, but the host's I-4
            // gate only commits while Switching. Driving DocumentRendered once
            // commits the current generation.
            host.RaiseDocumentRenderedForTesting();
            Assert.Equal("Committed", host.DebugStateForTesting);

            // A further DocumentRendered while already Committed is dropped
            // (state stays Committed, not re-committed) — the I-4 gate on the
            // REAL host, not the mirror.
            host.RaiseDocumentRenderedForTesting();
            Assert.Equal("Committed", host.DebugStateForTesting);
        });
    }

    // ----- Failure routing on the real host -------------------------------------

    [Fact]
    public void RealHostFallbackRequestRoutesToDocumentRenderFailedWithTheRenderedDocumentPath()
    {
        RunOnHost(host =>
        {
            ApplicateRendererFailureEvent? received = null;
            var count = 0;
            host.RendererFailed += (_, e) =>
            {
                received = e;
                count++;
            };

            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());
            host.RequestRender(DocA, Request());
            host.RaiseDocumentRenderedForTesting();

            host.RaiseFallbackRequestedForTesting();

            Assert.Equal(1, count);
            Assert.NotNull(received);
            Assert.Equal(ApplicateRendererFailureKind.DocumentRenderFailed, received!.Kind);
            Assert.Equal(DocA.Path, received.DocumentPath);
            Assert.Null(received.Exception);
        });
    }

    // ----- Retry guard (no captured context) ------------------------------------

    [Fact]
    public void RetryRenderBeforeAnyRenderIsANoOpAndBumpsNoGeneration()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            var genBefore = host.DebugGenerationForTesting;

            // No RequestRender has run, so _lastRenderRequest is null: RetryRender
            // must early-return without throwing and without bumping generation.
            host.RetryRender();

            Assert.Equal(genBefore, host.DebugGenerationForTesting);
        });
    }

    // ----- Retry after a post-ready crash re-issues the render (2f78dda) --------
    // The render CONTEXT must survive Commit so RetryRender is not a no-op at its
    // null guard. Here we observe the RETRY EFFECT: generation bumps and the host
    // re-enters Switching. The DebugLastRenderSourceForTesting observer (added by
    // this work item) is asserted in the sibling context-survival test once it
    // exists. FM-2 probe: RetryRender must not throw a live-WebView2 exception.

    [Fact]
    public void RetryRenderAfterAPostReadyFallbackReIssuesTheRenderWithoutThrowing()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            host.RaiseDocumentRenderedForTesting();
            Assert.Equal("Committed", host.DebugStateForTesting);
            var committedGen = host.DebugGenerationForTesting;

            // A WebView2 process failure lands AFTER the clean commit.
            host.RaiseFallbackRequestedForTesting();

            // Retry re-issues the render for the document that died. FM-2 probe:
            // this must be clean headless (the re-render parks at the factory
            // await; it does not reach the native post path), so no ctor-overload
            // delivery-hooks seam is required.
            host.RetryRender();

            Assert.True(host.DebugGenerationForTesting > committedGen);
            Assert.Equal("Switching", host.DebugStateForTesting);
        });
    }

    // ----- Retry CONTEXT survives commit (the core 2f78dda behaviour) -----------
    // This is the guarantee the source-text mirror could only pin STRUCTURALLY.
    // Here it is behavioural: after a REAL Commit the retained context is still
    // the committed document, and RetryRender re-issues with it. A null-out of
    // the context on commit (or on any path into commit, even under a renamed
    // field) turns these assertions red — which a source-text match cannot.

    [Fact]
    public void RenderContextSurvivesCommitSoRetryReRendersTheDiedDocument()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            var request = Request();
            host.RequestRender(DocA, request);

            // Captured on the render request, before any commit.
            Assert.Same(DocA, host.DebugLastRenderSourceForTesting);
            Assert.Same(request, host.DebugLastRenderRequestForTesting);

            // A REAL commit runs (host state -> Committed) and the context is NOT
            // destroyed by success — the whole point of 2f78dda.
            host.RaiseDocumentRenderedForTesting();
            Assert.Equal("Committed", host.DebugStateForTesting);
            Assert.Same(DocA, host.DebugLastRenderSourceForTesting);
            Assert.Same(request, host.DebugLastRenderRequestForTesting);

            var committedGen = host.DebugGenerationForTesting;

            // A WebView2 process failure lands after the clean commit; retry is
            // the only affordance for a user who stays on the same document.
            host.RaiseFallbackRequestedForTesting();
            host.RetryRender();

            // Retry re-issued the render for the SAME document, using the
            // surviving context.
            Assert.True(host.DebugGenerationForTesting > committedGen);
            Assert.Equal("Switching", host.DebugStateForTesting);
            Assert.Same(DocA, host.DebugLastRenderSourceForTesting);
            Assert.Same(request, host.DebugLastRenderRequestForTesting);
        });
    }

    // ----- Revive the RaiseRendererFailed(failure) drive seam -------------------

    [Fact]
    public void RaiseRendererFailedForwardsTheSuppliedFailureEventVerbatim()
    {
        RunOnHost(host =>
        {
            ApplicateRendererFailureEvent? received = null;
            var count = 0;
            host.RendererFailed += (_, e) =>
            {
                received = e;
                count++;
            };

            var failure = new ApplicateRendererFailureEvent(
                Kind: ApplicateRendererFailureKind.WebView2RuntimeMissing,
                DocumentPath: DocA.Path,
                Timestamp: DateTime.UtcNow,
                Exception: null);

            host.RaiseRendererFailed(failure);

            Assert.Equal(1, count);
            Assert.Same(failure, received);
        });
    }

    // ----- Headings consumer-owned debt pull (design REVISION 3,
    // work-items/active/2026-07-25-toc-empty-on-open/design.md D1) -----------
    // REVISION 3 moves the trigger from the HOST (ApplicateSharedWebViewHost.
    // RequestRender, gated on inputUpdateAction) to the CONSUMER, which is the
    // only party that knows whether its own heading collection is empty. The
    // production call is ApplicateWebMarkdownDocumentView.
    // TryRaiseRetainedHeadingsForConsumerDebt, invoked by ApplicateViewerView /
    // ApplicateEditPreviewView AFTER their own RequestRender call. These tests
    // drive the REAL View directly through that same entry point rather than
    // relying on RequestRender to trigger anything -- after design D1.d,
    // RequestRender contains ZERO heading LOGIC (design claim 3). Corrected
    // 2026-07-26 (round-3 gate finding A4): grep "Headings" in
    // ApplicateSharedWebViewHost.cs does NOT return nothing -- it returns two
    // matches, both comment text pointing at this pull (the "Headings are NOT
    // re-emitted from here" header and the TryRaiseRetainedHeadingsForConsumerDebt
    // pointer). The substance stands (no heading LOGIC remains in that file);
    // only the earlier "returns nothing" phrasing was false.
    //
    // Feeds the real IPC message sequence a genuine renderer would produce for a
    // fresh document load directly into the REAL View via HandleWebMessageBody --
    // the same injection seam IpcContractTests uses. No live WebView2 needed:
    // QueueRenderShellAsync parks at the ParkingShellAssetFactory await, but
    // _hasLoadedDocument / _awaitingLayoutReady / _activeRevealRenderId are all
    // set synchronously inside QueueRender before that await, so feeding
    // document-ready / minimap-state / layout-ready afterward drives the real
    // gates for real.
    //
    // Shell mode is the process default here (ApplicateRendererShellMode.IsEnabled
    // defaults true, and NewHost() always supplies a non-null shellAssetFactory),
    // so the FIRST document-ready is the shell's own empty-page ready (consumed
    // silently) and a SECOND is required to promote _hasLoadedDocument -- design
    // §8.4 flags this exact message count as an ASSUMPTION (UNVERIFIED); it is
    // resolved here by asserting HasLoadedDocumentForSource as an explicit
    // precondition rather than assuming the recipe worked.

    private static void DriveViewToLoadedAndPainted(ApplicateWebMarkdownDocumentView view)
    {
        if (!view.HasLoadedDocumentForReveal)
        {
            view.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "document-ready" }));
        }
        if (!view.HasLoadedDocumentForReveal)
        {
            view.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "document-ready" }));
        }
        view.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "minimap-state", visible = false }));
        view.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "layout-ready", cached = false }));
    }

    private static void PostHeadings(ApplicateWebMarkdownDocumentView view, params string[] headingIds)
    {
        var headings = new List<object>();
        foreach (var id in headingIds)
        {
            headings.Add(new { id, level = 1, text = id });
        }

        view.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "headings-updated", headings }));
    }

    // The end-to-end guard (design §12 claim 1) -- the actual regression this
    // revision fixes. Per design §8 this must be RED at 98f99ab (empirically
    // confirmed against a scratch worktree pinned at that commit before this
    // fix was implemented: Assert.Equal(1, fireCount) failed with Actual: 0)
    // and GREEN once the CONSUMER, not RequestRender's inputUpdateAction,
    // drives the pull.
    [Fact]
    public void ReopenWithAChangedAvailableContentWidthAndAnEmptyTocRestoresTheHeadings()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostHeadings(host.View, "intro", "body");
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            IReadOnlyList<DocumentHeading>? reemitted = null;
            var fireCount = 0;
            host.View.HeadingsChanged += (_, headings) =>
            {
                reemitted = headings;
                fireCount++;
            };

            // Reopen the SAME document with a CHANGED AvailableContentWidth --
            // gate finding F2 (98f99ab): DetermineInputUpdateAction resolves
            // ApplyLivePreferences here, not None -- the reachable trigger
            // revision 2's host-side narrowing missed (a reopen from welcome
            // recents after a window resize, or any preference/width delta
            // while the TOC is empty). The production call site is a
            // CONSUMER's own pull, so it is driven directly here rather than
            // through RequestRender.
            var changedWidthRequest = new ApplicateWebRenderRequest(
                ReadingPreferences.Default, ImageSourceResolver: null, AvailableContentWidth: 900);
            host.RequestRender(DocA, changedWidthRequest);
            var pulled = host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                DocA, consumerHasHeadingDebt: true);

            Assert.True(pulled);
            Assert.Equal(1, fireCount);
            Assert.NotNull(reemitted);
            Assert.Equal(2, reemitted!.Count);
            Assert.Equal("intro", reemitted[0].Id);
            Assert.Equal("body", reemitted[1].Id);
        });
    }

    // Re-pointed at the new entry point (design §8 "three more must be
    // re-pointed"). Same scenario as the end-to-end guard above but via a
    // byte-identical reload (same width) -- action=None rather than
    // ApplyLivePreferences -- proving the pull covers both reachable
    // DetermineInputUpdateAction outcomes, not just one of them.
    [Fact]
    public void NoOpReloadReemitsRetainedHeadingsForTheSameSource()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostHeadings(host.View, "intro", "body");

            // Explicit precondition (design §8.4) -- prove the document is
            // actually loaded AND painted for DocA before relying on it.
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            IReadOnlyList<DocumentHeading>? reemitted = null;
            var fireCount = 0;
            host.View.HeadingsChanged += (_, headings) =>
            {
                reemitted = headings;
                fireCount++;
            };

            // Same source, same content -> UpdateInputs returns
            // None/ApplyLivePreferences, not Render -- exactly the no-op
            // reload this design's root names (DetermineInputUpdateAction).
            // The consumer pull runs AFTER RequestRender (invariant I9),
            // passing consumerHasHeadingDebt: true to simulate a TOC emptied by
            // a prior Document=null transition (ClearDocumentHeadings).
            host.RequestRender(DocA, Request());
            var pulled = host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                DocA, consumerHasHeadingDebt: true);

            Assert.True(pulled);
            Assert.Equal(1, fireCount);
            Assert.NotNull(reemitted);
            Assert.Equal(2, reemitted!.Count);
            Assert.Equal("intro", reemitted[0].Id);
            Assert.Equal("body", reemitted[1].Id);
        });
    }

    // Re-pointed at the new entry point. No stale overwrite: the retain-at-
    // ingress write in HandleHeadingsUpdatedMessage is a single writer -- a
    // second headings-updated for the SAME source (e.g. a live split-editor
    // content update that keeps the document path identical) must replace the
    // retained payload, not accumulate alongside it, and the pull must hand
    // out the LATEST one.
    [Fact]
    public void NoOpReloadReemitsTheLatestRetainedHeadingsNotAStaleEarlierList()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostHeadings(host.View, "first");
            PostHeadings(host.View, "second-a", "second-b");
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            IReadOnlyList<DocumentHeading>? reemitted = null;
            host.View.HeadingsChanged += (_, headings) => reemitted = headings;

            host.RequestRender(DocA, Request());
            var pulled = host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                DocA, consumerHasHeadingDebt: true);

            Assert.True(pulled);
            Assert.NotNull(reemitted);
            Assert.Equal(2, reemitted!.Count);
            Assert.Equal("second-a", reemitted[0].Id);
            Assert.Equal("second-b", reemitted[1].Id);
        });
    }

    // G1 (I1): an empty retained payload (a heading-less document; the
    // renderer's extractAndPostHeadings posts headings: [] when there is no
    // main.mm-document or no surviving heading) must never reach the TOC via
    // the pull -- the "no-retained-payload" guard suppresses it. Renamed from
    // NoOpReloadSuppressesReemitWhenRetainedPayloadIsEmpty and re-pointed at
    // the new entry point (its RequestRender-only mechanism vanished under
    // D1.d the same way the FastPath tests below did).
    [Fact]
    public void G1PullIsSuppressedForAnEmptyRetainedPayload()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostHeadings(host.View);
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            var fireCount = 0;
            host.View.HeadingsChanged += (_, _) => fireCount++;

            host.RequestRender(DocA, Request());
            var pulled = host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                DocA, consumerHasHeadingDebt: true);

            Assert.False(pulled);
            Assert.Equal(0, fireCount);
        });
    }

    // G2 (I3, INVERTED from RepeatedNoOpReloadsReemitTheSameRetainedHeadingListInstance,
    // design §8 "the trap"): the prior test PINNED the churn as expected
    // (Assert.Equal(3, observed.Count) for three no-op reloads against a
    // POPULATED TOC) -- RED at 98f99ab is this file's own baseline run of that
    // exact assertion (recorded: it passed with count=3, i.e. the OLD
    // mechanism fired on every repeat despite a populated TOC). Under D1 the
    // consumer's own consumerHasHeadingDebt reading is true ONLY the first time
    // (before the TOC is refilled) -- a repeat sync with an ALREADY-POPULATED
    // TOC passes consumerHasHeadingDebt: false and must not pull at all.
    [Fact]
    public void G2RepeatedSyncsWithAPopulatedTocDoNotPull()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostHeadings(host.View, "intro", "body");
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            var observed = new List<IReadOnlyList<DocumentHeading>>();
            host.View.HeadingsChanged += (_, headings) => observed.Add(headings);

            // Three repeated syncs, each passing consumerHasHeadingDebt: false --
            // the TOC is already populated (as it would be after the FIRST
            // successful pull in production), so none of these three may
            // pull, unlike the pre-fix mechanism which fired on every one.
            host.RequestRender(DocA, Request());
            host.View.TryRaiseRetainedHeadingsForConsumerDebt(DocA, consumerHasHeadingDebt: false);
            host.RequestRender(DocA, Request());
            host.View.TryRaiseRetainedHeadingsForConsumerDebt(DocA, consumerHasHeadingDebt: false);
            host.RequestRender(DocA, Request());
            host.View.TryRaiseRetainedHeadingsForConsumerDebt(DocA, consumerHasHeadingDebt: false);

            Assert.Empty(observed);
        });
    }

    // G10 (D6, design REVISION 4 §2/§3, INVERTED from
    // TransactionalRequestRenderDoesNotReemitHeadings): DEFECT-2 -- a mode
    // transaction cancels existing debt (Ctrl+E inside the 80ms defer window
    // invalidates the scheduled ScheduleApply callback,
    // ApplicateDeferredHeadingUpdater.cs) and then the ONLY replacement pull
    // used to be suppressed by a transactionGeneration != 0 conjunct that
    // this test PINNED as expected behaviour (Assert.False(pulled),
    // Assert.Equal(0, fireCount)) -- exactly the same trap
    // RepeatedNoOpReloadsReemitTheSameRetainedHeadingListInstance pinned
    // before ca045b4 inverted it (now G2). The guard's stated rationale was
    // verified FALSE (design §1 DEFECT-2): the debt predicate above already
    // forbids firing on a populated collection, so the conjunct blocked the
    // only settlement opportunity rather than preventing churn. The
    // transactionGeneration parameter is deleted from the pull entirely
    // (D6.a); Shape A's OWN transactionGeneration == 0 gate at
    // ApplicateSharedWebViewHost.cs:359 is untouched and remains the sole
    // guard against re-emitting reveal-ready mid-transaction -- it does not
    // apply to this consumer-owned pull.
    [Fact]
    public void PullSettlesDebtDuringAModeTransaction()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostHeadings(host.View, "intro");
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            var fireCount = 0;
            IReadOnlyList<DocumentHeading>? reemitted = null;
            host.View.HeadingsChanged += (_, headings) =>
            {
                reemitted = headings;
                fireCount++;
            };

            // A mode transaction (Ctrl+E toggle) drives RequestRender with a
            // POSITIVE transactionGeneration (Shape A's own gate, unaffected
            // by D6) -- the consumer's own collection was cleared by the
            // invalidated defer timer, so it reports debt.
            host.RequestRender(DocA, Request(), transactionGeneration: 1);
            var pulled = host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                DocA, consumerHasHeadingDebt: true);

            Assert.True(pulled);
            Assert.Equal(1, fireCount);
            Assert.NotNull(reemitted);
            Assert.Single(reemitted!);
            Assert.Equal("intro", reemitted[0].Id);
        });
    }

    // G6 (I10): the pull is inert when the host holds no painted document at
    // all -- a brand-new host, nothing ever rendered. HasLoadedDocumentForSource
    // is false for every source, so the guard ladder's first real check must
    // reject before ever touching _lastHeadings.
    [Fact]
    public void G6PullIsSuppressedWhenTheHostHoldsNoPaintedDocument()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            var pulled = host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                DocA, consumerHasHeadingDebt: true);

            Assert.False(pulled);
        });
    }

    // G4b (I7, INV-ORDER, C# half -- design §8 harness caveat): every other
    // test in this file feeds layout-ready BEFORE calling PostHeadings (via
    // DriveViewToLoadedAndPainted), the REVERSE of production order, so none
    // of them are evidence for INV-ORDER. This test feeds headings-updated
    // BEFORE layout-ready -- while the render is still in flight
    // (_hasLoadedDocument is false in shell mode until layout-ready runs) --
    // and proves the pull cannot succeed during that window even though a
    // (premature) payload has already been retained at ingress. This is the
    // structural backstop INV-ORDER relies on: HasLoadedDocumentForSource is
    // the gate, and it is false for the whole in-flight duration.
    [Fact]
    public void G4bRetainedHeadingsAreNotPullableWhileARenderIsInFlight()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());

            // Drive as far as document-ready (x2, per the shell-mode recipe)
            // + minimap-state -- deliberately NOT layout-ready.
            if (!host.View.HasLoadedDocumentForReveal)
            {
                host.View.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "document-ready" }));
            }
            if (!host.View.HasLoadedDocumentForReveal)
            {
                host.View.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "document-ready" }));
            }
            host.View.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "minimap-state", visible = false }));

            // Headings arrive BEFORE layout-ready -- the reverse order this
            // test exists to cover.
            PostHeadings(host.View, "intro", "body");

            // Explicit precondition: the render must still be in flight.
            Assert.False(host.View.HasLoadedDocumentForSource(DocA));

            var fireCount = 0;
            host.View.HeadingsChanged += (_, _) => fireCount++;

            var pulled = host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                DocA, consumerHasHeadingDebt: true);

            Assert.False(pulled);
            Assert.Equal(0, fireCount);

            // Complete the render so the host is left in a clean state.
            host.View.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "layout-ready", cached = false }));
        });
    }

    // Renamed from NoOpReloadSkipsARetainedPayloadFromASupersededPageGeneration
    // (design §2 F1 / §8): reclassified as a DEFENSIVE-BRANCH PIN, not
    // behavioural coverage -- _lastHeadingsCaptureGeneration is a backstop
    // against an EARLY-captured payload from a superseded generation, not the
    // guarantee that makes the pull correct today (that is INV-ORDER, covered
    // by G4a/G4b). This drives DocB through TWO separate page generations for
    // the SAME Source value (DocB -> DocA -> DocB again, mirroring a
    // same-Source page recreation such as a WebView2 crash+retry or legacy
    // MARKMELLO_RENDERER_SHELL_MODE=0's per-render Navigate) and leaves the
    // SECOND DocB generation without its own fresh headings-updated.
    // Source-equality alone cannot tell the two DocB generations apart (both
    // are literally the same MarkdownSource), so without this guard the pull
    // would hand out the FIRST generation's retained payload for the SECOND
    // generation's reload -- a genuinely superseded page's payload leaking
    // forward. _lastHeadingsCaptureGeneration CAN tell them apart, because
    // _activeRevealRenderId bumps on every QueueRender even when Source itself
    // reads the same both times.
    [Fact]
    public void NoOpReloadSkipsAPayloadCapturedUnderASupersededGeneration()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            // DocB's FIRST page generation: loads, paints, and gets its own
            // real headings.
            host.RequestRender(DocB, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostHeadings(host.View, "b-first-generation");
            Assert.True(host.View.HasLoadedDocumentForSource(DocB));

            // An intervening different document -- a real render, so Source
            // moves away from DocB and _activeRevealRenderId bumps.
            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);

            // DocB is reopened -- Source changes DocA -> DocB again, so this
            // IS a real render (sourceChanged=true), a NEW page generation
            // for the SAME MarkdownSource value. Deliberately do NOT post a
            // fresh headings-updated for this second generation, so the
            // retained payload is still the FIRST generation's if nothing
            // guards against it.
            host.RequestRender(DocB, Request());
            DriveViewToLoadedAndPainted(host.View);
            Assert.True(host.View.HasLoadedDocumentForSource(DocB));

            var fireCount = 0;
            host.View.HeadingsChanged += (_, _) => fireCount++;

            // A genuine no-op reload of the SECOND DocB generation.
            host.RequestRender(DocB, Request());
            var pulled = host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                DocB, consumerHasHeadingDebt: true);

            Assert.False(pulled);
            Assert.Equal(0, fireCount);
        });
    }

    // Rewritten at the pull's entry point (design §8 "the trap" / §15
    // ratification: REWRITE, do not delete). After D1.d, RequestRender itself
    // contains no heading logic at all, so these zero-fire assertions would
    // otherwise pass VACUOUSLY -- the mechanism they used to guard left
    // RequestRender entirely. The real reason nothing fires for a
    // ALREADY-populated TOC across a run of width-only fast-path renders is
    // consumerHasHeadingDebt: false, driven here explicitly after every request.
    [Fact]
    public void FastPathRequestRenderWithOnlyAWidthChangeDoesNotReemitHeadings()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostHeadings(host.View, "intro");
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            var fireCount = 0;
            host.View.HeadingsChanged += (_, _) => fireCount++;

            // Each request changes ONLY AvailableContentWidth relative to the
            // previous one (Request() uses 800; none of these repeat it or
            // each other), so Source/ImageSourceResolver/ReadingPreferences
            // never change -- UpdateInputs must return ApplyLivePreferences
            // every time, never None and never Render. The consumer's own TOC
            // is already populated (consumerHasHeadingDebt: false), matching what
            // ApplicateViewerView.IssueRenderRequest would actually observe.
            for (var width = 700.0; width <= 780.0; width += 20.0)
            {
                host.RequestRender(
                    DocA,
                    new ApplicateWebRenderRequest(ReadingPreferences.Default, ImageSourceResolver: null, AvailableContentWidth: width));
                host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                    DocA, consumerHasHeadingDebt: false);
            }

            Assert.Equal(0, fireCount);
        });
    }

    private static readonly int[] DistinctFontSizesForNoReemitTest = [20, 22, 24];

    [Fact]
    public void FastPathRequestRenderWithOnlyAFontSizeChangeDoesNotReemitHeadings()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostHeadings(host.View, "intro");
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            var fireCount = 0;
            host.View.HeadingsChanged += (_, _) => fireCount++;

            // Each request changes ONLY the reading preferences' FontSize
            // (Source/AvailableContentWidth/ImageSourceResolver held fixed),
            // so UpdateInputs must return ApplyLivePreferences every time. As
            // above, consumerHasHeadingDebt: false is the real reason nothing
            // fires now that the mechanism lives at the pull, not in
            // RequestRender.
            foreach (var fontSize in DistinctFontSizesForNoReemitTest)
            {
                host.RequestRender(
                    DocA,
                    new ApplicateWebRenderRequest(
                        ReadingPreferences.Default with { FontSize = fontSize },
                        ImageSourceResolver: null,
                        AvailableContentWidth: 800));
                host.View.TryRaiseRetainedHeadingsForConsumerDebt(
                    DocA, consumerHasHeadingDebt: false);
            }

            Assert.Equal(0, fireCount);
        });
    }

    // ----- Minimap-reservation consumer-owned debt pull -------------------------
    // Second Shape-B member of the no-op-reload state-debt class
    // (work-items/decisions/2026-07-26-noop-reload-signal-reemit-ownership.md,
    // option 1 "retain at ingress"). Runtime-proven root (probe 2026-07-27, five
    // valid repeats): after closing all tabs and reopening the same file from
    // Recents, the renderer still holds the document with the minimap drawn and
    // posts NO minimap-state (its lastPostedMinimapState dedupe ledger suppresses
    // the identical state), while the consumer zeroed its reservation on the
    // document-identity transition. At a binding clamp ceiling the document
    // column then widens and its left inset goes to 0, so the text runs into the
    // strip the minimap still occupies.
    //
    // Same harness contract as the heading pull above: the production call site
    // is ApplicateViewerView.IssueRenderRequest AFTER its own RequestRender
    // (invariant I9), driven here directly through host.View, with the consumer's
    // debt reading supplied explicitly.

    private static void PostMinimapState(
        ApplicateWebMarkdownDocumentView view,
        bool visible,
        double reservedWidth)
        => view.HandleWebMessageBody(JsonSerializer.Serialize(
            new { type = "minimap-state", visible, reservedWidth }));

    // Same recipe as DriveViewToLoadedAndPainted MINUS its minimap-state post.
    // Required by the tests below that must leave a generation with NO
    // minimap-state of its own: the shared helper's `visible = false` post would
    // itself overwrite the retention, so those tests would be rejected by the
    // retained-reservation guard and prove nothing about the guard they name.
    // Sound because ShouldCompleteRender does not require _hasMinimapState --
    // layout-ready alone completes the reveal (asserted by each caller).
    private static void DriveViewToLoadedAndPaintedWithoutMinimapState(
        ApplicateWebMarkdownDocumentView view)
    {
        if (!view.HasLoadedDocumentForReveal)
        {
            view.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "document-ready" }));
        }
        if (!view.HasLoadedDocumentForReveal)
        {
            view.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "document-ready" }));
        }
        view.HandleWebMessageBody(JsonSerializer.Serialize(new { type = "layout-ready", cached = false }));
    }

    // M1 -- the end-to-end guard, and the harm this member exists to prevent.
    // RED without the retain-at-ingress write in HandleMinimapStateMessage, and
    // RED without the pull itself.
    //
    // It also pins the PLACEMENT claim, not only the existence of a retention:
    // the minimap-state below is posted while NOTHING is subscribed to
    // MinimapStateChanged (the handler is attached afterwards), so live delivery
    // is dropped exactly as it is when edit-preview owns the host. The payload
    // survives only because it is retained BEFORE the raise. Moving the retention
    // after the raise still passes here -- what fails is deleting it, or gating
    // it on a consumer being present.
    [Fact]
    public void NoOpReloadReemitsTheRetainedMinimapReservationForTheSameSource()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            // The renderer reports a VISIBLE minimap reserving 168 px -- the
            // width the probe measured on a real build.
            PostMinimapState(host.View, visible: true, reservedWidth: 168);
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            ApplicateWebMinimapStateEventArgs? reemitted = null;
            var fireCount = 0;
            host.View.MinimapStateChanged += (_, state) =>
            {
                reemitted = state;
                fireCount++;
            };

            // The reopen: same source, same content -> UpdateInputs resolves
            // None (or ApplyLivePreferences), never Render, so the renderer is
            // sent nothing and replays nothing. consumerHasMinimapDebt: true is
            // the consumer's own reading after SyncFromViewModel zeroed
            // _webMinimapReservedWidth on the document-identity transition.
            host.RequestRender(DocA, Request());
            var pulled = host.View.TryRaiseRetainedMinimapStateForConsumerDebt(
                DocA, consumerHasMinimapDebt: true);

            Assert.True(
                pulled,
                "The minimap reservation is LOST while the minimap is still drawn: reopening the same "
                + "document resolves to a no-op reload, the renderer's dedupe ledger suppresses any "
                + "minimap-state replay, and nothing refills the consumer's reservation -- so the document "
                + "column is laid out as if the minimap strip were free and the text runs underneath it.");
            Assert.True(
                reemitted is { Visible: true } && Math.Abs(reemitted.ReservedWidth - 168) < 0.001,
                "The reservation restored must be the 168 px the renderer last reported for this document; "
                + $"a wrong width mis-lays the column just as a missing one does. Got visible="
                + $"{reemitted?.Visible.ToString() ?? "(no event)"} reservedWidth="
                + $"{reemitted?.ReservedWidth.ToString("F2", CultureInfo.InvariantCulture) ?? "(no event)"}.");
            Assert.True(
                fireCount == 1,
                $"The pull must re-emit exactly once per settled debt; fired {fireCount} times.");
        });
    }

    // M2 -- the writer-enumeration obligation's W3 case, and the reason this
    // member needs no consumer-side discriminator field. A minimap the user (or
    // policy) HID is a DELIBERATE zero, not debt. Because the same message that
    // zeroes the consumer also writes the host's retained snapshot, the retained
    // payload itself discriminates: it reads Visible=false, and the pull must
    // refuse. RED if the retained-reservation guard is dropped.
    [Fact]
    public void M2PullIsSuppressedWhenTheRetainedStateSaysTheMinimapIsHidden()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            // Minimap shown, then deliberately hidden -- the second message is
            // the one that drove the consumer's reservation to 0.
            PostMinimapState(host.View, visible: true, reservedWidth: 168);
            PostMinimapState(host.View, visible: false, reservedWidth: 0);
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            var fireCount = 0;
            host.View.MinimapStateChanged += (_, _) => fireCount++;

            host.RequestRender(DocA, Request());
            var pulled = host.View.TryRaiseRetainedMinimapStateForConsumerDebt(
                DocA, consumerHasMinimapDebt: true);

            Assert.True(
                !pulled && fireCount == 0,
                "A deliberately HIDDEN minimap must never be resurrected as a reservation: the consumer "
                + "reads 0 because the renderer said the strip is gone, not because it lost state. "
                + "Reinstating 168 px here would inset the document away from a strip nothing occupies, "
                + $"undoing another owner's decision. pulled={pulled} fireCount={fireCount}.");
        });
    }

    // M3 -- a consumer that already holds a reservation has no debt, so a repeat
    // sync must not pull at all. The consumer, not the host, owns this reading.
    [Fact]
    public void M3RepeatedSyncsWithAPopulatedReservationDoNotPull()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostMinimapState(host.View, visible: true, reservedWidth: 168);
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            var fireCount = 0;
            host.View.MinimapStateChanged += (_, _) => fireCount++;

            for (var i = 0; i < 3; i++)
            {
                host.RequestRender(DocA, Request());
                host.View.TryRaiseRetainedMinimapStateForConsumerDebt(
                    DocA, consumerHasMinimapDebt: false);
            }

            Assert.True(
                fireCount == 0,
                $"A consumer holding a live reservation has no debt to settle; the pull fired {fireCount} "
                + "times anyway, churning the column width on every ordinary sync.");
        });
    }

    // M4 -- inert on a host that has never painted anything: a pull on a
    // brand-new host must never raise. OUTCOME pin, not an isolation of one
    // guard: nothing is retained AND nothing is loaded, so the retained-payload
    // check rejects first and the loaded check would reject too. Both facts are
    // true of a fresh host and neither is worth engineering apart.
    [Fact]
    public void M4PullIsSuppressedWhenTheHostHoldsNoPaintedDocument()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            var pulled = host.View.TryRaiseRetainedMinimapStateForConsumerDebt(
                DocA, consumerHasMinimapDebt: true);

            Assert.False(pulled);
        });
    }

    // M5 -- currency. Unlike the heading path there is no INV-ORDER analogue
    // here (ShouldCompleteRender deliberately does not require _hasMinimapState),
    // so the capture-generation check is this member's load-bearing currency
    // guard, not a backstop. Two page generations for the SAME Source value
    // (DocB -> DocA -> DocB), with no fresh minimap-state for the second: the
    // first generation's reservation must not leak forward. RED without the
    // guard.
    [Fact]
    public void M5PullSkipsAReservationCapturedUnderASupersededGeneration()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocB, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostMinimapState(host.View, visible: true, reservedWidth: 168);
            Assert.True(host.View.HasLoadedDocumentForSource(DocB));

            // Neither of the next two generations posts a minimap-state, so the
            // retained payload stays DocB-generation-1's VISIBLE 168 px -- the
            // capture-generation check is the only thing left that can reject it
            // (the retained-reservation guard cannot, deliberately).
            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPaintedWithoutMinimapState(host.View);

            // DocB reopened: a real render, a NEW page generation for the SAME
            // MarkdownSource value.
            host.RequestRender(DocB, Request());
            DriveViewToLoadedAndPaintedWithoutMinimapState(host.View);
            Assert.True(host.View.HasLoadedDocumentForSource(DocB));

            var fireCount = 0;
            host.View.MinimapStateChanged += (_, _) => fireCount++;

            host.RequestRender(DocB, Request());
            var pulled = host.View.TryRaiseRetainedMinimapStateForConsumerDebt(
                DocB, consumerHasMinimapDebt: true);

            Assert.True(
                !pulled && fireCount == 0,
                "A reservation captured under a superseded page generation must not be handed to a later "
                + "one: Source equality alone cannot tell two generations of the same document apart, and "
                + "a stale width lays the column out against a minimap that generation never reported. "
                + $"pulled={pulled} fireCount={fireCount}.");
        });
    }

    // M6 -- the retention is a SINGLE writer: a later minimap-state for the same
    // source replaces the retained payload rather than accumulating beside it,
    // and the pull hands out the LATEST one.
    [Fact]
    public void M6PullReemitsTheLatestRetainedReservationNotAStaleEarlierOne()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostMinimapState(host.View, visible: true, reservedWidth: 168);
            PostMinimapState(host.View, visible: true, reservedWidth: 210);
            Assert.True(host.View.HasLoadedDocumentForSource(DocA));

            ApplicateWebMinimapStateEventArgs? reemitted = null;
            host.View.MinimapStateChanged += (_, state) => reemitted = state;

            host.RequestRender(DocA, Request());
            var pulled = host.View.TryRaiseRetainedMinimapStateForConsumerDebt(
                DocA, consumerHasMinimapDebt: true);

            Assert.True(
                pulled,
                "The minimap reservation is LOST while the minimap is still drawn -- see "
                + "NoOpReloadReemitsTheRetainedMinimapReservationForTheSameSource for the harm; this test "
                + "additionally requires that the reservation restored is the LATEST one.");
            Assert.True(
                reemitted is not null && Math.Abs(reemitted.ReservedWidth - 210) < 0.001,
                "The pull must restore the reservation the renderer reported LAST, not an earlier one; "
                + $"got {reemitted?.ReservedWidth.ToString("F2", CultureInfo.InvariantCulture) ?? "(no event)"} instead of 210.");
        });
    }

    // M7 -- COMPOSITE pin, stated honestly: one document's minimap geometry must
    // never reach another document's layout. It does NOT isolate the source
    // guard. A Source change forces a QueueRender, so the capture generation
    // moves with it and BOTH the source check and the generation check reject
    // here -- exactly as they do on the heading path, whose source check has the
    // same property. The source check is kept as a defensive branch (it asserts
    // payload ownership independently of the generation's lifecycle); this test
    // pins the OUTCOME, not that branch alone, and is labelled so no later
    // reader mistakes it for coverage of the guard in isolation.
    [Fact]
    public void M7PullIsSuppressedForADifferentSourceThanTheRetainedOne()
    {
        RunOnHost(host =>
        {
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            host.SetWarmupParent(warmup);
            host.AttachTo(slot, ViewerIntent());

            host.RequestRender(DocA, Request());
            DriveViewToLoadedAndPainted(host.View);
            PostMinimapState(host.View, visible: true, reservedWidth: 168);

            // DocB becomes the loaded document, and deliberately gets no
            // minimap-state of its own -- so only DocA's reservation is retained
            // (the shared drive helper's visible=false post would have replaced
            // it and made this test pass for the wrong reason).
            host.RequestRender(DocB, Request());
            DriveViewToLoadedAndPaintedWithoutMinimapState(host.View);
            Assert.True(host.View.HasLoadedDocumentForSource(DocB));

            var fireCount = 0;
            host.View.MinimapStateChanged += (_, _) => fireCount++;

            host.RequestRender(DocB, Request());
            var pulled = host.View.TryRaiseRetainedMinimapStateForConsumerDebt(
                DocB, consumerHasMinimapDebt: true);

            Assert.True(
                !pulled && fireCount == 0,
                "One document's minimap reservation must never be applied to another document's layout. "
                + $"pulled={pulled} fireCount={fireCount}.");
        });
    }

    // ----- Parking test doubles -------------------------------------------------
    // Both return never-completing tasks so the host's fire-and-forget QueueRender
    // parks at its first await, before any _webView.Navigate. The synchronous
    // test bodies never pump a continuation, so nothing async runs mid-assertion.

    private sealed class ParkingRenderer : IApplicateHtmlMarkdownRenderer
    {
        public MarkdownSource? LastRenderAsyncSource { get; private set; }

        public MarkdownSource? LastRenderBodyAsyncSource { get; private set; }

        public Task<ApplicateHtmlDocument> RenderAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
        {
            LastRenderAsyncSource = source;
            return new TaskCompletionSource<ApplicateHtmlDocument>().Task;
        }

        public Task<ApplicateRenderedBody> RenderBodyAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
        {
            LastRenderBodyAsyncSource = source;
            return new TaskCompletionSource<ApplicateRenderedBody>().Task;
        }

        public Task<string> RenderTableCellHtmlAsync(
            string rawCellMarkdown,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => new TaskCompletionSource<string>().Task;
    }

    private sealed class ParkingShellAssetFactory : IApplicateShellAssetBundleFactory
    {
        public Task<ApplicateShellAssetBundle> GetAsync(CancellationToken cancellationToken)
            => new TaskCompletionSource<ApplicateShellAssetBundle>().Task;
    }
}

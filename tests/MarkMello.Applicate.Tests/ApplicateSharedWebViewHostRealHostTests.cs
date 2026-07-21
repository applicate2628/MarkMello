using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
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

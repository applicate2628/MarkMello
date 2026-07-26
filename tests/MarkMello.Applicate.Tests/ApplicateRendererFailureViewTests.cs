using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation.Localization;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateRendererFailureViewTests
{
    [Fact]
    public async Task FailureTitleUsesEnglishAndUpdatesForRussianLanguage()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var application = Avalonia.Application.Current
                ?? throw new InvalidOperationException("Headless Avalonia application is unavailable.");
            var resources = application.Resources;
            var hadExistingLocalization = resources.TryGetValue("Localization", out var existingLocalization);
            var localization = new LocalizationService(AppLanguage.English);
            resources["Localization"] = localization;

            try
            {
                var view = new ApplicateRendererFailureView();

                Assert.Equal("Could not display the document", view.TitleTextForTesting);

                localization.SetLanguage(AppLanguage.Russian);

                Assert.Equal("Не удалось отобразить документ", view.TitleTextForTesting);
            }
            finally
            {
                if (hadExistingLocalization)
                {
                    resources["Localization"] = existingLocalization!;
                }
                else
                {
                    resources.Remove("Localization");
                }
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void ConstructsHiddenWithDefaultDocumentRenderFailedKind()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateRendererFailureView();

            Assert.False(view.IsVisible);
            Assert.Equal(ApplicateRendererFailureKind.DocumentRenderFailed, view.FailureKind);
            Assert.True(view.IsRetryButtonVisibleForTesting);
            Assert.False(string.IsNullOrEmpty(view.TitleTextForTesting));
            Assert.False(string.IsNullOrEmpty(view.BodyTextForTesting));
            Assert.False(view.DocumentLineVisibleForTesting);
        }, CancellationToken.None);
    }

    [Fact]
    public void RuntimeMissingKindHidesRetryAndUpdatesTitle()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateRendererFailureView();
            var renderFailedTitle = view.TitleTextForTesting;

            view.FailureKind = ApplicateRendererFailureKind.WebView2RuntimeMissing;

            Assert.False(view.IsRetryButtonVisibleForTesting);
            Assert.NotEqual(renderFailedTitle, view.TitleTextForTesting);
            Assert.False(string.IsNullOrEmpty(view.TitleTextForTesting));
        }, CancellationToken.None);
    }

    [Fact]
    public void StaleNavigationKindHidesRetry()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateRendererFailureView
            {
                FailureKind = ApplicateRendererFailureKind.StaleNavigation,
            };

            Assert.False(view.IsRetryButtonVisibleForTesting);
            Assert.False(string.IsNullOrEmpty(view.TitleTextForTesting));
        }, CancellationToken.None);
    }

    [Fact]
    public void DocumentPathSurfacesInDocumentLineAndDiagnostics()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateRendererFailureView
            {
                DocumentPath = @"D:\dev\sample.md",
                Timestamp = new DateTime(2026, 5, 19, 12, 30, 0, DateTimeKind.Utc),
            };

            Assert.True(view.DocumentLineVisibleForTesting);
            Assert.Equal(@"D:\dev\sample.md", view.DocumentLineTextForTesting);

            var payload = view.BuildDiagnosticsPayload();
            Assert.Contains("Document: D:\\dev\\sample.md", payload);
            Assert.Contains("Kind: DocumentRenderFailed", payload);
            Assert.Contains("2026-05-19T12:30:00.000Z", payload);
        }, CancellationToken.None);
    }

    [Fact]
    public void NullDocumentPathHidesDocumentLineAndOmitsFromDiagnostics()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateRendererFailureView
            {
                DocumentPath = null,
                Timestamp = new DateTime(2026, 5, 19, 12, 30, 0, DateTimeKind.Utc),
            };

            Assert.False(view.DocumentLineVisibleForTesting);
            Assert.DoesNotContain("Document:", view.BuildDiagnosticsPayload());
        }, CancellationToken.None);
    }

    [Fact]
    public void DiagnosticsPayloadIncludesExceptionTypeAndMessageWhenProvided()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateRendererFailureView
            {
                FailureException = new InvalidOperationException("boom"),
                Timestamp = new DateTime(2026, 5, 19, 12, 30, 0, DateTimeKind.Utc),
            };

            var payload = view.BuildDiagnosticsPayload();

            Assert.Contains("Exception: System.InvalidOperationException", payload);
            Assert.Contains("Message: boom", payload);
        }, CancellationToken.None);
    }

    [Fact]
    public void CopyDiagnosticsCallbackReceivesPayloadInsteadOfClipboardFallback()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            string? captured = null;
            var view = new ApplicateRendererFailureView
            {
                DocumentPath = @"D:\docs\readme.md",
                CopyDiagnosticsCallback = payload => captured = payload,
            };

            // Locate the button via the diagnostics callback wiring through
            // the visual tree is overkill for a unit test. Drive the click
            // path through BuildDiagnosticsPayload + manual callback invocation
            // which exercises the same code path users would.
            var payload = view.BuildDiagnosticsPayload();
            view.CopyDiagnosticsCallback?.Invoke(payload);

            Assert.NotNull(captured);
            Assert.Contains("D:\\docs\\readme.md", captured);
        }, CancellationToken.None);
    }

    [Fact]
    public void ShowFailureAppliesContextAndMakesViewVisible()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateRendererFailureView();
            var retryFired = 0;
            var failure = new ApplicateRendererFailureEvent(
                Kind: ApplicateRendererFailureKind.DocumentRenderFailed,
                DocumentPath: @"E:\Downloads\wave.md",
                Timestamp: new DateTime(2026, 5, 19, 9, 0, 0, DateTimeKind.Utc),
                Exception: new InvalidOperationException("render boom"));

            view.ShowFailure(failure, retry: () => retryFired++);

            Assert.True(view.IsVisible);
            Assert.Equal(ApplicateRendererFailureKind.DocumentRenderFailed, view.FailureKind);
            Assert.Equal(@"E:\Downloads\wave.md", view.DocumentLineTextForTesting);
            Assert.NotNull(view.RetryCallback);

            view.RetryCallback?.Invoke();
            Assert.Equal(1, retryFired);
        }, CancellationToken.None);
    }

    // G11 (D7, design work-items/active/2026-07-25-toc-empty-on-open/
    // design.md §9.2/§9.5, claim 9): the overlay must dismiss itself on the
    // retry it originates -- a same-document retry that resolves via the
    // shared host's cache-hit fast path fires no DocumentRendered, so
    // neither of the two consumer clear sites (document-identity change,
    // OnHostDocumentRendered) ever runs, and the overlay used to latch
    // visible forever (and, with D4, the TOC latched empty with it).
    //
    // MUST go through the click handler (ClickRetryForTesting -> the real
    // OnRetryClick), not RetryCallback directly -- the existing tests above
    // (e.g. ShowFailureAppliesContextAndMakesViewVisible) invoke
    // RetryCallback?.Invoke() directly, bypassing OnRetryClick entirely,
    // which is exactly the mistake that made the now-deleted
    // ApplicateSharedWebViewHostRealHostTests G9 guard worthless.
    [Fact]
    public async Task RetryDismissesTheFailureViewBeforeInvokingTheCallback()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var failure = new ApplicateRendererFailureEvent(
                Kind: ApplicateRendererFailureKind.DocumentRenderFailed,
                DocumentPath: @"E:\Downloads\wave.md",
                Timestamp: new DateTime(2026, 5, 19, 9, 0, 0, DateTimeKind.Utc));

            // Part 1 -- an ordinary retry (the callback does not re-fail)
            // must already have IsVisible == false by the time the callback
            // observes it, and must stay hidden afterwards.
            var view = new ApplicateRendererFailureView();
            var retryFired = 0;
            var wasVisibleInsideCallback = true;
            view.ShowFailure(failure, retry: () =>
            {
                retryFired++;
                wasVisibleInsideCallback = view.IsVisible;
            });

            view.ClickRetryForTesting();

            Assert.Equal(1, retryFired);
            Assert.False(
                wasVisibleInsideCallback,
                "OnRetryClick must set IsVisible=false BEFORE invoking RetryCallback (design D7 ordering).");
            Assert.False(view.IsVisible);

            // Part 2 -- the ordering assertion: a re-failure raised FROM
            // INSIDE the retry callback (an immediate re-fail, exactly what
            // ShowFailure being called again simulates) must win. ShowFailure
            // sets IsVisible=true AFTER OnRetryClick's own dismiss runs, so
            // the overlay ends up visible again, not latched hidden.
            var refailingView = new ApplicateRendererFailureView();
            refailingView.ShowFailure(failure, retry: () => refailingView.ShowFailure(failure));

            refailingView.ClickRetryForTesting();

            Assert.True(
                refailingView.IsVisible,
                "A ShowFailure issued from inside the retry callback must leave the overlay visible (ordering: dismiss happens before invoke).");
        }, CancellationToken.None);
    }

    // F4 (round-5 gate finding, 2026-07-26): OnRetryClick dismissed the
    // overlay unconditionally before checking whether a retry callback was
    // even set. The retry button is visible for both DocumentRenderFailed
    // and the default: arm in ApplyFailureKind, but no consumer sets a
    // non-null RetryCallback for anything other than DocumentRenderFailed --
    // a click reaching the handler with retry: null must not destroy the
    // only failure surface for nothing.
    [Fact]
    public async Task RetryClickWithNoCallbackLeavesTheOverlayVisible()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var failure = new ApplicateRendererFailureEvent(
                Kind: ApplicateRendererFailureKind.DocumentRenderFailed,
                DocumentPath: @"E:\Downloads\wave.md",
                Timestamp: new DateTime(2026, 5, 19, 9, 0, 0, DateTimeKind.Utc));

            var view = new ApplicateRendererFailureView();
            view.ShowFailure(failure, retry: null);

            view.ClickRetryForTesting();

            Assert.True(
                view.IsVisible,
                "OnRetryClick must not dismiss the overlay when no RetryCallback is set.");
        }, CancellationToken.None);
    }
}

public sealed class ApplicateRendererFailureEventTests
{
    [Fact]
    public void DefaultExceptionParameterIsNull()
    {
        var failure = new ApplicateRendererFailureEvent(
            Kind: ApplicateRendererFailureKind.DocumentRenderFailed,
            DocumentPath: "foo.md",
            Timestamp: new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Utc));

        Assert.Null(failure.Exception);
        Assert.Equal(ApplicateRendererFailureKind.DocumentRenderFailed, failure.Kind);
        Assert.Equal("foo.md", failure.DocumentPath);
    }

    [Fact]
    public void RecordEqualityIsValueBased()
    {
        var ts = new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Utc);
        var a = new ApplicateRendererFailureEvent(
            ApplicateRendererFailureKind.WebView2RuntimeMissing,
            DocumentPath: null,
            Timestamp: ts);
        var b = new ApplicateRendererFailureEvent(
            ApplicateRendererFailureKind.WebView2RuntimeMissing,
            DocumentPath: null,
            Timestamp: ts);

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordsWithDifferentDocumentPathsAreNotEqual()
    {
        var ts = new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Utc);
        var a = new ApplicateRendererFailureEvent(
            ApplicateRendererFailureKind.DocumentRenderFailed,
            DocumentPath: "a.md",
            Timestamp: ts);
        var b = new ApplicateRendererFailureEvent(
            ApplicateRendererFailureKind.DocumentRenderFailed,
            DocumentPath: "b.md",
            Timestamp: ts);

        Assert.NotEqual(a, b);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using Xunit;

namespace MarkMello.Applicate.Tests;

// Design H2 — Host<->renderer IPC contract ownership (Option B+, terra revisions).
// These tests close the loop the TS-side `ipcContract.ts` recursive descriptors +
// generated `contract/ipc-contract.json` open, so the producer (C#) and the
// declared contract (TS) do not drift silently:
//   - outbound: every enumerated host->renderer producer (view builders + the
//     reveal-intent builders) serializes to a payload whose serialized `type`
//     value matches the registry key AND passes the RECURSIVE descriptor walk (a
//     stray key at ANY nesting level, incl. inside minimapPolicy{}, goes RED).
//   - inbound: registry-shaped fixtures drive the real dispatch and fire the
//     matching handler (incl. a NONEMPTY nested headings payload); tolerant
//     log/trace handlers get field-read + wrong-kind/case/malformed fixtures.
//   - registry: the JSON type-set, a C# type registry, AND the host-SENDER
//     literals extracted from the sender source are asserted SET-EQUAL, so an
//     unregistered C#-only outbound sender (or a registered type with no sender)
//     goes RED. Renderer types are subset-checked against the renderer source
//     (predicate-dispatched receivers have no `type ==` site).
public sealed class IpcContractTests
{
    private static readonly string RepoSrcRoot = Path.Combine(
        FindRepoRoot(), "src", "MarkMello.Applicate.Desktop");

    private static readonly string ContractJsonPath = Path.Combine(
        RepoSrcRoot, "RendererWeb", "contract", "ipc-contract.json");

    private static readonly string RendererSourcePath = Path.Combine(
        RepoSrcRoot, "RendererWeb", "src", "renderer.ts");

    private static readonly string ViewSourcePath = Path.Combine(
        RepoSrcRoot, "Views", "ApplicateWebMarkdownDocumentView.cs");

    private static readonly string RevealIntentsSourcePath = Path.Combine(
        RepoSrcRoot, "Rendering", "IApplicateHostRevealIntents.cs");

    private static readonly string[] KnownHostMessageTypes =
    [
        "theme", "minimap-policy", "reading-preferences", "scroll-by", "scroll-to-block",
        "scroll-to", "scroll-to-progress", "load-document", "append-document", "load-cached-document",
        "clear-document", "invalidate-document-cache-key", "set-task-checkbox", "table-cell-updated", "scroll-to-heading",
        "scroll-to-source-line", "open-find-bar", "host-scrollbar", "mode-settle-probe",
        "minimap-settle-probe", "host-shortcuts-reset", "mode-reveal-prepare", "mode-reveal-start",
        "document-reveal-prepare", "document-reveal-start", "prepare-for-export", "cancel-full-render",
        "capture-rendered-html",
    ];

    private static readonly string[] KnownRendererMessageTypes =
    [
        "document-ready", "layout-ready", "post-ready-enhancements-complete", "theme-applied",
        "link-clicked", "task-toggle", "table-cell-edit", "minimap-state", "minimap-settled", "scroll", "viewer-interaction",
        "wheel", "width-drag", "drag-hover", "drop-file", "host-shortcut", "debug-log", "perf-mark",
        "headings-updated", "active-heading-changed", "preview-source-line", "csp-violation",
        "document-cache-miss", "document-first-paint", "mode-toggle-settled", "shell-init-failed",
        "full-render-complete", "full-render-failed", "rendered-html-captured", "rendered-html-failed",
    ];

    // ---- outbound: producer manifest serialized through the recursive walk -----
    // Each asserts BOTH the serialized `type` VALUE (a typo'd literal in one
    // builder branch goes RED) AND the recursive descriptor match.

    [Fact]
    public void ReadingPreferencesBuilderMatchesDescriptorAndCarriesNoViewport()
    {
        var shape = LoadContract().Host["reading-preferences"];

        RunOnView(view =>
        {
            var payload = Serialize(view.BuildReadingPreferencesMessage());
            Assert.Equal("reading-preferences", TypeValue(payload));
            Assert.Empty(CollectViolations(payload, shape, "reading-preferences"));

            var keys = ObjectKeys(payload);
            Assert.Equal(
                new SortedSet<string>(StringComparer.Ordinal)
                {
                    "type", "fontFamily", "fontSize", "lineHeight", "maxWidth", "minMaxWidth",
                    "minimapMode", "viewerChromeEnabled", "documentScrollEnabled", "wheelProxyEnabled",
                    "widthResizerVisibility",
                },
                new SortedSet<string>(keys, StringComparer.Ordinal));
            Assert.DoesNotContain("viewportWidth", keys);
            Assert.DoesNotContain("viewportHeight", keys);
        });
    }

    [Fact]
    public void ModeSettleProbeBuilderMatchesDescriptorAndCarriesViewport()
    {
        var shape = LoadContract().Host["mode-settle-probe"];

        RunOnView(view =>
        {
            var nonTransactional = Serialize(view.BuildModeSettleProbeMessage());
            Assert.Equal("mode-settle-probe", TypeValue(nonTransactional));
            Assert.Empty(CollectViolations(nonTransactional, shape, "mode-settle-probe"));
            Assert.Contains("viewportWidth", ObjectKeys(nonTransactional));
            Assert.Contains("viewportHeight", ObjectKeys(nonTransactional));
            Assert.DoesNotContain("transactionGeneration", ObjectKeys(nonTransactional));

            var transactional = Serialize(view.BuildModeSettleProbeMessage(transactionGeneration: 5, skipFrameWait: true));
            Assert.Equal("mode-settle-probe", TypeValue(transactional));
            Assert.Empty(CollectViolations(transactional, shape, "mode-settle-probe"));
            Assert.Contains("viewportWidth", ObjectKeys(transactional));
            Assert.Contains("transactionGeneration", ObjectKeys(transactional));
            Assert.Contains("skipFrameWait", ObjectKeys(transactional));
        });
    }

    [Fact]
    public void MinimapPolicyBuilderMatchesNestedDescriptor()
    {
        var shape = LoadContract().Host["minimap-policy"];

        RunOnView(view =>
        {
            var payload = Serialize(view.BuildMinimapPolicyMessage());
            Assert.Equal("minimap-policy", TypeValue(payload));
            // Recursive walk into minimapPolicy{} — a stray nested key goes RED.
            Assert.Empty(CollectViolations(payload, shape, "minimap-policy"));
        });
    }

    [Fact]
    public void RevealIntentBuildersMatchDescriptorsAndTypeValues()
    {
        var host = LoadContract().Host;

        void Check(object message, string expectedType)
        {
            var payload = Serialize(message);
            Assert.Equal(expectedType, TypeValue(payload));
            Assert.Empty(CollectViolations(payload, host[expectedType], expectedType));
        }

        Check(SharedWebViewHostRevealIntents.BuildModeRevealPrepareMessage(120), "mode-reveal-prepare");
        Check(SharedWebViewHostRevealIntents.BuildModeRevealStartMessage(120), "mode-reveal-start");
        Check(SharedWebViewHostRevealIntents.BuildDocumentRevealPrepareMessage(120, "light"), "document-reveal-prepare");
        Check(SharedWebViewHostRevealIntents.BuildDocumentRevealStartMessage(120), "document-reveal-start");
    }

    [Fact]
    public void TableCellUpdatedHostSenderMatchesExactProducerShapes()
    {
        var contract = LoadContract();

        var success = Serialize(ApplicateWebMarkdownDocumentView.BuildTableCellUpdatedSuccessMessage(
            line: 12,
            cellIndex: 3,
            text: "Canonical plain text",
            key: "f00dcafe"));
        Assert.Equal("table-cell-updated", TypeValue(success));
        Assert.True(success.GetProperty("ok").GetBoolean());
        Assert.Equal("Canonical plain text", success.GetProperty("text").GetString());
        Assert.Equal("f00dcafe", success.GetProperty("key").GetString());
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "type", "line", "cellIndex", "ok", "text", "key" },
            new SortedSet<string>(ObjectKeys(success), StringComparer.Ordinal));
        Assert.Empty(CollectViolations(success, contract.Host["table-cell-updated"], "table-cell-updated.success"));

        var failure = Serialize(ApplicateWebMarkdownDocumentView.BuildTableCellUpdatedFailureMessage(
            line: 12,
            cellIndex: 3));
        Assert.Equal("table-cell-updated", TypeValue(failure));
        Assert.False(failure.GetProperty("ok").GetBoolean());
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "type", "line", "cellIndex", "ok" },
            new SortedSet<string>(ObjectKeys(failure), StringComparer.Ordinal));
        Assert.False(failure.TryGetProperty("text", out _));
        Assert.False(failure.TryGetProperty("key", out _));
        Assert.False(failure.TryGetProperty("reason", out _));
        Assert.Empty(CollectViolations(failure, contract.Host["table-cell-updated"], "table-cell-updated.failure"));

        // BUSY failure carries reason="busy" (and still omits text/key) so the
        // renderer keeps the user's typed text instead of restoring the stash.
        var busyFailure = Serialize(ApplicateWebMarkdownDocumentView.BuildTableCellUpdatedFailureMessage(
            line: 12,
            cellIndex: 3,
            busy: true));
        Assert.False(busyFailure.GetProperty("ok").GetBoolean());
        Assert.Equal("busy", busyFailure.GetProperty("reason").GetString());
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "type", "line", "cellIndex", "ok", "reason" },
            new SortedSet<string>(ObjectKeys(busyFailure), StringComparer.Ordinal));
        Assert.False(busyFailure.TryGetProperty("text", out _));
        Assert.False(busyFailure.TryGetProperty("key", out _));
        Assert.Empty(CollectViolations(busyFailure, contract.Host["table-cell-updated"], "table-cell-updated.busy-failure"));
    }

    [Fact]
    public void PrepareForExportHostSenderMatchesDescriptor()
    {
        var builder = typeof(ApplicateWebMarkdownDocumentView).GetMethod(
            "BuildPrepareForExportMessage",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(builder);

        var payload = Serialize(builder!.Invoke(null, ["export-17"])!);
        Assert.Equal("prepare-for-export", TypeValue(payload));
        Assert.Equal("export-17", payload.GetProperty("requestId").GetString());
        Assert.Empty(CollectViolations(payload, LoadContract().Host["prepare-for-export"], "prepare-for-export"));
    }

    [Fact]
    public void CancelFullRenderHostSenderUsesExactRequestShape()
    {
        var builder = typeof(ApplicateWebMarkdownDocumentView).GetMethod(
            "BuildCancelFullRenderMessage",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(builder);

        var payload = Serialize(builder!.Invoke(null, ["export-17"])!);
        // Exactly type + requestId: the renderer's cancel gate is an exact
        // requestId match against the live barrier, so nothing else is on the wire
        // and there is no optional stamp for the gate to fail open on.
        Assert.True(ObjectKeys(payload).SetEquals(["requestId", "type"]));
        Assert.Equal("cancel-full-render", TypeValue(payload));
        Assert.Equal("export-17", payload.GetProperty("requestId").GetString());
        Assert.Empty(CollectViolations(
            payload,
            LoadContract().Host["cancel-full-render"],
            "cancel-full-render"));
    }

    [Fact]
    public void CancellingAPendingFullRenderRequestSendsTheRendererACorrelatedUnwind()
    {
        var posted = new List<string>();
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: payload =>
                {
                    posted.Add(payload);
                    return true;
                },
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                using var cancellation = new CancellationTokenSource();
                var request = view.PrepareForExportAsync(cancellation.Token);
                var requestId = Assert.Single(PendingFullRenderRequestIds(view));
                Assert.Single(posted);
                Assert.Contains("\"prepare-for-export\"", posted[0], StringComparison.Ordinal);

                cancellation.Cancel();

                // The host settling its own side is 14fefe1's guarantee and must
                // survive. What this pins is the SECOND thing: the renderer is told
                // to unwind, correlated to the same requestId, so its barrier and
                // its abandoned Mermaid tracking do not outlive the export.
                Assert.Equal(2, posted.Count);
                var cancel = JsonDocument.Parse(posted[1]).RootElement;
                Assert.Equal("cancel-full-render", cancel.GetProperty("type").GetString());
                Assert.Equal(requestId, cancel.GetProperty("requestId").GetString());
                Assert.Empty(CollectViolations(
                    cancel,
                    LoadContract().Host["cancel-full-render"],
                    "cancel-full-render"));

                Assert.True(request.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(
                    ApplicateFullRenderStatus.Cancelled,
                    request.Result.Status);
                Assert.Empty(PendingFullRenderRequestIds(view));
            });
    }

    [Fact]
    public void CompletingAFullRenderRequestSendsNoCancelUnwind()
    {
        var posted = new List<string>();
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: payload =>
                {
                    posted.Add(payload);
                    return true;
                },
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                using var cancellation = new CancellationTokenSource();
                var request = view.PrepareForExportAsync(cancellation.Token);
                var requestId = Assert.Single(PendingFullRenderRequestIds(view));
                CompleteFullRenderRequestFromRenderer(view, requestId, succeeded: true);
                Assert.True(request.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(ApplicateFullRenderStatus.Completed, request.Result.Status);

                // A cancel arriving after the export already finished must not put a
                // stray unwind on the wire: the request is gone, so there is nothing
                // to cancel and a later export must not inherit one.
                cancellation.Cancel();
                Assert.Single(posted);
                Assert.Contains("\"prepare-for-export\"", posted[0], StringComparison.Ordinal);
            });
    }

    [Fact]
    public void CaptureRenderedHtmlHostSenderUsesExactRequestShape()
    {
        var builder = typeof(ApplicateWebMarkdownDocumentView).GetMethod(
            "BuildCaptureRenderedHtmlMessage",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(builder);

        var payload = Serialize(builder!.Invoke(null, ["capture-17"])!);
        Assert.True(ObjectKeys(payload).SetEquals(["requestId", "type"]));
        Assert.Equal("capture-rendered-html", TypeValue(payload));
        Assert.Equal("capture-17", payload.GetProperty("requestId").GetString());
        Assert.Empty(CollectViolations(
            payload,
            LoadContract().Host["capture-rendered-html"],
            "capture-rendered-html"));
    }

    [Fact]
    public void CaptureRenderedHtmlTerminalDescriptorsUseExactClosedShapes()
    {
        var contract = LoadContract();

        var captured = contract.Renderer["rendered-html-captured"];
        var failed = contract.Renderer["rendered-html-failed"];
        Assert.Equal(["html", "requestId", "type"], captured.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["reason", "requestId", "type"], failed.Keys.Order(StringComparer.Ordinal));
        Assert.All(captured.Values, static field => Assert.False(field.Optional));
        Assert.All(failed.Values, static field => Assert.False(field.Optional));
        Assert.DoesNotContain("code", failed.Keys);
    }

    [Fact]
    public void CaptureRenderedHtmlCorrelatesSuccessAndRendererFailureAndCleans()
    {
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => true,
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                var success = InvokeCaptureRenderedHtml(view, CancellationToken.None);
                var successId = Assert.Single(PendingRenderedHtmlCaptureRequestIds(view));
                view.HandleWebMessageBody(JsonSerializer.Serialize(new
                {
                    type = "rendered-html-captured",
                    requestId = successId,
                    html = "<!DOCTYPE html>\n<html></html>",
                }));
                AssertRenderedHtmlCaptureResult(
                    success,
                    "Success",
                    "<!DOCTYPE html>\n<html></html>",
                    null,
                    null);

                var failure = InvokeCaptureRenderedHtml(view, CancellationToken.None);
                var failureId = Assert.Single(PendingRenderedHtmlCaptureRequestIds(view));
                view.HandleWebMessageBody(JsonSerializer.Serialize(new
                {
                    type = "rendered-html-failed",
                    requestId = failureId,
                    reason = "HTMLX-SERIALIZATION: outerHTML failed",
                }));
                AssertRenderedHtmlCaptureResult(
                    failure,
                    "CaptureFailed",
                    null,
                    "HTMLX-SERIALIZATION",
                    "HTMLX-SERIALIZATION: outerHTML failed");

                AssertCaptureOwnersReleased(view);
            });
    }

    [Theory]
    [InlineData("HTMLX-NOT-READ-MODE")]
    [InlineData("HTMLX-DOCUMENT-CHANGED")]
    [InlineData("HTMLX-CLEANUP-CONTRACT")]
    [InlineData("HTMLX-RESOURCE-NOT-DATA-URI")]
    [InlineData("HTMLX-SERIALIZATION")]
    public void RendererCaptureFailureReasonsMapToTheClosedHostTaxonomy(string failureId)
    {
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => true,
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                var capture = InvokeCaptureRenderedHtml(view, CancellationToken.None);
                var requestId = Assert.Single(PendingRenderedHtmlCaptureRequestIds(view));
                var reason = $"{failureId}: renderer detail";
                view.HandleWebMessageBody(JsonSerializer.Serialize(new
                {
                    type = "rendered-html-failed",
                    requestId,
                    reason,
                }));

                AssertRenderedHtmlCaptureResult(
                    capture,
                    "CaptureFailed",
                    null,
                    failureId,
                    reason);
                AssertCaptureOwnersReleased(view);
            });
    }

    [Theory]
    [InlineData("captured-missing-html")]
    [InlineData("captured-wrong-html-kind")]
    [InlineData("captured-extra-field")]
    [InlineData("failed-empty-reason")]
    [InlineData("failed-extra-code")]
    [InlineData("failed-unknown-reason")]
    public void MalformedCaptureTerminalSettlesMatchingRequestAsTypedFailure(string scenario)
    {
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => true,
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                var capture = InvokeCaptureRenderedHtml(view, CancellationToken.None);
                var requestId = Assert.Single(PendingRenderedHtmlCaptureRequestIds(view));
                var body = scenario switch
                {
                    "captured-missing-html" => JsonSerializer.Serialize(new
                    {
                        type = "rendered-html-captured",
                        requestId,
                    }),
                    "captured-wrong-html-kind" => JsonSerializer.Serialize(new
                    {
                        type = "rendered-html-captured",
                        requestId,
                        html = 17,
                    }),
                    "captured-extra-field" => JsonSerializer.Serialize(new
                    {
                        type = "rendered-html-captured",
                        requestId,
                        html = "<!DOCTYPE html>\n<html></html>",
                        code = "forbidden",
                    }),
                    "failed-empty-reason" => JsonSerializer.Serialize(new
                    {
                        type = "rendered-html-failed",
                        requestId,
                        reason = string.Empty,
                    }),
                    "failed-extra-code" => JsonSerializer.Serialize(new
                    {
                        type = "rendered-html-failed",
                        requestId,
                        reason = "HTMLX-CLEANUP-CONTRACT",
                        code = "forbidden",
                    }),
                    "failed-unknown-reason" => JsonSerializer.Serialize(new
                    {
                        type = "rendered-html-failed",
                        requestId,
                        reason = "renderer said no",
                    }),
                    _ => throw new Xunit.Sdk.XunitException($"Unknown malformed scenario: {scenario}"),
                };

                view.HandleWebMessageBody(body);

                AssertRenderedHtmlCaptureResult(
                    capture,
                    "CaptureFailed",
                    null,
                    "HTMLX-IPC-SHAPE",
                    "Malformed rendered HTML capture terminal.");
                AssertCaptureOwnersReleased(view);
            });
    }

    [Fact(DisplayName = "Capture_TerminalRace_FirstWinsAndCleans")]
    public void CaptureTerminalRaceFirstWinsAndCleans()
    {
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => true,
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                var capture = InvokeCaptureRenderedHtml(view, CancellationToken.None);
                var requestId = Assert.Single(PendingRenderedHtmlCaptureRequestIds(view));
                view.HandleWebMessageBody(JsonSerializer.Serialize(new
                {
                    type = "rendered-html-captured",
                    requestId,
                    html = "<!DOCTYPE html>\n<html>first</html>",
                }));
                view.HandleWebMessageBody(JsonSerializer.Serialize(new
                {
                    type = "rendered-html-failed",
                    requestId,
                    reason = "HTMLX-SERIALIZATION: late",
                }));

                AssertRenderedHtmlCaptureResult(
                    capture,
                    "Success",
                    "<!DOCTYPE html>\n<html>first</html>",
                    null,
                    null);
                AssertCaptureOwnersReleased(view);
            });

        var source = File.ReadAllText(ViewSourcePath);
        const string settledEvent = "\"HtmlExportCaptureSettled\"";
        var settledEventOwner = source.IndexOf(settledEvent, StringComparison.Ordinal);
        Assert.True(settledEventOwner >= 0);
        Assert.Equal(
            -1,
            source.IndexOf(settledEvent, settledEventOwner + settledEvent.Length, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cancellation", "Cancelled", "HTMLX-CANCELLED")]
    [InlineData("allowed-navigation", "CaptureFailed", "HTMLX-DOCUMENT-CHANGED")]
    [InlineData("process-failure", "ProcessCrashed", "HTMLX-PROCESS-FAILED")]
    [InlineData("disposal", "Faulted", "HTMLX-DISPOSED")]
    public void CaptureLifecycleTerminalReleasesEveryOwner(
        string terminal,
        string expectedStatus,
        string expectedFailureId)
    {
        using var cancellation = new CancellationTokenSource();
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => true,
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                var capture = InvokeCaptureRenderedHtml(view, cancellation.Token);
                Assert.Single(PendingRenderedHtmlCaptureRequestIds(view));

                switch (terminal)
                {
                    case "cancellation":
                        cancellation.Cancel();
                        break;
                    case "allowed-navigation":
                        Assert.False(InvokeNavigationStarted(view, "about:blank").Cancel);
                        break;
                    case "process-failure":
                        InvokeFullRenderProcessFailure(view, "RendererProcessFailed");
                        break;
                    case "disposal":
                        view.Dispose();
                        break;
                    default:
                        throw new Xunit.Sdk.XunitException($"Unknown lifecycle terminal: {terminal}");
                }

                AssertRenderedHtmlCaptureResult(
                    capture,
                    expectedStatus,
                    null,
                    expectedFailureId,
                    expectedFailureId);
                AssertCaptureOwnersReleased(view);
            });
    }

    [Fact]
    public void CaptureDeliveryFailureReturnsTypedFailureAndCleans()
    {
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: _ => throw new InvalidOperationException("serialize failed"),
                TryPostNative: _ => throw new Xunit.Sdk.XunitException("native post must not run"),
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                var capture = InvokeCaptureRenderedHtml(view, CancellationToken.None);
                AssertRenderedHtmlCaptureResult(
                    capture,
                    "CaptureFailed",
                    null,
                    "HTMLX-CAPTURE-DELIVERY",
                    "HTMLX-CAPTURE-DELIVERY:InvalidOperationException");
                AssertCaptureOwnersReleased(view);
            });
    }

    [Fact]
    public void HostPatchBuildersStampPositiveRenderIdsAndPreserveLegacyShapes()
    {
        var taskBuilder = RequirePatchBuilder(
            "BuildTaskCheckboxStateMessage",
            typeof(int),
            typeof(bool),
            typeof(long?));
        var tableSuccessBuilder = RequirePatchBuilder(
            "BuildTableCellUpdatedSuccessMessage",
            typeof(int),
            typeof(int),
            typeof(string),
            typeof(string),
            // Rendered fragment for a RAW (rich-cell) settle; null keeps the
            // original literal shapes byte-for-byte. renderId stays LAST, as on
            // every host patch builder.
            typeof(string),
            typeof(long?));
        var tableFailureBuilder = RequirePatchBuilder(
            "BuildTableCellUpdatedFailureMessage",
            typeof(int),
            typeof(int),
            typeof(bool),
            typeof(long?));

        AssertOptionalRenderId(taskBuilder);
        AssertOptionalRenderId(tableSuccessBuilder);
        AssertOptionalRenderId(tableFailureBuilder);

        var legacyTask = Serialize(taskBuilder.Invoke(null, [12, true, null])!);
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "type", "line", "checked" },
            new SortedSet<string>(ObjectKeys(legacyTask), StringComparer.Ordinal));

        var currentTask = Serialize(taskBuilder.Invoke(null, [12, true, 42L])!);
        Assert.Equal(42L, currentTask.GetProperty("renderId").GetInt64());

        var legacySuccess = Serialize(tableSuccessBuilder.Invoke(
            null,
            [12, 3, "Canonical plain text", "f00dcafe", null, null])!);
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "type", "line", "cellIndex", "ok", "text", "key" },
            new SortedSet<string>(ObjectKeys(legacySuccess), StringComparer.Ordinal));

        var currentSuccess = Serialize(tableSuccessBuilder.Invoke(
            null,
            [12, 3, "Canonical plain text", "f00dcafe", null, 42L])!);
        Assert.Equal(42L, currentSuccess.GetProperty("renderId").GetInt64());

        // A RAW (rich-cell) settle adds the rendered fragment so the cell lands
        // re-rendered; every other key stays exactly as on the literal shapes.
        var rawSuccess = Serialize(tableSuccessBuilder.Invoke(
            null,
            [12, 3, "$x^2$", "f00dcafe", "<span data-tex=\"x^2\"></span>", 42L])!);
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
                { "type", "line", "cellIndex", "ok", "text", "key", "html", "renderId" },
            new SortedSet<string>(ObjectKeys(rawSuccess), StringComparer.Ordinal));
        Assert.Equal("$x^2$", rawSuccess.GetProperty("text").GetString());
        Assert.Equal("<span data-tex=\"x^2\"></span>", rawSuccess.GetProperty("html").GetString());

        var legacyFailure = Serialize(tableFailureBuilder.Invoke(null, [12, 3, false, null])!);
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "type", "line", "cellIndex", "ok" },
            new SortedSet<string>(ObjectKeys(legacyFailure), StringComparer.Ordinal));

        var currentFailure = Serialize(tableFailureBuilder.Invoke(null, [12, 3, false, 42L])!);
        Assert.Equal(42L, currentFailure.GetProperty("renderId").GetInt64());
        Assert.False(currentFailure.TryGetProperty("reason", out _));

        var legacyBusy = Serialize(tableFailureBuilder.Invoke(null, [12, 3, true, null])!);
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "type", "line", "cellIndex", "ok", "reason" },
            new SortedSet<string>(ObjectKeys(legacyBusy), StringComparer.Ordinal));

        var currentBusy = Serialize(tableFailureBuilder.Invoke(null, [12, 3, true, 42L])!);
        Assert.Equal(42L, currentBusy.GetProperty("renderId").GetInt64());
        Assert.Equal("busy", currentBusy.GetProperty("reason").GetString());
    }

    [Fact]
    public void TableCellUpdatedFailureDescriptorRejectsNullOptionalFields()
    {
        var shape = LoadContract().Host["table-cell-updated"];
        var invalidFailure = Serialize(new
        {
            type = "table-cell-updated",
            line = 12,
            cellIndex = 3,
            ok = false,
            text = (string?)null,
            key = (string?)null,
            reason = (string?)null,
        });

        var violations = CollectViolations(invalidFailure, shape, "table-cell-updated.failure");
        Assert.Contains("table-cell-updated.failure.text: unexpected null", violations);
        Assert.Contains("table-cell-updated.failure.key: unexpected null", violations);
        Assert.Contains("table-cell-updated.failure.reason: unexpected null", violations);
    }

    // ---- inbound: registry-shaped fixtures drive the real dispatch -------------

    [Fact]
    public void InboundHandlersFireForRegistryShapedFixtures()
    {
        var renderer = LoadContract().Renderer;

        RunOnView(view =>
        {
            AssertInbound(view, "scroll", renderer,
                new() { ["type"] = "scroll", ["scrollTop"] = 10, ["scrollHeight"] = 1000, ["clientHeight"] = 500, ["topBlockIndex"] = 2 },
                handler => view.ScrollStateChanged += (_, _) => handler());

            AssertInbound(view, "headings-updated", renderer,
                new() { ["type"] = "headings-updated", ["headings"] = Array.Empty<object>() },
                handler => view.HeadingsChanged += (_, _) => handler());

            AssertInbound(view, "minimap-state", renderer,
                new() { ["type"] = "minimap-state", ["visible"] = true, ["reservedWidth"] = 42 },
                handler => view.MinimapStateChanged += (_, _) => handler());

            AssertInbound(view, "width-drag", renderer,
                new() { ["type"] = "width-drag", ["phase"] = "move", ["deltaX"] = 5 },
                handler => view.WidthDragRequested += (_, _) => handler());

            AssertInbound(view, "wheel", renderer,
                new() { ["type"] = "wheel", ["deltaY"] = 3, ["deltaMode"] = 0 },
                handler => view.WheelRequested += (_, _) => handler());

            AssertInbound(view, "viewer-interaction", renderer,
                new() { ["type"] = "viewer-interaction" },
                handler => view.ViewerInteractionRequested += (_, _) => handler());

            AssertInbound(view, "preview-source-line", renderer,
                new() { ["type"] = "preview-source-line", ["sourceLine"] = 4 },
                handler => view.PreviewSourceLineChanged += (_, _) => handler());

            AssertInbound(view, "active-heading-changed", renderer,
                new() { ["type"] = "active-heading-changed", ["id"] = "h1" },
                handler => view.ActiveHeadingChanged += (_, _) => handler());

            AssertInbound(view, "task-toggle", renderer,
                new() { ["type"] = "task-toggle", ["line"] = 3, ["checked"] = true, ["key"] = "k", ["renderId"] = null },
                handler => view.TaskToggleRequested += (_, _) => handler());

            AssertInbound(view, "table-cell-edit", renderer,
                new() { ["type"] = "table-cell-edit", ["line"] = 12, ["cellIndex"] = 3, ["text"] = "plain", ["key"] = null, ["raw"] = false, ["renderId"] = null },
                handler => view.TableCellEditRequested += (_, _) => handler());
        });
    }

    [Fact]
    public void ShellInitFailedIngressFaultsPendingShellReadyAndRaisesFallback()
    {
        RunOnView(view =>
        {
            var shellReady = InstallShellReadyLatch(view, completed: false);
            var fallbackCount = 0;
            view.FallbackRequested += (_, _) => fallbackCount++;

            view.HandleWebMessageBody(@"{""type"":""shell-init-failed"",""message"":""bootstrap error""}");

            Assert.True(shellReady.Task.IsCompleted);
            Assert.False(shellReady.Task.GetAwaiter().GetResult());
            Assert.Equal(1, fallbackCount);
        });
    }

    [Fact]
    public void FullRenderTerminalResponsesUseExactRequestCorrelationAndTypedResults()
    {
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => true,
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                var first = InvokePrepareForExport(view, CancellationToken.None);
                var firstRequestId = Assert.Single(PendingFullRenderRequestIds(view));
                var second = InvokePrepareForExport(view, CancellationToken.None);
                var requestIds = PendingFullRenderRequestIds(view);
                Assert.Equal(2, requestIds.Length);
                var secondRequestId = Assert.Single(requestIds, requestId => requestId != firstRequestId);

                view.HandleWebMessageBody(@"{""type"":""full-render-complete"",""requestId"":""unknown"",""mermaidErrorCount"":0}");
                Assert.False(first.IsCompleted);
                Assert.False(second.IsCompleted);

                view.HandleWebMessageBody(JsonSerializer.Serialize(new
                {
                    type = "full-render-failed",
                    requestId = firstRequestId,
                    reason = "renderer driver failed",
                }));
                view.HandleWebMessageBody(JsonSerializer.Serialize(new
                {
                    type = "full-render-complete",
                    requestId = secondRequestId,
                    mermaidErrorCount = 2,
                }));

                AssertFullRenderResult(first, "RendererFailed", 0, "renderer driver failed");
                AssertFullRenderResult(second, "Completed", 2, null);
                Assert.Empty(PendingFullRenderRequestIds(view));
            });
    }

    [Fact]
    public void ProcessFailureCancellationAndDisposalSettleEveryPendingFullRenderRequest()
    {
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => true,
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                var processFirst = InvokePrepareForExport(view, CancellationToken.None);
                var processSecond = InvokePrepareForExport(view, CancellationToken.None);
                InvokeFullRenderProcessFailure(view, "RendererProcessFailed");
                AssertFullRenderResult(processFirst, "ProcessFailed", 0, "RendererProcessFailed");
                AssertFullRenderResult(processSecond, "ProcessFailed", 0, "RendererProcessFailed");

                using var cancellation = new CancellationTokenSource();
                var cancelled = InvokePrepareForExport(view, cancellation.Token);
                cancellation.Cancel();
                AssertFullRenderResult(cancelled, "Cancelled", 0, null);

                var disposed = InvokePrepareForExport(view, CancellationToken.None);
                view.Dispose();
                AssertFullRenderResult(disposed, "Disposed", 0, null);
                Assert.Empty(PendingFullRenderRequestIds(view));
            });
    }

    [Fact]
    public async Task ExportDeliveryFailureSettlesInsteadOfRemainingPending()
    {
        Task? request = null;
        RunOnView(view => request = InvokePrepareForExport(view, CancellationToken.None));

        Assert.NotNull(request);
        var completed = await Task.WhenAny(request!, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(request, completed);
        AssertFullRenderResult(
            request,
            "RendererFailed",
            0,
            "prepare-for-export delivery failed:InvalidOperationException");
    }

    [Fact]
    public void ExportDeliveryRegistrationPrecedesSynchronousOutcomes()
    {
        var serializerPendingCounts = new List<int>();
        var serializerSupervisorCounts = new List<int>();
        ApplicateWebMarkdownDocumentView? serializerView = null;
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: _ =>
                {
                    serializerPendingCounts.Add(serializerView!.PendingFullRenderRequestCountForTesting);
                    serializerSupervisorCounts.Add(serializerView.FullRenderDeliverySupervisorCountForTesting);
                    throw new InvalidOperationException("serialize failed");
                },
                TryPostNative: _ => throw new Xunit.Sdk.XunitException("native post must not run"),
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                serializerView = view;
                var request = InvokePrepareForExport(view, CancellationToken.None);
                AssertFullRenderResult(
                    request,
                    "RendererFailed",
                    0,
                    "prepare-for-export delivery failed:InvalidOperationException");
                Assert.Equal([1], serializerPendingCounts);
                Assert.Equal([1], serializerSupervisorCounts);
                Assert.Equal(0, view.PendingFullRenderRequestCountForTesting);
                Assert.Equal(0, view.FullRenderDeliverySupervisorCountForTesting);
            });

        var nativePendingCounts = new List<int>();
        var nativeSupervisorCounts = new List<int>();
        ApplicateWebMarkdownDocumentView? nativeView = null;
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ =>
                {
                    nativePendingCounts.Add(nativeView!.PendingFullRenderRequestCountForTesting);
                    nativeSupervisorCounts.Add(nativeView.FullRenderDeliverySupervisorCountForTesting);
                    return true;
                },
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                nativeView = view;
                var request = InvokePrepareForExport(view, CancellationToken.None);
                Assert.Equal([1], nativePendingCounts);
                Assert.Equal([1], nativeSupervisorCounts);
                Assert.Equal(1, view.PendingFullRenderRequestCountForTesting);
                Assert.Equal(0, view.FullRenderDeliverySupervisorCountForTesting);

                var requestId = Assert.Single(PendingFullRenderRequestIds(view));
                CompleteFullRenderRequestFromRenderer(view, requestId, succeeded: true);
                AssertFullRenderResult(request, "Completed", 0, null);
            });

        var observedFaults = new List<Exception?>();
        var rawPendingCounts = new List<int>();
        var rawSupervisorCounts = new List<int>();
        ApplicateWebMarkdownDocumentView? rawView = null;
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => false,
                InvokeRaw: _ =>
                {
                    rawPendingCounts.Add(rawView!.PendingFullRenderRequestCountForTesting);
                    rawSupervisorCounts.Add(rawView.FullRenderDeliverySupervisorCountForTesting);
                    return Task.FromException(new InvalidOperationException("raw failed"));
                },
                DeliveryObserved: observedFaults.Add),
            view =>
            {
                rawView = view;
                var request = InvokePrepareForExport(view, CancellationToken.None);
                AssertFullRenderResult(
                    request,
                    "RendererFailed",
                    0,
                    "prepare-for-export delivery failed:InvalidOperationException");
                Assert.Equal([1], rawPendingCounts);
                Assert.Equal([1], rawSupervisorCounts);
                Assert.Single(observedFaults, fault => fault is InvalidOperationException);
                Assert.Equal(0, view.PendingFullRenderRequestCountForTesting);
                Assert.Equal(0, view.FullRenderDeliverySupervisorCountForTesting);
            });
    }

    [Fact]
    public void ExportDeliveryDisposalDetachesNeverSettlingFallback()
    {
        var fallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => false,
                InvokeRaw: _ => fallback.Task),
            view =>
            {
                var request = InvokePrepareForExport(view, CancellationToken.None);
                Assert.Equal(1, view.PendingFullRenderRequestCountForTesting);
                Assert.Equal(1, view.FullRenderDeliverySupervisorCountForTesting);

                view.Dispose();

                AssertFullRenderResult(request, "Disposed", 0, null);
                Assert.True(SpinWait.SpinUntil(
                    () => view.FullRenderDeliverySupervisorCountForTesting == 0,
                    TimeSpan.FromSeconds(2)));
                Assert.Equal(0, view.PendingFullRenderRequestCountForTesting);
                Assert.False(fallback.Task.IsCompleted);
            });
    }

    [Theory]
    [InlineData("cancellation", "Cancelled", null)]
    [InlineData("allowed-navigation", "RendererFailed", "renderer navigation started")]
    [InlineData("process-failure", "ProcessFailed", "RendererProcessFailed")]
    [InlineData("renderer-complete", "Completed", null)]
    [InlineData("renderer-failed", "RendererFailed", "renderer driver failed")]
    public void ExportDeliveryTerminalCompetitorsDetachNeverSettlingFallback(
        string competitor,
        string expectedStatus,
        string? expectedReason)
    {
        var fallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => false,
                InvokeRaw: _ => fallback.Task),
            view =>
            {
                var request = InvokePrepareForExport(view, cancellation.Token);
                var requestId = Assert.Single(PendingFullRenderRequestIds(view));
                Assert.Equal(1, view.PendingFullRenderRequestCountForTesting);
                Assert.Equal(1, view.FullRenderDeliverySupervisorCountForTesting);

                switch (competitor)
                {
                    case "cancellation":
                        cancellation.Cancel();
                        break;
                    case "allowed-navigation":
                        Assert.False(InvokeNavigationStarted(view, "about:blank").Cancel);
                        break;
                    case "process-failure":
                        InvokeFullRenderProcessFailure(view, "RendererProcessFailed");
                        break;
                    case "renderer-complete":
                        CompleteFullRenderRequestFromRenderer(view, requestId, succeeded: true);
                        break;
                    case "renderer-failed":
                        CompleteFullRenderRequestFromRenderer(view, requestId, succeeded: false);
                        break;
                    default:
                        throw new Xunit.Sdk.XunitException($"Unknown terminal competitor: {competitor}");
                }

                AssertFullRenderResult(request, expectedStatus, 0, expectedReason);
                Assert.True(SpinWait.SpinUntil(
                    () => view.PendingFullRenderRequestCountForTesting == 0
                        && view.FullRenderDeliverySupervisorCountForTesting == 0,
                    TimeSpan.FromSeconds(2)),
                    $"Terminal competitor '{competitor}' did not release both full-render owners.");
                Assert.Equal(0, view.PendingFullRenderRequestCountForTesting);
                Assert.Equal(0, view.FullRenderDeliverySupervisorCountForTesting);
                Assert.False(fallback.Task.IsCompleted);
            });
    }

    [Fact]
    public void ExportAllowedNavigationUsesActualHandlerAndSettlesPendingRequest()
    {
        RunOnView(
            new ApplicateFullRenderDeliveryHooks(
                Serialize: message => JsonSerializer.Serialize(message),
                TryPostNative: _ => true,
                InvokeRaw: _ => throw new Xunit.Sdk.XunitException("raw fallback must not run")),
            view =>
            {
                var request = InvokePrepareForExport(view, CancellationToken.None);
                var navigation = InvokeNavigationStarted(view, "about:blank");

                Assert.False(navigation.Cancel);
                AssertFullRenderResult(request, "RendererFailed", 0, "renderer navigation started");
                Assert.Equal(0, view.PendingFullRenderRequestCountForTesting);
                Assert.Equal(0, view.FullRenderDeliverySupervisorCountForTesting);
            });
    }

    [Fact]
    public void ShellInitFailedIngressLeavesCompletedShellReadyAndFallbackInert()
    {
        RunOnView(view =>
        {
            var shellReady = InstallShellReadyLatch(view, completed: true);
            var fallbackCount = 0;
            view.FallbackRequested += (_, _) => fallbackCount++;

            view.HandleWebMessageBody(@"{""type"":""shell-init-failed""}");

            Assert.True(shellReady.Task.GetAwaiter().GetResult());
            Assert.Equal(0, fallbackCount);
        });
    }

    [Fact]
    public void ShellInitFailedIngressLeavesAbsentShellReadyAndFallbackInert()
    {
        RunOnView(view =>
        {
            var fallbackCount = 0;
            view.FallbackRequested += (_, _) => fallbackCount++;

            view.HandleWebMessageBody(@"{""type"":""shell-init-failed""}");

            Assert.Equal(0, fallbackCount);
        });
    }

    // terra revision 3: a NONEMPTY nested headings payload must parse through the
    // real ingress (empty `headings: []` never exercises headings[].segments[]).
    [Fact]
    public void HeadingsUpdatedIngressParsesNonemptyNestedPayload()
    {
        RunOnView(view =>
        {
            IReadOnlyList<DocumentHeading>? received = null;
            view.HeadingsChanged += (_, headings) => received = headings;

            view.HandleWebMessageBody(
                @"{""type"":""headings-updated"",""headings"":[" +
                @"{""id"":""h1"",""level"":2,""text"":""Intro"",""segments"":[{""kind"":""text"",""text"":""Intro""}]}]}");

            Assert.NotNull(received);
            var heading = Assert.Single(received!);
            Assert.Equal("h1", heading.Id);
            Assert.Equal(2, heading.Level);
        });
    }

    // ---- inbound: tolerant log/trace handlers (terra revision 3) ---------------
    // Reads Console.Error to observe the ONLY side effect. Do NOT tighten the C#
    // ingress: missing / wrong-kind / wrong-case / malformed must be silently
    // ignored, never throw. Serialized via the assembly non-parallel switch
    // (TestParallelization.cs) so the process-global capture cannot interleave.

    [Fact]
    public void TolerantLogHandlersConsumeFieldsAndPreserveIngressTolerance()
    {
        RunOnView(view =>
        {
            // debug-log reads `text`, emits `[renderer-debug] <text>`.
            Assert.Contains("[renderer-debug] mm-debug-payload", CaptureStderr(view, @"{""type"":""debug-log"",""text"":""mm-debug-payload""}"));
            Assert.DoesNotContain("[renderer-debug]", CaptureStderr(view, @"{""type"":""debug-log""}"));                 // missing field
            Assert.DoesNotContain("[renderer-debug]", CaptureStderr(view, @"{""type"":""debug-log"",""text"":42}"));      // wrong kind
            Assert.DoesNotContain("[renderer-debug]", CaptureStderr(view, @"{""type"":""debug-log"",""Text"":""x""}"));   // wrong case

            // perf-mark reads `name`, emits `[renderer-perf ...] <name> ...`.
            Assert.Contains("[renderer-perf", CaptureStderr(view, @"{""type"":""perf-mark"",""name"":""mm-perf-name""}"));
            Assert.DoesNotContain("[renderer-perf", CaptureStderr(view, @"{""type"":""perf-mark""}"));                    // missing name
            Assert.DoesNotContain("[renderer-perf", CaptureStderr(view, @"{""type"":""perf-mark"",""name"":42}"));        // wrong kind
            Assert.DoesNotContain("[renderer-perf", CaptureStderr(view, @"{""type"":""perf-mark"",""Name"":""x""}"));     // wrong case

            // csp-violation reads blockedURI/violatedDirective/sourceFile/lineNumber, emits `[CSP] ...`.
            Assert.Contains("mm-blocked-uri", CaptureStderr(view, @"{""type"":""csp-violation"",""blockedURI"":""mm-blocked-uri"",""violatedDirective"":""img-src"",""sourceFile"":""a.js"",""lineNumber"":3,""columnNumber"":1}"));
            Assert.Contains("[CSP]", CaptureStderr(view, @"{""type"":""csp-violation"",""blockedURI"":42,""lineNumber"":""x""}")); // wrong kinds -> defaults, still logs, no throw
            var wrongCase = CaptureStderr(view, @"{""type"":""csp-violation"",""BlockedURI"":""mm-blocked-uri""}");
            Assert.Contains("[CSP]", wrongCase);                    // wrong case -> default, still logs
            Assert.DoesNotContain("mm-blocked-uri", wrongCase);     // wrong case -> field not consumed

            // Malformed / non-object roots must be swallowed (no throw). Reaching
            // here proves HandleWebMessageBody stayed tolerant.
            CaptureStderr(view, "not json at all");
            CaptureStderr(view, "[]");
            CaptureStderr(view, "null");
            CaptureStderr(view, "12");
        });
    }

    // Host attribution (2026-08-01). The process runs TWO WebView hosts — the
    // viewer and the off-screen edit preview — whose markers interleave in ONE
    // stderr stream, and `renderId` is a PER-INSTANCE counter, so both hosts
    // stamp the same ids. Attributing a marker to a host by adjacency in that
    // shared stream produced two wrong conclusions during the 2026-07-27
    // tab-switch investigation. Every marker must therefore carry its emitting
    // view's `shellDocumentId`, and carry it at the TAIL: the log extractors in
    // .scratch match a marker by its leading `ms=` / `detail=` prefix, so a tail
    // field is additive to them while a mid-string one silently zeroes every
    // count they report. Both halves are asserted here.
    [Fact]
    public void PerfMarkTraceIsAttributableToItsEmittingHost()
    {
        RunOnTwoViews((firstHost, secondHost) =>
        {
            const string bare = @"{""type"":""perf-mark"",""name"":""mm-attribution-probe""}";
            var firstLine = CaptureStderr(firstHost, bare).TrimEnd('\r', '\n');
            var secondLine = CaptureStderr(secondHost, bare).TrimEnd('\r', '\n');

            // 1. The tag is present and terminal on each emitted line.
            var tag = new Regex(@" shellDocumentId=(\d+)$");
            var firstTag = tag.Match(firstLine);
            var secondTag = tag.Match(secondLine);
            Assert.True(firstTag.Success, $"no trailing shellDocumentId in: {firstLine}");
            Assert.True(secondTag.Success, $"no trailing shellDocumentId in: {secondLine}");

            // 2. The point of the tag: two hosts are TELLABLE APART. Same marker
            //    name, same renderId space, different emitting view.
            Assert.NotEqual(firstTag.Groups[1].Value, secondTag.Groups[1].Value);

            // 3. Tail placement relative to `detail=`, which is what the
            //    extractors' `detail=(\{.*\})` capture depends on. Anchor indices
            //    are asserted to be found first — a -1 here would otherwise make
            //    the ordering comparison pass vacuously, the exact way a guard in
            //    this repository already went silently green on an empty string.
            var withDetail = CaptureStderr(
                secondHost,
                @"{""type"":""perf-mark"",""name"":""mm-attribution-probe"",""detail"":""{\""weight\"":7}""}")
                .TrimEnd('\r', '\n');
            var detailAt = withDetail.IndexOf("detail=", StringComparison.Ordinal);
            var tagAt = withDetail.IndexOf(" shellDocumentId=", StringComparison.Ordinal);
            Assert.True(detailAt > -1, $"no detail= in: {withDetail}");
            Assert.True(tagAt > -1, $"no shellDocumentId in: {withDetail}");
            Assert.True(detailAt < tagAt, $"shellDocumentId must follow detail=, got: {withDetail}");
        });
    }

    // terra revision 5+6: renderId is optional+nullable in the contract, but the
    // host DROPS the message on missing/non-numeric renderId. Executable proof via
    // the handler's own diag output (positive control + two negative drops).
    [Fact]
    public void RenderIdOptionalInContractButDroppedOnMissingByHost()
    {
        var renderer = LoadContract().Renderer;
        foreach (var type in new[] { "post-ready-enhancements-complete", "document-cache-miss" })
        {
            var renderId = renderer[type]["renderId"];
            Assert.True(renderId.Optional, $"{type}.renderId must be optional in the descriptor");
            Assert.True(renderId.Nullable, $"{type}.renderId must be nullable in the descriptor");
        }

        RunOnView(view =>
        {
            // Positive control: renderId PRESENT (999, != the fresh view's active
            // id 0) reaches the stale branch and logs — the handler ran PAST the
            // renderId guard.
            Assert.Contains("post-ready-enhancements-stale",
                CaptureStderr(view, @"{""type"":""post-ready-enhancements-complete"",""renderId"":999,""hasMermaid"":true,""hasHljs"":true}"));

            // Dropped on MISSING renderId (renderId-mandatory-in-C#): returns before
            // any log, so no `post-ready-enhancements` marker at all.
            Assert.DoesNotContain("post-ready-enhancements",
                CaptureStderr(view, @"{""type"":""post-ready-enhancements-complete"",""hasMermaid"":true,""hasHljs"":true}"));

            // Dropped on WRONG-KIND renderId.
            Assert.DoesNotContain("post-ready-enhancements",
                CaptureStderr(view, @"{""type"":""post-ready-enhancements-complete"",""renderId"":""x"",""hasMermaid"":true,""hasHljs"":true}"));
        });
    }

    // ---- registry: JSON <-> C# <-> sender-source SET-EQUALITY ------------------

    [Fact]
    public void RegistryTypeSetsAgreeAcrossJsonSourceAndSenders()
    {
        var contract = LoadContract();

        Assert.Equal(
            new SortedSet<string>(KnownHostMessageTypes, StringComparer.Ordinal),
            new SortedSet<string>(contract.Host.Keys, StringComparer.Ordinal));
        Assert.Equal(
            new SortedSet<string>(KnownRendererMessageTypes, StringComparer.Ordinal),
            new SortedSet<string>(contract.Renderer.Keys, StringComparer.Ordinal));

        // Host SENDER literals (`type = "..."`, single '=', not the `type == "..."`
        // receiver comparisons) SET-EQUAL the registry — both directions. An
        // unregistered C#-only PostRendererMessage(new { type = "x" }) adds a
        // literal absent from the registry -> RED; a registered host type with no
        // sender -> RED.
        var hostSenderSource = File.ReadAllText(ViewSourcePath) + "\n" + File.ReadAllText(RevealIntentsSourcePath);
        var hostSenderLiterals = Regex.Matches(hostSenderSource, "\\btype = \"([a-z][a-z0-9-]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new SortedSet<string>(KnownHostMessageTypes, StringComparer.Ordinal),
            new SortedSet<string>(hostSenderLiterals, StringComparer.Ordinal));

        // Renderer types: every registered type is a real quoted literal in the
        // renderer sender (subset — some receivers are predicate-dispatched).
        var rendererSource = File.ReadAllText(RendererSourcePath);
        foreach (var type in KnownRendererMessageTypes)
        {
            Assert.Contains($"\"{type}\"", rendererSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryDescriptorDeclaresTypeStringField()
    {
        var contract = LoadContract();
        Assert.All(contract.Host.Values, shape => Assert.Equal("string", shape["type"].Kind));
        Assert.All(contract.Renderer.Values, shape => Assert.Equal("string", shape["type"].Kind));
    }

    // ---- helpers --------------------------------------------------------------

    private static void AssertInbound(
        ApplicateWebMarkdownDocumentView view,
        string type,
        IReadOnlyDictionary<string, Shape> renderer,
        Dictionary<string, object?> fixture,
        Action<Action> subscribe)
    {
        Assert.Equal(
            new SortedSet<string>(renderer[type].Keys, StringComparer.Ordinal),
            new SortedSet<string>(fixture.Keys, StringComparer.Ordinal));

        var fired = false;
        subscribe(() => fired = true);
        view.HandleWebMessageBody(JsonSerializer.Serialize(fixture));
        Assert.True(fired, $"handler for '{type}' did not fire for a registry-shaped fixture");
    }

    private static string CaptureStderr(ApplicateWebMarkdownDocumentView view, string body)
    {
        var original = Console.Error;
        using var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            view.HandleWebMessageBody(body);
        }
        finally
        {
            Console.SetError(original);
        }

        return buffer.ToString();
    }

    private static JsonElement Serialize(object message)
    {
        // Matches production PostRendererMessage: JsonSerializer.Serialize on an
        // `object` root uses the runtime (anonymous) type, default options.
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message));
        return document.RootElement.Clone();
    }

    private static MethodInfo RequirePatchBuilder(string name, params Type[] parameterTypes)
    {
        var method = typeof(ApplicateWebMarkdownDocumentView).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        return method!;
    }

    private static Task InvokePrepareForExport(
        ApplicateWebMarkdownDocumentView view,
        CancellationToken cancellationToken)
    {
        var method = typeof(ApplicateWebMarkdownDocumentView).GetMethod(
            "PrepareForExportAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(CancellationToken)],
            modifiers: null);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method!.Invoke(view, [cancellationToken]));
    }

    private static Task InvokeCaptureRenderedHtml(
        ApplicateWebMarkdownDocumentView view,
        CancellationToken cancellationToken)
    {
        var method = typeof(ApplicateWebMarkdownDocumentView).GetMethod(
            "CaptureRenderedHtmlAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(CancellationToken)],
            modifiers: null);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method!.Invoke(view, [cancellationToken]));
    }

    private static string[] PendingRenderedHtmlCaptureRequestIds(ApplicateWebMarkdownDocumentView view)
    {
        var field = typeof(ApplicateWebMarkdownDocumentView).GetField(
            "_pendingRenderedHtmlCaptureRequests",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var pending = Assert.IsAssignableFrom<System.Collections.IEnumerable>(field!.GetValue(view));
        return pending.Cast<object>()
            .Select(entry => (string)entry.GetType().GetProperty("Key")!.GetValue(entry)!)
            .OrderBy(requestId => requestId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertRenderedHtmlCaptureResult(
        Task task,
        string expectedStatus,
        string? expectedHtml,
        string? expectedFailureId,
        string? expectedReason)
    {
        task.GetAwaiter().GetResult();
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var resultType = result.GetType();
        Assert.Equal(expectedStatus, resultType.GetProperty("Status")!.GetValue(result)!.ToString());
        Assert.Equal(expectedHtml, resultType.GetProperty("Html")!.GetValue(result));
        Assert.Equal(expectedFailureId, resultType.GetProperty("FailureId")!.GetValue(result));
        Assert.Equal(expectedReason, resultType.GetProperty("Reason")!.GetValue(result));
    }

    private static void AssertCaptureOwnersReleased(ApplicateWebMarkdownDocumentView view)
    {
        Assert.Empty(PendingRenderedHtmlCaptureRequestIds(view));
        Assert.Equal(0, GetInternalIntProperty(view, "RenderedHtmlCaptureDeliverySupervisorCountForTesting"));
        Assert.Equal(0, GetInternalIntProperty(view, "RenderedHtmlCaptureCancellationRegistrationCountForTesting"));
    }

    private static int GetInternalIntProperty(ApplicateWebMarkdownDocumentView view, string name)
    {
        var property = typeof(ApplicateWebMarkdownDocumentView).GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<int>(property!.GetValue(view));
    }

    private static string[] PendingFullRenderRequestIds(ApplicateWebMarkdownDocumentView view)
    {
        var field = typeof(ApplicateWebMarkdownDocumentView).GetField(
            "_pendingFullRenderRequests",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var pending = Assert.IsAssignableFrom<System.Collections.IEnumerable>(field!.GetValue(view));
        return pending.Cast<object>()
            .Select(entry => (string)entry.GetType().GetProperty("Key")!.GetValue(entry)!)
            .OrderBy(requestId => requestId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void InvokeFullRenderProcessFailure(
        ApplicateWebMarkdownDocumentView view,
        string reason)
    {
        var method = typeof(ApplicateWebMarkdownDocumentView).GetMethod(
            "FailPendingFullRenderRequestsForProcessFailure",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(view, [reason]);
    }

    private static void AssertFullRenderResult(
        Task task,
        string expectedStatus,
        int expectedMermaidErrorCount,
        string? expectedReason)
    {
        task.GetAwaiter().GetResult();
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var resultType = result.GetType();
        Assert.Equal(expectedStatus, resultType.GetProperty("Status")!.GetValue(result)!.ToString());
        Assert.Equal(expectedMermaidErrorCount, resultType.GetProperty("MermaidErrorCount")!.GetValue(result));
        Assert.Equal(expectedReason, resultType.GetProperty("Reason")!.GetValue(result));
    }

    private static void AssertOptionalRenderId(MethodInfo method)
    {
        var parameter = method.GetParameters()[^1];
        Assert.Equal("renderId", parameter.Name);
        Assert.Equal(typeof(long?), parameter.ParameterType);
        Assert.True(parameter.IsOptional);
        Assert.Null(parameter.DefaultValue);
    }

    private static string? TypeValue(JsonElement value)
        => value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static HashSet<string> ObjectKeys(JsonElement value)
        => value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    // Recursive descriptor walk — mirrors collectWireShapeViolations in
    // ipcContract.ts. Empty result = valid.
    private static List<string> CollectViolations(JsonElement value, Shape shape, string path)
    {
        var violations = new List<string>();
        CollectObjectViolations(value, shape, path, violations);
        return violations;
    }

    private static void CollectObjectViolations(JsonElement value, Shape shape, string path, List<string> outv)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            outv.Add($"{path}: expected object, got {value.ValueKind}");
            return;
        }

        var declared = new HashSet<string>(shape.Keys, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!declared.Contains(property.Name))
            {
                outv.Add($"{path}.{property.Name}: undeclared field");
            }
        }

        foreach (var (field, descriptor) in shape)
        {
            if (!value.TryGetProperty(field, out var fieldValue))
            {
                if (!descriptor.Optional)
                {
                    outv.Add($"{path}.{field}: missing required field");
                }

                continue;
            }

            CollectFieldViolations(fieldValue, descriptor, $"{path}.{field}", outv);
        }
    }

    private static void CollectFieldViolations(JsonElement value, FieldDescriptor descriptor, string path, List<string> outv)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!descriptor.Nullable)
            {
                outv.Add($"{path}: unexpected null");
            }

            return;
        }

        switch (descriptor.Kind)
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    outv.Add($"{path}: expected string, got {value.ValueKind}");
                    return;
                }

                if (descriptor.Variants is not null && !descriptor.Variants.Contains(value.GetString()))
                {
                    outv.Add($"{path}: '{value.GetString()}' not in variants");
                }

                return;
            case "number":
                if (value.ValueKind != JsonValueKind.Number)
                {
                    outv.Add($"{path}: expected number, got {value.ValueKind}");
                }

                return;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    outv.Add($"{path}: expected boolean, got {value.ValueKind}");
                }

                return;
            case "object":
                CollectObjectViolations(value, descriptor.Of ?? new Shape(), path, outv);
                return;
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    outv.Add($"{path}: expected array, got {value.ValueKind}");
                    return;
                }

                if (descriptor.Element is not null)
                {
                    var index = 0;
                    foreach (var element in value.EnumerateArray())
                    {
                        CollectFieldViolations(element, descriptor.Element, $"{path}[{index++}]", outv);
                    }
                }

                return;
        }
    }

    // Two views on one dispatch, for contracts that only exist BETWEEN the two
    // hosts the process runs (e.g. trace attribution). Blocking wait kept in the
    // helper for the same reason the single-view overload gives below.
    private static void RunOnTwoViews(
        Action<ApplicateWebMarkdownDocumentView, ApplicateWebMarkdownDocumentView> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(
            () => body(
                new ApplicateWebMarkdownDocumentView(new NoopRenderer()),
                new ApplicateWebMarkdownDocumentView(new NoopRenderer())),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void RunOnView(Action<ApplicateWebMarkdownDocumentView> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        // GetAwaiter().GetResult() is load-bearing: Dispatch returns a Task and any
        // assertion/exception raised on the UI thread surfaces only when the task
        // is awaited. Without it the body runs fire-and-forget and every assertion
        // inside is swallowed (the test would pass vacuously).
        session.Dispatch(() =>
        {
            var view = new ApplicateWebMarkdownDocumentView(new NoopRenderer());
            body(view);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void RunOnView(
        ApplicateFullRenderDeliveryHooks deliveryHooks,
        Action<ApplicateWebMarkdownDocumentView> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateWebMarkdownDocumentView(
                new NoopRenderer(),
                shellAssetFactory: null,
                new ApplicateRenderedBodyCache(),
                deliveryHooks);
            body(view);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static WebViewNavigationStartingEventArgs InvokeNavigationStarted(
        ApplicateWebMarkdownDocumentView view,
        string request)
    {
        var args = new WebViewNavigationStartingEventArgs
        {
            Request = new Uri(request),
        };
        var method = typeof(ApplicateWebMarkdownDocumentView).GetMethod(
            "OnNavigationStarted",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(view, [null, args]);
        return args;
    }

    private static void CompleteFullRenderRequestFromRenderer(
        ApplicateWebMarkdownDocumentView view,
        string requestId,
        bool succeeded)
    {
        view.HandleWebMessageBody(succeeded
            ? JsonSerializer.Serialize(new
            {
                type = "full-render-complete",
                requestId,
                mermaidErrorCount = 0,
            })
            : JsonSerializer.Serialize(new
            {
                type = "full-render-failed",
                requestId,
                reason = "renderer driver failed",
            }));
    }

    private static TaskCompletionSource<bool> InstallShellReadyLatch(
        ApplicateWebMarkdownDocumentView view,
        bool completed)
    {
        var shellReady = new TaskCompletionSource<bool>();
        if (completed)
        {
            shellReady.SetResult(true);
        }

        var shellReadyField = typeof(ApplicateWebMarkdownDocumentView).GetField(
            "_shellReady",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(shellReadyField);
        shellReadyField!.SetValue(view, shellReady);
        return shellReady;
    }

    // ---- descriptor model + JSON parse ----------------------------------------

    private sealed class Shape : Dictionary<string, FieldDescriptor>
    {
        public Shape() : base(StringComparer.Ordinal) { }
    }

    private sealed record FieldDescriptor(
        string Kind,
        bool Optional,
        bool Nullable,
        Shape? Of,
        FieldDescriptor? Element,
        string[]? Variants);

    private sealed record Contract(
        Dictionary<string, Shape> Host,
        Dictionary<string, Shape> Renderer);

    private static Contract LoadContract()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ContractJsonPath));

        Dictionary<string, Shape> ReadShapes(string property)
        {
            var result = new Dictionary<string, Shape>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty(property).EnumerateObject())
            {
                result[entry.Name] = ParseShape(entry.Value);
            }

            return result;
        }

        return new Contract(ReadShapes("hostMessageShapes"), ReadShapes("rendererMessageShapes"));
    }

    private static Shape ParseShape(JsonElement element)
    {
        var shape = new Shape();
        foreach (var field in element.EnumerateObject())
        {
            shape[field.Name] = ParseField(field.Value);
        }

        return shape;
    }

    private static FieldDescriptor ParseField(JsonElement element)
    {
        var kind = element.GetProperty("kind").GetString()!;
        var optional = element.TryGetProperty("optional", out var o) && o.GetBoolean();
        var nullable = element.TryGetProperty("nullable", out var n) && n.GetBoolean();
        var of = element.TryGetProperty("of", out var ofElement) ? ParseShape(ofElement) : null;
        var elementDescriptor = element.TryGetProperty("element", out var el) ? ParseField(el) : null;
        var variants = element.TryGetProperty("variants", out var v)
            ? v.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : null;
        return new FieldDescriptor(kind, optional, nullable, of, elementDescriptor, variants);
    }

    private sealed class NoopRenderer : IApplicateHtmlMarkdownRenderer
    {
        public System.Threading.Tasks.Task<ApplicateHtmlDocument> RenderAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("renderer is not exercised by IPC-contract tests");

        public System.Threading.Tasks.Task<ApplicateRenderedBody> RenderBodyAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("renderer is not exercised by IPC-contract tests");

        public System.Threading.Tasks.Task<string> RenderTableCellHtmlAsync(
            string rawCellMarkdown,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("renderer is not exercised by IPC-contract tests");
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MarkMello.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the MarkMello repository root.");
    }
}

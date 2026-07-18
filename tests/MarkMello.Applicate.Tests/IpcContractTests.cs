using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MarkMello.Applicate.Desktop");

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
        "document-reveal-prepare", "document-reveal-start",
    ];

    private static readonly string[] KnownRendererMessageTypes =
    [
        "document-ready", "layout-ready", "post-ready-enhancements-complete", "theme-applied",
        "link-clicked", "task-toggle", "table-cell-edit", "minimap-state", "minimap-settled", "scroll", "viewer-interaction",
        "wheel", "width-drag", "drag-hover", "drop-file", "host-shortcut", "debug-log", "perf-mark",
        "headings-updated", "active-heading-changed", "preview-source-line", "csp-violation",
        "document-cache-miss", "document-first-paint", "mode-toggle-settled",
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
                new() { ["type"] = "task-toggle", ["line"] = 3, ["checked"] = true, ["key"] = "k" },
                handler => view.TaskToggleRequested += (_, _) => handler());

            AssertInbound(view, "table-cell-edit", renderer,
                new() { ["type"] = "table-cell-edit", ["line"] = 12, ["cellIndex"] = 3, ["text"] = "plain", ["key"] = null, ["renderId"] = null },
                handler => view.TableCellEditRequested += (_, _) => handler());
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
    }
}

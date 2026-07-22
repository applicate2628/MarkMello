using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Headless coverage for the mode-toggle renderer priority-boost WORKAROUND: the
/// opt-out gate, the pure renderer-child resolver, and the generation-scoped
/// restore owner's full lifecycle + fail-through behaviour (with a fake native
/// boundary). The load-bearing safety invariant under test is: no renderer is
/// ever left permanently AboveNormal on any exit path.
/// </summary>
public sealed class ApplicateModeTogglePriorityBoostTests
{
    private static string SrcPath(string fileName) => Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Rendering",
        fileName);

    // ---------------------------------------------------------------- gate ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("anything-else")]
    public void GateDefaultsToEnabledForMissingAndUnknownValues(string? value)
        => Assert.True(ApplicateModeTogglePriorityBoostMode.ReadFromEnvironment(value));

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("FALSE")]
    [InlineData("  Off  ")]
    public void GateDisablesForExplicitFalseForms(string value)
        => Assert.False(ApplicateModeTogglePriorityBoostMode.ReadFromEnvironment(value));

    // ------------------------------------------------------------ resolver ----

    private const int BrowserPid = 1000;

    private static WindowsWebViewRendererPriorityNative.RendererProcessCandidate Candidate(
        int pid, int parentPid, string? commandLine)
        => new(pid, parentPid, commandLine);

    private static string RendererCmd(string extra = "")
        => $"\"C:\\Program Files\\EdgeWebView\\msedgewebview2.exe\" --type=renderer --lang=en-US {extra}".Trim();

    [Fact]
    public void ResolverSelectsDirectRendererChildrenOnly()
    {
        var candidates = new[]
        {
            Candidate(11, BrowserPid, RendererCmd("--renderer-client-id=5")),
            Candidate(12, BrowserPid, RendererCmd("--renderer-client-id=6")),
            Candidate(13, BrowserPid, "\"...msedgewebview2.exe\" --type=gpu-process"),
            Candidate(14, BrowserPid, "\"...msedgewebview2.exe\" --type=utility --utility-sub-type=network.mojom.NetworkService"),
            Candidate(15, BrowserPid, "\"...crashpad_handler.exe\" --monitor-self"),
            // Renderer, but not a direct child of the browser -> excluded.
            Candidate(16, 9999, RendererCmd()),
        };

        var selected = WindowsWebViewRendererPriorityNative.SelectRendererChildren(BrowserPid, candidates);

        var expected = new List<int> { 11, 12 };
        Assert.Equal(expected, selected);
    }

    [Fact]
    public void ResolverRejectsInexactRendererTypeToken()
    {
        var candidates = new[]
        {
            Candidate(21, BrowserPid, "\"exe\" --type=renderer-extension"),
            Candidate(22, BrowserPid, "\"exe\" --not-type=renderer"),
            Candidate(23, BrowserPid, "\"exe\" --type=render"),
        };

        Assert.Empty(WindowsWebViewRendererPriorityNative.SelectRendererChildren(BrowserPid, candidates));
    }

    [Theory]
    [InlineData("\"C:\\a b\\msedgewebview2.exe\" --type=renderer", true)]
    [InlineData("--type=renderer", true)]
    [InlineData("--foo --type=renderer --bar", true)]
    [InlineData("\"exe\" --type=gpu-process", false)]
    [InlineData("\"exe\" --type=renderer-x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasExactRendererTokenClassifiesCommandLines(string? commandLine, bool expected)
        => Assert.Equal(expected, WindowsWebViewRendererPriorityNative.HasExactRendererTypeToken(commandLine));

    [Fact]
    public void HasExactRendererTokenRejectsOversizedCommandLine()
    {
        var oversized = "--type=renderer " + new string('x', WindowsWebViewRendererPriorityNative.MaxCommandLineChars);
        Assert.False(WindowsWebViewRendererPriorityNative.HasExactRendererTypeToken(oversized));
    }

    // --------------------------------------------------- scope: success -------

    [Fact]
    public void SuccessTerminalRestoresEveryRendererToNormalAndClosesHandles()
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(Discovery(Lease(11, "A"), Lease(12, "B")));
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);
        Assert.Equal(new object[] { "A", "B" }, native.AppliedHandles);
        Assert.Equal(2, native.AboveNormal.Count);

        scope.Close(1, "success");

        Assert.Empty(native.AboveNormal); // never left bumped
        Assert.Equal(new object[] { "A", "B" }, native.RestoredHandles);
        Assert.Equal(new object[] { "A", "B" }, native.ClosedHandles);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("rollback:rapid-toggle")]
    [InlineData("rollback:cancel")]
    [InlineData("rollback:renderer-failure")]
    [InlineData("rollback:reconcile-slot-failed")]
    [InlineData("rollback:commit-slot-failed")]
    [InlineData("rollback:rejected-reveal")]
    [InlineData("rollback:dispose")]
    public void EveryTerminalRouteLeavesNoRendererBumpedAndClosesHandles(string reason)
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(Discovery(Lease(11, "A"), Lease(12, "B")));
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);
        scope.Close(1, reason);

        Assert.Empty(native.AboveNormal);
        Assert.Equal(2, native.RestoredHandles.Count);
        Assert.Equal(2, native.ClosedHandles.Count);
    }

    [Fact]
    public void DisposeRestoresAndClosesAnyLiveLease()
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(Discovery(Lease(11, "A")));
        var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);
        scope.Dispose();

        Assert.Empty(native.AboveNormal);
        Assert.Contains((object)"A", native.RestoredHandles);
        Assert.Contains((object)"A", native.ClosedHandles);
    }

    // ------------------------------------------- scope: replacement / stale ---

    [Fact]
    public void SupersedingGenerationClosesOldBeforeNewIsElevated()
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(Discovery(Lease(11, "A1"), Lease(12, "B1")));
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);

        // Admit a new generation before generation 1 reaches a terminal.
        scope.Arm(2, ApplicateMode.Viewer);

        // Old generation fully restored + closed, and no lease left bumped,
        // BEFORE generation 2 elevates anything.
        Assert.Empty(native.AboveNormal);
        Assert.Equal(new object[] { "A1", "B1" }, native.RestoredHandles);
        Assert.Equal(new object[] { "A1", "B1" }, native.ClosedHandles);

        native.EnqueueDiscovery(Discovery(Lease(21, "A2")));
        scope.BeginAfterFinalHide(2, BrowserPid);
        Assert.Equal(new object[] { "A2" }, native.AboveNormal.ToArray());

        scope.Close(2, "success");
        Assert.Empty(native.AboveNormal);
    }

    [Fact]
    public void StaleCloseCannotTouchTheCurrentGeneration()
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(Discovery(Lease(11, "A1")));
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);
        scope.Arm(2, ApplicateMode.Viewer); // supersede: closes generation 1
        native.EnqueueDiscovery(Discovery(Lease(21, "A2")));
        scope.BeginAfterFinalHide(2, BrowserPid);

        var restoredBefore = native.RestoredHandles.Count;
        scope.Close(1, "success"); // stale: generation 1 already gone

        Assert.Equal(restoredBefore, native.RestoredHandles.Count); // no-op
        Assert.Equal(new object[] { "A2" }, native.AboveNormal.ToArray()); // gen 2 untouched
    }

    [Fact]
    public void ArmedButNeverBumpedGenerationClosesCleanlyWithNoHandles()
    {
        var native = new FakeRendererPriorityNative();
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.Close(1, "rollback:reconcile-slot-failed"); // never began

        Assert.Empty(native.RestoredHandles);
        Assert.Empty(native.ClosedHandles);
        Assert.Empty(native.DiscoverCalls);
    }

    // -------------------------------------------------- scope: fail-through ---

    [Fact]
    public void NullBrowserPidFallsThroughUnbumpedWithNoNativeCalls()
    {
        var native = new FakeRendererPriorityNative();
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, browserProcessId: null);

        Assert.Empty(native.DiscoverCalls);
        Assert.Empty(native.AppliedHandles);
        Assert.Empty(native.AboveNormal);

        // Unbumped is terminal for the generation: a later begin does not retry.
        scope.BeginAfterFinalHide(1, BrowserPid);
        Assert.Empty(native.DiscoverCalls);

        scope.Close(1, "success"); // nothing to restore, no throw
        Assert.Empty(native.RestoredHandles);
    }

    [Fact]
    public void DiscoveryFailureFallsThroughUnbumped()
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(RendererPriorityDiscovery.Fail("snapshot", 5));
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);

        Assert.Empty(native.AppliedHandles);
        Assert.Empty(native.AboveNormal);
        scope.Close(1, "success");
        Assert.Empty(native.RestoredHandles);
    }

    [Fact]
    public void NoRendererMatchFallsThroughUnbumped()
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(RendererPriorityDiscovery.NoMatch);
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);

        Assert.Empty(native.AppliedHandles);
        Assert.Empty(native.AboveNormal);
    }

    [Fact]
    public void PartialElevationFailureUnwindsEveryChangedLease()
    {
        var native = new FakeRendererPriorityNative
        {
            // Fail AboveNormal on the second renderer only.
            ApplyPredicate = lease => (string)lease.Handle != "B",
        };
        native.EnqueueDiscovery(Discovery(Lease(11, "A"), Lease(12, "B")));
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);

        // The already-elevated "A" is restored to Normal; both handles are closed.
        Assert.Empty(native.AboveNormal);
        Assert.Contains((object)"A", native.RestoredHandles);
        Assert.DoesNotContain((object)"B", native.RestoredHandles);
        Assert.Equal(new object[] { "A", "B" }, native.ClosedHandles);
    }

    [Fact]
    public void FailThroughIsInertToLaterKnownHidesAndTerminals()
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(RendererPriorityDiscovery.Fail("open-inspect", 5));
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);
        scope.ReapplyAfterKnownHide("attach-completed", BrowserPid); // Unbumped -> no-op

        Assert.Single(native.DiscoverCalls); // exactly the one begin attempt
        Assert.Empty(native.AboveNormal);
    }

    // ------------------------------------------------ scope: PID reuse --------

    [Fact]
    public void RestorationUsesLeaseHandleNotReusedPid()
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(Discovery(Lease(1234, "A")));
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);

        // A known hide re-resolves membership; PID 1234 is reused by a DIFFERENT
        // process (handle "B"). The scope keeps the original retained handle "A".
        native.EnqueueDiscovery(Discovery(Lease(1234, "B")));
        scope.ReapplyAfterKnownHide("render-starting", BrowserPid);

        scope.Close(1, "success");

        // Restoration only ever targeted the ORIGINAL retained handle, never the
        // reused-PID replacement.
        Assert.DoesNotContain((object)"B", native.RestoredHandles);
        Assert.Contains((object)"A", native.RestoredHandles);
        Assert.Empty(native.AboveNormal);
    }

    // ----------------------------------------- scope: re-bump idempotence -----

    [Fact]
    public void ReapplyBeforeElevationIsNoOp()
    {
        var native = new FakeRendererPriorityNative();
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.ReapplyAfterKnownHide("attach-starting", BrowserPid); // Armed, not Elevated

        Assert.Empty(native.DiscoverCalls);
        Assert.Empty(native.AppliedHandles);
    }

    [Fact]
    public void DuplicateBeginForElevatedGenerationReBumpsExistingLeases()
    {
        var native = new FakeRendererPriorityNative();
        native.EnqueueDiscovery(Discovery(Lease(11, "A"), Lease(12, "B")));
        using var scope = new ApplicateModeToggleRendererPriorityScope(native);

        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);
        scope.BeginAfterFinalHide(1, BrowserPid); // re-entrant final hide -> re-bump

        // Each retained lease re-applied AboveNormal (2 applies each).
        Assert.Equal(2, native.AppliedHandles.Count(h => (string)h == "A"));
        Assert.Equal(2, native.AppliedHandles.Count(h => (string)h == "B"));
        Assert.Equal(2, native.AboveNormal.Count);

        scope.Close(1, "success");
        Assert.Empty(native.AboveNormal);
    }

    [Fact]
    public void NoOpScopeIsInert()
    {
        var scope = ApplicateModeToggleRendererPriorityScope.NoOp;
        // Every operation is a no-op; none throws.
        scope.Arm(1, ApplicateMode.Edit);
        scope.BeginAfterFinalHide(1, BrowserPid);
        scope.ReapplyAfterKnownHide("x", BrowserPid);
        scope.Close(1, "success");
        scope.CloseAny("dispose");
        scope.Dispose();
    }

    // -------------------------------------------------- static invariants -----

    [Fact]
    public void NewPriorityFilesIntroduceNoTimerPollOrWatchdog()
    {
        var scope = File.ReadAllText(SrcPath("ApplicateModeToggleRendererPriorityScope.cs"));
        var native = File.ReadAllText(SrcPath("WindowsWebViewRendererPriorityNative.cs"));
        foreach (var banned in new[]
                 {
                     "Timer", "Task.Delay", "Thread.Sleep", "Stopwatch",
                     "PeriodicTimer", "DispatcherTimer", "CreateTimer",
                 })
        {
            Assert.DoesNotContain(banned, scope, StringComparison.Ordinal);
            Assert.DoesNotContain(banned, native, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NewPriorityFilesIntroduceNoWmiOrCim()
    {
        var scope = File.ReadAllText(SrcPath("ApplicateModeToggleRendererPriorityScope.cs"));
        var native = File.ReadAllText(SrcPath("WindowsWebViewRendererPriorityNative.cs"));
        foreach (var banned in new[]
                 {
                     "System.Management", "ManagementObject", "ManagementClass",
                     "Win32_Process", "GetWmiObject", "CimInstance",
                 })
        {
            Assert.DoesNotContain(banned, scope, StringComparison.Ordinal);
            Assert.DoesNotContain(banned, native, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SetPriorityClassLivesOnlyInTheNativeAdapter()
    {
        var scope = File.ReadAllText(SrcPath("ApplicateModeToggleRendererPriorityScope.cs"));
        var native = File.ReadAllText(SrcPath("WindowsWebViewRendererPriorityNative.cs"));
        Assert.DoesNotContain("SetPriorityClass", scope, StringComparison.Ordinal);
        Assert.Contains("SetPriorityClass", native, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeRestoresToNormalNotCapturedPriorityClass()
    {
        var native = File.ReadAllText(SrcPath("WindowsWebViewRendererPriorityNative.cs"));
        // Restore sets the neutral NORMAL class Chromium assigns on show, never
        // the captured (possibly Idle) prior class.
        Assert.Contains("SetPriorityClass(handle, NORMAL_PRIORITY_CLASS)", native, StringComparison.Ordinal);
        Assert.DoesNotContain("SetPriorityClass(handle, lease.CapturedPriorityClass", native, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- helpers ---

    private static RendererPriorityLease Lease(int pid, string handle, int capturedPriorityClass = 0x40 /* IDLE */)
        => new(pid, capturedPriorityClass, handle);

    private static RendererPriorityDiscovery Discovery(params RendererPriorityLease[] leases)
        => RendererPriorityDiscovery.Success(leases);

    private sealed class FakeRendererPriorityNative : IApplicateRendererPriorityNative
    {
        private readonly Queue<RendererPriorityDiscovery> _discoveries = new();

        public RendererPriorityDiscovery? DefaultDiscovery { get; set; }

        public Func<RendererPriorityLease, bool>? ApplyPredicate { get; set; }

        public Func<RendererPriorityLease, RendererPriorityRestoreResult>? RestoreFunc { get; set; }

        public List<int> DiscoverCalls { get; } = [];

        public List<object> AppliedHandles { get; } = [];

        public List<object> RestoredHandles { get; } = [];

        public List<object> ClosedHandles { get; } = [];

        public HashSet<object> AboveNormal { get; } = [];

        public void EnqueueDiscovery(RendererPriorityDiscovery discovery) => _discoveries.Enqueue(discovery);

        public RendererPriorityDiscovery Discover(int browserProcessId)
        {
            DiscoverCalls.Add(browserProcessId);
            if (_discoveries.Count > 0)
            {
                return _discoveries.Dequeue();
            }

            return DefaultDiscovery ?? RendererPriorityDiscovery.NoMatch;
        }

        public bool TryApplyAboveNormal(RendererPriorityLease lease)
        {
            AppliedHandles.Add(lease.Handle);
            var ok = ApplyPredicate?.Invoke(lease) ?? true;
            if (ok)
            {
                AboveNormal.Add(lease.Handle);
            }

            return ok;
        }

        public RendererPriorityRestoreResult RestoreNormal(RendererPriorityLease lease)
        {
            RestoredHandles.Add(lease.Handle);
            var result = RestoreFunc?.Invoke(lease) ?? RendererPriorityRestoreResult.Restored;
            if (result.Outcome != RendererPriorityRestoreOutcome.Failed)
            {
                AboveNormal.Remove(lease.Handle);
            }

            return result;
        }

        public void CloseLease(RendererPriorityLease lease) => ClosedHandles.Add(lease.Handle);
    }
}

using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Tests for the pure slot-visibility state machine that backs
/// <see cref="ApplicateSharedWebViewHost"/>. Anchored to design D1 / D10:
/// state transitions, generation monotonicity, mode-toggle atomicity.
/// </summary>
public sealed class ApplicateSharedWebViewHostStateMachineTests
{
    private static readonly string HostSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Rendering",
        "ApplicateSharedWebViewHost.cs");

    [Fact]
    public void NewMachineStartsParked()
    {
        var sm = new ApplicateSharedWebViewHostStateMachine();

        Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Parked, sm.CurrentState);
        Assert.Equal(0, sm.CurrentGeneration);
        Assert.Null(sm.CurrentParent);
    }

    [Fact]
    public void SetWarmupParentTransitionsToParkedAndAdoptsPanel()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            sm.SetWarmupParent(warmup);

            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Parked, sm.CurrentState);
            Assert.Same(warmup, sm.CurrentParent);
        }, CancellationToken.None);
    }

    [Fact]
    public void AttachToTransitionsToSwitchingAndHidesTargetSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);

            sm.AttachTo(slot);

            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Switching, sm.CurrentState);
            Assert.Same(slot, sm.CurrentParent);
            Assert.False(slot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void AttachToKeepsTargetTransparentUntilDocumentRendered()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var viewer = new Panel { IsVisible = true, Opacity = 1.0 };
            var edit = new Panel { IsVisible = true, Opacity = 1.0 };
            sm.SetWarmupParent(warmup);

            sm.AttachTo(viewer);
            Assert.Equal(0.0, viewer.Opacity);
            Assert.True(sm.ApplyDocumentRendered(sm.RequestRender()));
            Assert.Equal(1.0, viewer.Opacity);

            sm.AttachTo(edit);

            Assert.Equal(0.0, edit.Opacity);
            Assert.True(sm.ApplyDocumentRendered(sm.RequestRender()));
            Assert.Equal(1.0, edit.Opacity);
        }, CancellationToken.None);
    }

    [Fact]
    public void RequestRenderBumpsGenerationAndHidesActiveSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);
            // Manually pretend the prior generation committed.
            sm.ApplyDocumentRendered(sm.RequestRender());

            slot.IsVisible = true;

            var gen = sm.RequestRender();

            Assert.True(gen > 1);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Switching, sm.CurrentState);
            Assert.False(slot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void ApplyDocumentRenderedCommitsAndShowsSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);
            var gen = sm.RequestRender();

            var committed = sm.ApplyDocumentRendered(gen);

            Assert.True(committed);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Committed, sm.CurrentState);
            Assert.True(slot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void StaleDocumentRenderedIsDropped()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);
            var firstGen = sm.RequestRender();
            var secondGen = sm.RequestRender();

            Assert.NotEqual(firstGen, secondGen);

            // Stale event from earlier generation must be silently dropped —
            // state stays Switching, slot stays hidden (Invariant I-4).
            var committed = sm.ApplyDocumentRendered(firstGen);

            Assert.False(committed);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Switching, sm.CurrentState);
            Assert.False(slot.IsVisible);

            // The later generation still commits cleanly.
            Assert.True(sm.ApplyDocumentRendered(secondGen));
            Assert.True(slot.IsVisible);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Committed, sm.CurrentState);
        }, CancellationToken.None);
    }

    [Fact]
    public void ModeToggleNeverShowsBothSlotsSimultaneously()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var viewer = new Panel { IsVisible = true };
            var edit = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);

            sm.AttachTo(viewer);
            var gen1 = sm.RequestRender();
            sm.ApplyDocumentRendered(gen1);
            Assert.True(viewer.IsVisible);

            // Mode toggle: re-attach to the edit slot. Invariant I-6: no
            // moment exists when both slots are IsVisible=true.
            sm.AttachTo(edit);

            Assert.False(edit.IsVisible);
            // Viewer slot has had its visibility restored — but only as a
            // layout-host invariant, NOT because it currently displays the
            // WebView. The state machine itself never "shows both".
            // The actual WebView lives in edit now.
            Assert.Same(edit, sm.CurrentParent);
        }, CancellationToken.None);
    }

    [Fact]
    public void GenerationMonotonicallyIncreases()
    {
        var sm = new ApplicateSharedWebViewHostStateMachine();
        var firstGen = sm.RequestRender();
        var secondGen = sm.RequestRender();
        var thirdGen = sm.RequestRender();

        Assert.True(secondGen > firstGen);
        Assert.True(thirdGen > secondGen);
    }

    [Fact]
    public void SharedHostInterfaceExposesTransactionReadinessSurface()
    {
        var overload = typeof(IApplicateSharedWebViewHost)
            .GetMethods()
            .SingleOrDefault(method =>
                method.Name == nameof(IApplicateSharedWebViewHost.RequestRender)
                && method.GetParameters().Length == 3);

        Assert.NotNull(overload);
        Assert.Equal(typeof(long), overload.GetParameters()[2].ParameterType);
        Assert.NotNull(typeof(IApplicateModeTransactionHost).GetEvent("MinimapSettled"));
        Assert.NotNull(typeof(IApplicateModeTransactionHost).GetEvent("CommitCompleted"));
        Assert.NotNull(typeof(IApplicateModeTransactionHost).GetEvent("RendererSettled"));
    }

    [Fact]
    public void SharedHostInterfaceExposesTransactionalNativeRevealSurface()
    {
        var method = typeof(IApplicateModeTransactionHost)
            .GetMethod("RevealNativeWebViewForCommittedTransaction");

        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method.ReturnType);
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(long), parameter.ParameterType);

        var suppressMethod = typeof(IApplicateModeTransactionHost)
            .GetMethod("SuppressNativeRendererForModeSwitch");
        Assert.NotNull(suppressMethod);
        Assert.Equal(typeof(void), suppressMethod.ReturnType);
        var suppressParameter = Assert.Single(suppressMethod.GetParameters());
        Assert.Equal(typeof(ApplicateMode), suppressParameter.ParameterType);

        var restoreMethod = typeof(IApplicateModeTransactionHost)
            .GetMethod("RestoreNativeRendererAfterModeSwitchSuppression");
        Assert.NotNull(restoreMethod);
        Assert.Equal(typeof(void), restoreMethod.ReturnType);
        var restoreParameter = Assert.Single(restoreMethod.GetParameters());
        Assert.Equal(typeof(ApplicateMode), restoreParameter.ParameterType);
    }

    [Fact]
    public void SharedHostInterfaceExposesInactivePrimeRenderSurface()
    {
        var method = typeof(IApplicateSharedWebViewHost)
            .GetMethod(nameof(IApplicateSharedWebViewHost.RequestInactivePrimeRender));

        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(MarkdownSource), parameters[0].ParameterType);
        Assert.Equal(typeof(ApplicateWebRenderRequest), parameters[1].ParameterType);
    }

    [Fact]
    public void SharedHostInactivePrimeRenderKeepsColdParentVisible()
    {
        var source = File.ReadAllText(HostSourcePath);

        Assert.Contains(
            "keepColdParentVisibleForInactivePrime: true",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "&& !keepColdParentVisibleForInactivePrime",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionalCommitWaitsForBridgeNativeReveal()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true, Opacity = 0.0 };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot, transactionGeneration: 42);

            var renderGen = sm.RequestRender(
                transactionGeneration: 42,
                mode: ApplicateMode.Viewer,
                fastPathCommit: true);

            Assert.NotEqual(0, renderGen);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Committed, sm.CurrentState);
            Assert.False(sm.NativeWebViewVisible);
            Assert.False(sm.RevealNativeWebViewForCommittedTransaction(41));
            Assert.False(sm.NativeWebViewVisible);
            Assert.True(sm.RevealNativeWebViewForCommittedTransaction(42));
            Assert.True(sm.NativeWebViewVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void TransactionalAttachAndRequestRenderKeepBridgeOwnedSlotVisible()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true, Opacity = 1.0 };
            sm.SetWarmupParent(warmup);

            sm.AttachTo(slot, transactionGeneration: 42);

            Assert.True(slot.IsVisible);
            Assert.Equal(1.0, slot.Opacity);
            Assert.False(sm.NativeWebViewVisible);

            var renderGen = sm.RequestRender(
                transactionGeneration: 42,
                mode: ApplicateMode.Edit);

            Assert.True(renderGen > 0);
            Assert.True(slot.IsVisible);
            Assert.Equal(1.0, slot.Opacity);
            Assert.False(sm.NativeWebViewVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void TransactionGenerationTagsCommitCompletedEvent()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            ApplicateCommitCompletedEventArgs? completed = null;
            sm.CommitCompleted += (_, e) => completed = e;
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);

            var renderGen = sm.RequestRender(
                transactionGeneration: 42,
                mode: ApplicateMode.Viewer);

            Assert.True(sm.ApplyDocumentRendered(renderGen));
            Assert.NotNull(completed);
            Assert.Equal(42, completed.TransactionGeneration);
            Assert.Equal(ApplicateMode.Viewer, completed.Mode);
            Assert.Equal(slot.Bounds, completed.Bounds);
        }, CancellationToken.None);
    }

    [Fact]
    public void StaleRenderGenerationDoesNotRaiseCommitCompletedEvent()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            var commitCount = 0;
            ApplicateCommitCompletedEventArgs? completed = null;
            sm.CommitCompleted += (_, e) =>
            {
                commitCount++;
                completed = e;
            };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);

            var firstRenderGen = sm.RequestRender(
                transactionGeneration: 10,
                mode: ApplicateMode.Viewer);
            var secondRenderGen = sm.RequestRender(
                transactionGeneration: 20,
                mode: ApplicateMode.Viewer);

            Assert.False(sm.ApplyDocumentRendered(firstRenderGen));
            Assert.Null(completed);

            Assert.True(sm.ApplyDocumentRendered(secondRenderGen));
            Assert.Equal(1, commitCount);
            Assert.NotNull(completed);
            Assert.Equal(20, completed.TransactionGeneration);
        }, CancellationToken.None);
    }

    [Fact]
    public void FastPathRequestRenderRaisesCommitCompletedSynchronously()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            ApplicateCommitCompletedEventArgs? completed = null;
            sm.CommitCompleted += (_, e) => completed = e;
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);

            var renderGen = sm.RequestRender(
                transactionGeneration: 77,
                mode: ApplicateMode.Viewer,
                fastPathCommit: true);

            Assert.NotEqual(0, renderGen);
            Assert.NotNull(completed);
            Assert.Equal(77, completed.TransactionGeneration);
            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Committed, sm.CurrentState);
        }, CancellationToken.None);
    }

    [Fact]
    public void EditTransactionRaisesMinimapSettledNotApplicableSynchronously()
    {
        var sm = new ApplicateSharedWebViewHostStateMachine();
        ApplicateMinimapSettledEventArgs? settled = null;
        sm.MinimapSettled += (_, e) => settled = e;

        sm.RequestRender(
            transactionGeneration: 33,
            mode: ApplicateMode.Edit,
            minimapApplicable: false);

        Assert.NotNull(settled);
        Assert.Equal(33, settled.TransactionGeneration);
        Assert.False(settled.IsApplicable);
        Assert.Null(settled.State);
    }

    [Fact]
    public void ViewerTransactionRaisesMinimapSettledOnceAndDropsStaleGeneration()
    {
        var sm = new ApplicateSharedWebViewHostStateMachine();
        var events = new List<ApplicateMinimapSettledEventArgs>();
        sm.MinimapSettled += (_, e) => events.Add(e);

        sm.RequestRender(
            transactionGeneration: 44,
            mode: ApplicateMode.Viewer,
            minimapApplicable: true);

        Assert.False(sm.ApplyMinimapSettled(
            transactionGeneration: 43,
            state: new ApplicateWebMinimapStateEventArgs(visible: false, reservedWidth: 0)));
        Assert.True(sm.ApplyMinimapSettled(
            transactionGeneration: 44,
            state: new ApplicateWebMinimapStateEventArgs(visible: true, reservedWidth: 168)));
        Assert.False(sm.ApplyMinimapSettled(
            transactionGeneration: 44,
            state: new ApplicateWebMinimapStateEventArgs(visible: false, reservedWidth: 0)));

        var settled = Assert.Single(events);
        Assert.Equal(44, settled.TransactionGeneration);
        Assert.True(settled.IsApplicable);
        Assert.NotNull(settled.State);
        Assert.True(settled.State.Visible);
        Assert.Equal(168, settled.State.ReservedWidth);
    }

    [Fact]
    public void TransactionRaisesRendererSettledOnceAndDropsStaleGeneration()
    {
        var sm = new ApplicateSharedWebViewHostStateMachine();
        var events = new List<ApplicateRendererSettledEventArgs>();
        sm.RendererSettled += (_, e) => events.Add(e);

        sm.RequestRender(
            transactionGeneration: 44,
            mode: ApplicateMode.Edit);

        Assert.False(sm.ApplyRendererSettled(43));
        Assert.True(sm.ApplyRendererSettled(44));
        Assert.False(sm.ApplyRendererSettled(44));

        var settled = Assert.Single(events);
        Assert.Equal(44, settled.TransactionGeneration);
    }

    [Fact]
    public void ReturnToWarmupClearsConsumerSlotAndRestoresVisibility()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sm = new ApplicateSharedWebViewHostStateMachine();
            var warmup = new Panel();
            var slot = new Panel { IsVisible = true };
            sm.SetWarmupParent(warmup);
            sm.AttachTo(slot);
            var gen = sm.RequestRender();
            sm.ApplyDocumentRendered(gen);

            sm.ReturnToWarmup();

            Assert.Equal(ApplicateSharedWebViewHostStateMachine.State.Parked, sm.CurrentState);
            Assert.Same(warmup, sm.CurrentParent);
            Assert.True(slot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void SharedHostOwnsRevealFadeAfterRendererSettle()
    {
        var source = File.ReadAllText(HostSourcePath);
        var completeReveal = ExtractMethodBody(
            source,
            source.IndexOf("private void CompleteReveal()", StringComparison.Ordinal));

        Assert.Contains("PrepareTargetForReveal", source, StringComparison.Ordinal);
        Assert.Contains("RevealCurrentParent", source, StringComparison.Ordinal);
        Assert.Contains("ApplicateMotion.ModeSwitchDuration", source, StringComparison.Ordinal);
        Assert.Contains("CompleteNativeWebViewHiddenPaint", completeReveal, StringComparison.Ordinal);
        Assert.True(
            completeReveal.IndexOf("RevealCurrentParent", StringComparison.Ordinal)
            > completeReveal.IndexOf("View.CompleteNativeWebViewHiddenPaint", StringComparison.Ordinal));
    }

    [Fact]
    public void SharedHostFadesRendererDocumentInsideNativeWebView()
    {
        var source = File.ReadAllText(HostSourcePath);
        var completeReveal = ExtractMethodBody(
            source,
            source.IndexOf("private void CompleteReveal()", StringComparison.Ordinal));

        Assert.Contains("PrepareNativeRendererForReveal", source, StringComparison.Ordinal);
        Assert.Contains("PrepareNativeWebViewForHiddenPaint", source, StringComparison.Ordinal);
        Assert.Contains("CompleteNativeWebViewHiddenPaint", source, StringComparison.Ordinal);
        Assert.Contains("RevealNativeRenderer", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("View.PrepareNativeRendererForReveal", StringComparison.Ordinal)
            < source.IndexOf("View.PrepareNativeWebViewForHiddenPaint", StringComparison.Ordinal));
        Assert.True(
            source.IndexOf("View.PrepareNativeWebViewForHiddenPaint", StringComparison.Ordinal)
            < source.IndexOf("BeginRevealAfterSettle", StringComparison.Ordinal));
        Assert.True(
            completeReveal.IndexOf("View.RevealNativeRenderer", StringComparison.Ordinal)
            > completeReveal.IndexOf("View.CompleteNativeWebViewHiddenPaint", StringComparison.Ordinal));
    }

    [Fact]
    public void TransactionRevealUsesConfiguredModeSwitchDuration()
    {
        var source = File.ReadAllText(HostSourcePath);
        var commit = ExtractMethodBody(
            source,
            source.IndexOf("private void Commit()", StringComparison.Ordinal));
        var transactionalReveal = ExtractMethodBody(
            source,
            source.IndexOf("public bool RevealNativeWebViewForCommittedTransaction", StringComparison.Ordinal));
        var transactionalCommit = commit[
            commit.IndexOf("if (transactionalCommit)", StringComparison.Ordinal)..
            commit.IndexOf("else if (armRevealGate)", StringComparison.Ordinal)];

        Assert.Contains("_pendingTransactionRevealDuration = modeSwitchDuration", commit, StringComparison.Ordinal);
        Assert.Contains("View.PrepareNativeRendererForReveal(modeSwitchDuration)", transactionalCommit, StringComparison.Ordinal);
        Assert.DoesNotContain("View.PrepareNativeWebViewForHiddenPaint();", transactionalCommit, StringComparison.Ordinal);
        Assert.True(
            transactionalCommit.IndexOf("View.PrepareNativeRendererForReveal(modeSwitchDuration)", StringComparison.Ordinal)
            < commit.IndexOf("View.RequestModeToggleSettleProbe(", StringComparison.Ordinal));
        Assert.Contains("var duration = _pendingTransactionRevealDuration", transactionalReveal, StringComparison.Ordinal);
        Assert.Contains("View.CompleteNativeWebViewHiddenPaint();", transactionalReveal, StringComparison.Ordinal);
        Assert.DoesNotContain("View.SetNativeWebViewVisibility(true)", transactionalReveal, StringComparison.Ordinal);
        Assert.DoesNotContain("View.RevealNativeRenderer(TimeSpan.Zero)", transactionalReveal, StringComparison.Ordinal);
        Assert.Contains("View.RevealNativeRenderer(duration)", transactionalReveal, StringComparison.Ordinal);
    }

    [Fact]
    public void RevealGateRehidesNativeWindowAfterLayoutBeforeOffscreenPrepaint()
    {
        var source = File.ReadAllText(HostSourcePath);
        var commit = ExtractMethodBody(
            source,
            source.IndexOf("private void Commit()", StringComparison.Ordinal));

        Assert.Contains("View.SetNativeWebViewVisibility(false);", commit, StringComparison.Ordinal);
        Assert.True(
            commit.IndexOf("_currentParent.UpdateLayout();", StringComparison.Ordinal)
            < commit.IndexOf("View.SetNativeWebViewVisibility(false);", StringComparison.Ordinal));
        Assert.True(
            commit.IndexOf("View.SetNativeWebViewVisibility(false);", StringComparison.Ordinal)
            < commit.IndexOf("View.PrepareNativeWebViewForHiddenPaint();", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(42, true)]
    public void TransactionalFrameSettleSkipDependsOnlyOnTransactionGeneration(
        long transactionGeneration,
        bool expected)
    {
        var actual = ApplicateSharedWebViewHost.ShouldSkipRendererFrameSettleForTransaction(
            transactionGeneration);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TransactionOutgoingSuppressionHidesNativeWindowWithoutOffscreenParking()
    {
        var source = File.ReadAllText(HostSourcePath);
        var suppress = ExtractMethodBody(
            source,
            source.IndexOf("public void SuppressNativeRendererForModeSwitch(ApplicateMode displayedMode)", StringComparison.Ordinal));

        Assert.Contains("View.ResetHostShortcutsForModeSwitch();", suppress, StringComparison.Ordinal);
        Assert.Contains("View.SetNativeWebViewVisibility(false);", suppress, StringComparison.Ordinal);
        Assert.True(
            suppress.IndexOf("View.ResetHostShortcutsForModeSwitch();", StringComparison.Ordinal)
            < suppress.IndexOf("View.SetNativeWebViewVisibility(false);", StringComparison.Ordinal));
        Assert.DoesNotContain("View.ParkNativeWebViewForReparent();", suppress, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionOutgoingRestoreShowsNativeWindowAfterLayoutBarrier()
    {
        var source = File.ReadAllText(HostSourcePath);
        var restore = ExtractMethodBody(
            source,
            source.IndexOf("public void RestoreNativeRendererAfterModeSwitchSuppression(ApplicateMode displayedMode)", StringComparison.Ordinal));

        Assert.Contains("View.SetNativeWebViewVisibility(true);", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("View.ParkNativeWebViewForReparent();", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachToParksNativeWindowOffscreenBeforeReparent()
    {
        var source = File.ReadAllText(HostSourcePath);
        var attachTo = ExtractMethodBody(
            source,
            source.IndexOf("public void AttachTo(Panel target, ApplicateWebMountIntent intent)", StringComparison.Ordinal));

        Assert.Contains("View.ParkNativeWebViewForReparent();", attachTo, StringComparison.Ordinal);
        Assert.True(
            attachTo.IndexOf("View.ParkNativeWebViewForReparent();", StringComparison.Ordinal)
            < attachTo.IndexOf("using (View.BeginIntentionalReparent())", StringComparison.Ordinal));
    }

    [Fact]
    public void AttachToRehidesNativeWindowAfterTargetVisibilityReturns()
    {
        var source = File.ReadAllText(HostSourcePath);
        var attachTo = ExtractMethodBody(
            source,
            source.IndexOf("public void AttachTo(Panel target, ApplicateWebMountIntent intent)", StringComparison.Ordinal));
        var visibilityRestore = attachTo.IndexOf("target.IsVisible = _hasEverCommitted;", StringComparison.Ordinal);
        var postRestoreHide = attachTo.IndexOf("View.SetNativeWebViewVisibility(false);", visibilityRestore, StringComparison.Ordinal);

        Assert.True(visibilityRestore >= 0);
        Assert.True(postRestoreHide > visibilityRestore);
    }

    [Fact]
    public void RevealGateFallbackStaysBehindRendererPostChromeSettleBudget()
    {
        var source = File.ReadAllText(HostSourcePath);

        Assert.Contains("TimeSpan.FromMilliseconds(500)", source, StringComparison.Ordinal);
        Assert.Contains("post-chrome", source, StringComparison.Ordinal);
    }

    private static string ExtractMethodBody(string source, int methodStart)
    {
        Assert.True(methodStart >= 0);

        var bodyStart = source.IndexOf('{', methodStart);
        Assert.True(bodyStart >= 0);

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException("Could not find method body.");
    }
}

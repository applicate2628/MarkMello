using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Headless;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Applicate.Tests.Fakes;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateSiblingMountTests
{
    private static readonly string BridgeSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "ApplicateSiblingMountBridge.cs");
    private static readonly string CoverWindowSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Rendering",
        "ApplicateModeRevealCoverWindow.cs");
    private static readonly string RendererCssSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "RendererWeb",
        "assets",
        "renderer.css");

    private static ApplicateSiblingMountBridge MakeBridge(
        FakeMainWindowVm vm,
        ContentControl viewer,
        Panel edit,
        Control editContent,
        FakeModeRevealSignal? modeRevealSignal = null,
        FakeTransactionHost? transactionHost = null) =>
        new(vm, viewer, edit, editContent,
            () => vm.IsViewer, () => vm.IsEditMode,
            () => vm.EditorSession, () => vm.Document,
            () => vm.ReadingPreferences,
            viewerContent: vm,
            modeRevealSignal: modeRevealSignal,
            transactionHost: transactionHost);

    [Fact]
    public void OpeningDocumentInReaderModeShowsViewerSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var vm = new FakeMainWindowVm();
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);

            vm.Document = new object();
            vm.IsViewer = true;

            Assert.True(viewerSlot.IsVisible);
            Assert.True(viewerSlot.IsEnabled);
            Assert.True(viewerSlot.IsHitTestVisible);
            Assert.True(viewerSlot.IsTabStop);
            Assert.False(editSlot.IsVisible);
            Assert.False(editSlot.IsHitTestVisible);
            Assert.Null(editContent.DataContext);
        }, CancellationToken.None);
    }

    [Fact]
    public void BridgeOwnsModeSwitchFadeMechanismForEverySurface()
    {
        var source = File.ReadAllText(BridgeSourcePath);

        Assert.Contains("InstallModeSwitchFade(_viewerSlot, preferences);", source, StringComparison.Ordinal);
        Assert.Contains("InstallModeSwitchFade(_editSlot, preferences);", source, StringComparison.Ordinal);
        Assert.Contains("InstallModeSwitchFade(_editContent, preferences);", source, StringComparison.Ordinal);
        Assert.Contains("new DoubleTransition", source, StringComparison.Ordinal);
        Assert.Contains("ApplicateMotion.ModeSwitchDuration", source, StringComparison.Ordinal);
        Assert.Contains("ApplicateMotion.Easing", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeModeRevealCoverUsesOpaqueThemeShieldWithoutSnapshot()
    {
        var bridge = File.ReadAllText(BridgeSourcePath);
        var cover = File.ReadAllText(CoverWindowSourcePath);

        Assert.DoesNotContain("ApplicateModeTransitionCapture", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCapture", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge-cover-capture", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("Image", cover, StringComparison.Ordinal);
        Assert.DoesNotContain("Bitmap", cover, StringComparison.Ordinal);
        Assert.DoesNotContain("Stretch", cover, StringComparison.Ordinal);
        Assert.Contains("new Border", cover, StringComparison.Ordinal);
        Assert.Contains("MmBackgroundBrush", cover, StringComparison.Ordinal);
        Assert.Contains("ResolveHostPixelSize", cover, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.SetWindowPos", cover, StringComparison.Ordinal);
        Assert.Contains("bridge-cover-window-shown", cover, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeModeRevealCoverFallbackPaletteMatchesRendererDocumentBackground()
    {
        var cover = File.ReadAllText(CoverWindowSourcePath);
        var rendererCss = File.ReadAllText(RendererCssSourcePath);

        Assert.Contains("Color.FromRgb(0xFC, 0xFA, 0xF6)", cover, StringComparison.Ordinal);
        Assert.Contains("Color.FromRgb(0x14, 0x11, 0x0E)", cover, StringComparison.Ordinal);
        Assert.Contains("--mm-document-background: #fcfaf6;", rendererCss, StringComparison.Ordinal);
        Assert.Contains("--mm-document-background: #14110e;", rendererCss, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeInstallsOpacityTransitionsOnBothModeSlotsAndEditContent()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var vm = new FakeMainWindowVm();
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);

            AssertOpacityTransition(viewerSlot);
            AssertOpacityTransition(editSlot);
            AssertOpacityTransition(editContent);
        }, CancellationToken.None);
    }

    [Fact]
    public void ModeSwitchAppliesFadeStateInBothDirections()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sessionRef = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);

            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.False(editSlot.IsVisible);
            Assert.Equal(0.0, editSlot.Opacity);

            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;

            Assert.False(viewerSlot.IsVisible);
            Assert.Equal(0.0, viewerSlot.Opacity);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(1.0, editSlot.Opacity);
            Assert.True(editSlot.IsHitTestVisible);

            vm.IsEditMode = false;

            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.False(editSlot.IsVisible);
            Assert.Equal(0.0, editSlot.Opacity);
            Assert.False(editSlot.IsHitTestVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void ModeSwitchUsesOnlyViewerAsOutgoingCoverUntilSharedHostRevealCompletes()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sessionRef = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            var revealSignal = new FakeModeRevealSignal();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent, revealSignal);

            Assert.True(viewerSlot.IsVisible);
            Assert.False(editSlot.IsVisible);

            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;

            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.False(viewerSlot.IsHitTestVisible);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(0.0, editSlot.Opacity);
            Assert.False(editSlot.IsHitTestVisible);

            revealSignal.RaiseRevealCompleted();

            Assert.False(viewerSlot.IsVisible);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(1.0, editSlot.Opacity);
            Assert.True(editSlot.IsHitTestVisible);

            vm.IsEditMode = false;

            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.False(viewerSlot.IsHitTestVisible);
            Assert.False(editSlot.IsVisible);
            Assert.Equal(0.0, editSlot.Opacity);
            Assert.False(editSlot.IsHitTestVisible);
            Assert.Equal(0.0, editContent.Opacity);

            revealSignal.RaiseRevealCompleted();

            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.True(viewerSlot.IsHitTestVisible);
            Assert.False(editSlot.IsVisible);
            Assert.Equal(0.0, editContent.Opacity);
        }, CancellationToken.None);
    }

    [Fact]
    public void ModeSwitchSuppressesNativeRendererBeforeSlotVisibilityChanges()
    {
        var source = File.ReadAllText(BridgeSourcePath);
        var reconcile = ExtractMethodBody(
            source,
            source.IndexOf("private void Reconcile()", StringComparison.Ordinal));

        Assert.True(
            reconcile.IndexOf("_modeRevealSignal.SuppressNativeRendererForModeSwitch();", StringComparison.Ordinal)
            < reconcile.IndexOf("ApplySlotState(", StringComparison.Ordinal));
    }

    [Fact]
    public void ModeSwitchCoverUsesSeparateWindowForNativeWebViewAirspace()
    {
        var bridge = File.ReadAllText(BridgeSourcePath);
        var coverWindow = File.ReadAllText(CoverWindowSourcePath);

        Assert.Contains("ApplicateModeRevealCoverWindow", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("_modeRevealCoverPopup", bridge, StringComparison.Ordinal);
        Assert.Contains("new Window", coverWindow, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar = false", coverWindow, StringComparison.Ordinal);
        Assert.Contains("ShowActivated = false", coverWindow, StringComparison.Ordinal);
        Assert.Contains("WindowDecorations = WindowDecorations.None", coverWindow, StringComparison.Ordinal);
        Assert.Contains("Topmost = true", coverWindow, StringComparison.Ordinal);
        Assert.Contains(".Show(owner)", coverWindow, StringComparison.Ordinal);
        Assert.Contains("_owner.PositionChanged += OnOwnerPositionChanged", coverWindow, StringComparison.Ordinal);
        Assert.Contains("bridge-cover-window-repositioned", coverWindow, StringComparison.Ordinal);
        Assert.Contains("bridge-cover-cancelled", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionCommitDisarmsCoverBeforeNextModeSwitch()
    {
        var source = File.ReadAllText(BridgeSourcePath);
        var commit = ExtractMethodBody(
            source,
            source.IndexOf("private void CommitQueuedModeTransaction()", StringComparison.Ordinal));

        var disarm = commit.IndexOf("_modeRevealCoverArmed = false;", StringComparison.Ordinal);
        var hide = commit.IndexOf("HideModeRevealCover();", StringComparison.Ordinal);

        Assert.True(disarm >= 0);
        Assert.True(hide > disarm);
    }

    [Fact]
    public void TransactionRevealRejectionRestoresShieldAndHidesCover()
    {
        var source = File.ReadAllText(BridgeSourcePath);
        var commit = ExtractMethodBody(
            source,
            source.IndexOf("private void CommitQueuedModeTransaction()", StringComparison.Ordinal));
        var rejectedBranch = commit[
            commit.IndexOf("if (!_transactionHost.RevealNativeWebViewForCommittedTransaction(generation))", StringComparison.Ordinal)..];

        var restore = rejectedBranch.IndexOf("ApplyTransactionalSlotStates();", StringComparison.Ordinal);
        var disarm = rejectedBranch.IndexOf("_modeRevealCoverArmed = false;", StringComparison.Ordinal);
        var hide = rejectedBranch.IndexOf("HideModeRevealCover();", StringComparison.Ordinal);
        var rejectedLog = rejectedBranch.IndexOf("bridge-transaction-native-reveal-rejected", StringComparison.Ordinal);

        Assert.True(restore >= 0);
        Assert.True(disarm > restore);
        Assert.True(hide > disarm);
        Assert.True(rejectedLog > hide);
    }

    [Fact]
    public void TransactionCancellationDisarmsCoverBeforeNextModeSwitch()
    {
        var source = File.ReadAllText(BridgeSourcePath);
        var reconcile = ExtractMethodBody(
            source,
            source.IndexOf("private bool TryReconcileTransactionalModeSwitch(", StringComparison.Ordinal));
        var cancelBranch = reconcile[
            reconcile.IndexOf("else", reconcile.IndexOf("if (generation > 0)", StringComparison.Ordinal), StringComparison.Ordinal)..];

        var disarm = cancelBranch.IndexOf("_modeRevealCoverArmed = false;", StringComparison.Ordinal);
        var hide = cancelBranch.IndexOf("HideModeRevealCover();", StringComparison.Ordinal);

        Assert.True(disarm >= 0);
        Assert.True(hide > disarm);
    }

    [Fact]
    public void TransactionRouterDoesNotParkNonTargetHostAfterReveal()
    {
        var viewerHost = new FakeTransactionHost(() => (ViewerOpacity: 1.0, EditOpacity: 0.0));
        var editHost = new FakeTransactionHost(() => (ViewerOpacity: 0.0, EditOpacity: 1.0));
        using var router = new ApplicateModeTransactionHostRouter(viewerHost, editHost);

        editHost.RaiseCommitCompleted(57, ApplicateMode.Edit);

        Assert.True(router.RevealNativeWebViewForCommittedTransaction(57));
        Assert.Equal(0, viewerHost.SuppressNativeRendererCallCount);
        Assert.Equal(0, editHost.SuppressNativeRendererCallCount);
        Assert.Single(editHost.RevealedGenerations);
    }

    [Fact]
    public void TransactionRouterSuppressesOnlyDisplayedModeHostBeforeLayoutMutation()
    {
        var viewerHost = new FakeTransactionHost(() => (ViewerOpacity: 1.0, EditOpacity: 0.0));
        var editHost = new FakeTransactionHost(() => (ViewerOpacity: 0.0, EditOpacity: 1.0));
        using var router = new ApplicateModeTransactionHostRouter(viewerHost, editHost);

        router.SuppressNativeRendererForModeSwitch(ApplicateMode.Viewer);

        Assert.Equal(1, viewerHost.SuppressNativeRendererCallCount);
        Assert.Equal(0, editHost.SuppressNativeRendererCallCount);

        router.SuppressNativeRendererForModeSwitch(ApplicateMode.Edit);

        Assert.Equal(1, viewerHost.SuppressNativeRendererCallCount);
        Assert.Equal(1, editHost.SuppressNativeRendererCallCount);
        Assert.Equal(new[] { ApplicateMode.Viewer }, viewerHost.SuppressedModes);
        Assert.Equal(new[] { ApplicateMode.Edit }, editHost.SuppressedModes);

        router.RestoreNativeRendererAfterModeSwitchSuppression(ApplicateMode.Viewer);

        Assert.Equal(new[] { ApplicateMode.Viewer }, viewerHost.RestoredModes);
        Assert.Empty(editHost.RestoredModes);
    }

    [Fact]
    public void TransactionalModeSwitchSuppressesOutgoingNativeRendererBeforeSlotVisibilityChanges()
    {
        var source = File.ReadAllText(BridgeSourcePath);
        var reconcile = ExtractMethodBody(
            source,
            source.IndexOf("private bool TryReconcileTransactionalModeSwitch(", StringComparison.Ordinal));

        var suppress = reconcile.IndexOf(
            "SuppressOutgoingNativeRendererForActiveTransaction(",
            StringComparison.Ordinal);
        var applySlots = reconcile.IndexOf("ApplyTransactionalSlotStates();", StringComparison.Ordinal);
        var restore = reconcile.IndexOf(
            "RestoreOutgoingNativeRendererAfterTransactionalLayout(",
            StringComparison.Ordinal);

        Assert.True(suppress >= 0);
        Assert.True(applySlots > suppress);
        Assert.True(restore > applySlots);
        Assert.Contains(
            "_transactionHost.SuppressNativeRendererForModeSwitch(outgoingMode);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_transactionHost.RestoreNativeRendererAfterModeSwitchSuppression(outgoingMode);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ModeSwitchRequestsNativeRendererSuppressionInBothDirections()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sessionRef = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            var revealSignal = new FakeModeRevealSignal();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent, revealSignal);

            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;
            Assert.Equal(1, revealSignal.SuppressNativeRendererCallCount);

            revealSignal.RaiseRevealCompleted();
            vm.IsEditMode = false;
            Assert.Equal(2, revealSignal.SuppressNativeRendererCallCount);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ModeSwitchCoverFallsBackWhenSharedHostRevealDoesNotArrive()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(async () =>
        {
            var sessionRef = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            var revealSignal = new FakeModeRevealSignal();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent, revealSignal);

            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;

            Assert.True(viewerSlot.IsVisible);
            Assert.False(viewerSlot.IsHitTestVisible);
            Assert.True(editSlot.IsVisible);
            Assert.False(editSlot.IsHitTestVisible);

            await Task.Delay(750);

            Assert.False(viewerSlot.IsVisible);
            Assert.True(editSlot.IsVisible);
            Assert.True(editSlot.IsHitTestVisible);
        }, CancellationToken.None);
    }

    private sealed class FakeModeRevealSignal : IApplicateModeRevealSignal
    {
        public int SuppressNativeRendererCallCount { get; private set; }

        public event EventHandler? RevealCompleted;

        public void SuppressNativeRendererForModeSwitch() => SuppressNativeRendererCallCount++;

        public void RaiseRevealCompleted() => RevealCompleted?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeTransactionHost(
        Func<(double ViewerOpacity, double EditOpacity)> slotSnapshot) : IApplicateSharedWebViewHost, IApplicateModeRevealSignal
    {
        public readonly record struct RevealSnapshot(
            long Generation,
            double ViewerOpacity,
            double EditOpacity);

        public List<RevealSnapshot> RevealedGenerations { get; } = [];

        public List<ApplicateMode> SuppressedModes { get; } = [];

        public List<ApplicateMode> RestoredModes { get; } = [];

        public bool RejectReveals { get; set; }

        public int SuppressNativeRendererCallCount { get; private set; }

        public ApplicateWebMarkdownDocumentView View => throw new NotSupportedException();

        public event EventHandler? RevealCompleted;

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

        public event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled;

        public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

        public event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled;

        public void SetWarmupParent(Panel parent)
        {
        }

        public void AttachTo(Panel target, ApplicateWebMountIntent intent)
        {
        }

        public bool IsAttachedTo(Panel target) => false;

        public void ReturnToWarmup()
        {
        }

        public void RequestRender(MarkdownSource? source, ApplicateWebRenderRequest request)
        {
        }

        public void RequestRender(
            MarkdownSource? source,
            ApplicateWebRenderRequest request,
            long transactionGeneration)
        {
        }

        public void RequestInactivePrimeRender(MarkdownSource? source, ApplicateWebRenderRequest request)
        {
        }

        public void RetryRender()
        {
        }

        public void SuppressNativeRendererForModeSwitch() => SuppressNativeRendererCallCount++;

        public void SuppressNativeRendererForModeSwitch(ApplicateMode displayedMode)
        {
            SuppressedModes.Add(displayedMode);
            SuppressNativeRendererForModeSwitch();
        }

        public void RestoreNativeRendererAfterModeSwitchSuppression(ApplicateMode displayedMode)
            => RestoredModes.Add(displayedMode);

        public bool RevealNativeWebViewForCommittedTransaction(long transactionGeneration)
        {
            var snapshot = slotSnapshot();
            RevealedGenerations.Add(
                new RevealSnapshot(
                    transactionGeneration,
                    snapshot.ViewerOpacity,
                    snapshot.EditOpacity));
            return !RejectReveals;
        }

        public Task PreWarmShellAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WaitForShellReadyAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void RaiseCommitCompleted(long generation, ApplicateMode mode)
            => CommitCompleted?.Invoke(
                this,
                new ApplicateCommitCompletedEventArgs(
                    mode,
                    new Rect(0, 0, 800, 600),
                    generation));

        public void RaiseMinimapSettledNotApplicable(long generation)
            => MinimapSettled?.Invoke(
                this,
                ApplicateMinimapSettledEventArgs.NotApplicable(generation));

        public void RaiseMinimapSettled(long generation, bool visible = true, double reservedWidth = 168)
            => MinimapSettled?.Invoke(
                this,
                new ApplicateMinimapSettledEventArgs(
                    generation,
                    new ApplicateWebMinimapStateEventArgs(visible, reservedWidth)));

        public void RaiseRendererSettled(long generation)
            => RendererSettled?.Invoke(
                this,
                new ApplicateRendererSettledEventArgs(generation));

        public void RaiseRendererFailed()
            => RendererFailed?.Invoke(
                this,
                new ApplicateRendererFailureEvent(
                    ApplicateRendererFailureKind.DocumentRenderFailed,
                    DocumentPath: null,
                    DateTime.UtcNow));

        public void RaiseRevealCompleted() => RevealCompleted?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public void BridgeUsesModeSwitchSmoothPreferenceForOpacityTransitions()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var vm = new FakeMainWindowVm
            {
                ReadingPreferences = ReadingPreferences.Default with { ModeSwitchSmoothDurationMs = 260 }
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);

            AssertOpacityTransition(viewerSlot, TimeSpan.FromMilliseconds(260));
            AssertOpacityTransition(editSlot, TimeSpan.FromMilliseconds(260));
            AssertOpacityTransition(editContent, TimeSpan.FromMilliseconds(260));

            vm.ReadingPreferences = vm.ReadingPreferences with
            {
                ModeSwitchSmoothEnabled = false
            };

            Assert.Null(viewerSlot.Transitions);
            Assert.Null(editSlot.Transitions);
            Assert.Null(editContent.Transitions);
        }, CancellationToken.None);
    }

    [Fact]
    public void EnteringEditModeShowsEditSlotWithSessionContent()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);

            var sessionRef = new object();
            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;

            Assert.False(viewerSlot.IsVisible);
            Assert.True(editSlot.IsVisible);
            Assert.True(editSlot.IsHitTestVisible);
            Assert.Same(sessionRef, editContent.DataContext);
        }, CancellationToken.None);
    }

    [Fact]
    public void ExitingEditModeShowsViewerSlotButKeepsStickyContent()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sessionRef = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object(),
                EditorSession = sessionRef,
                IsEditMode = true
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);
            bridge.ForceReconcile();

            Assert.True(editSlot.IsVisible);
            Assert.Same(sessionRef, editContent.DataContext);

            vm.IsEditMode = false;

            Assert.True(viewerSlot.IsVisible);
            Assert.False(editSlot.IsVisible);
            Assert.Same(sessionRef, editContent.DataContext);
        }, CancellationToken.None);
    }

    [Fact]
    public void CloseFileFromEditModeHidesViewerWhenDocumentClearsBeforeIsViewerFalse()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sessionRef = new object();
            var document = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                IsEditMode = true,
                EditorSession = sessionRef,
                Document = document
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);
            bridge.ForceReconcile();

            Assert.True(editSlot.IsVisible);
            Assert.Same(sessionRef, editContent.DataContext);

            vm.IsEditMode = false;
            Assert.False(editSlot.IsVisible);
            Assert.Same(sessionRef, editContent.DataContext);
            Assert.True(viewerSlot.IsVisible);

            vm.EditorSession = null;
            Assert.Null(editContent.DataContext);
            Assert.True(viewerSlot.IsVisible);

            vm.Document = null;
            Assert.False(viewerSlot.IsVisible);
            Assert.False(editSlot.IsVisible);

            vm.IsViewer = false;
            Assert.False(viewerSlot.IsVisible);
            Assert.False(editSlot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PropertyChangedFromBackgroundThreadDoesNotThrow()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        Exception? capturedException = null;

        await session.Dispatch(async () =>
        {
            try
            {
                var vm = new FakeMainWindowVm { Document = new object() };
                var viewerSlot = new ContentControl();
                var editSlot = new Grid();
                var editContent = new ContentControl();
                editSlot.Children.Add(editContent);
                using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);

                await vm.FireFromBackgroundThreadAsync(nameof(FakeMainWindowVm.IsViewer));
                await Task.Delay(100);
                await vm.FireFromBackgroundThreadAsync(nameof(FakeMainWindowVm.Document));
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        }, CancellationToken.None);

        Assert.Null(capturedException);
    }

    [Fact]
    public void NewSessionReplacesContentWhenEditorSessionRefChanges()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var firstSession = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object(),
                EditorSession = firstSession,
                IsEditMode = true
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);
            bridge.ForceReconcile();

            Assert.Same(firstSession, editContent.DataContext);

            var secondSession = new object();
            vm.IsEditMode = false;
            vm.EditorSession = secondSession;
            vm.IsEditMode = true;

            Assert.Same(secondSession, editContent.DataContext);
            Assert.True(editSlot.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public void TransactionControllerSeparatesDisplayedAndRequestedModes()
    {
        var controller = new ApplicateModeTransitionController(ApplicateMode.Viewer);

        var generation = controller.RequestMode(ApplicateMode.Edit);
        var snapshot = controller.Snapshot;

        Assert.True(generation > 0);
        Assert.Equal(ApplicateMode.Viewer, snapshot.DisplayedMode);
        Assert.Equal(ApplicateMode.Edit, snapshot.RequestedMode);
        Assert.Equal(generation, snapshot.ActiveGeneration);
        Assert.True(snapshot.IsSwitching);

        var viewer = controller.GetSlotState(ApplicateMode.Viewer);
        var edit = controller.GetSlotState(ApplicateMode.Edit);
        Assert.True(viewer.IsVisible);
        Assert.Equal(1.0, viewer.Opacity);
        Assert.False(viewer.IsInteractive);
        Assert.True(edit.IsVisible);
        Assert.Equal(0.0, edit.Opacity);
        Assert.False(edit.IsInteractive);
    }

    [Fact]
    public void TransactionControllerCommitsOnlyAfterFullReadinessQuorum()
    {
        var controller = new ApplicateModeTransitionController(ApplicateMode.Viewer);
        var generation = controller.RequestMode(ApplicateMode.Edit);

        Assert.False(controller.ApplyLayoutSettled(generation));
        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.DisplayedMode);

        Assert.False(controller.ApplyCommitCompleted(generation, ApplicateMode.Edit));
        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.DisplayedMode);

        Assert.False(controller.ApplyMinimapSettled(generation));
        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.DisplayedMode);

        Assert.True(controller.ApplyRendererSettled(generation));
        Assert.Equal(ApplicateMode.Edit, controller.Snapshot.DisplayedMode);
        Assert.Equal(ApplicateMode.Edit, controller.Snapshot.RequestedMode);
        Assert.False(controller.Snapshot.IsSwitching);

        var edit = controller.GetSlotState(ApplicateMode.Edit);
        Assert.True(edit.IsVisible);
        Assert.Equal(1.0, edit.Opacity);
        Assert.True(edit.IsInteractive);
    }

    [Fact]
    public void TransactionControllerDoesNotCommitWithoutReadinessSignals()
    {
        var controller = new ApplicateModeTransitionController(ApplicateMode.Viewer);

        controller.RequestMode(ApplicateMode.Edit);

        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.DisplayedMode);
        Assert.True(controller.Snapshot.IsSwitching);
        Assert.False(controller.Snapshot.IsReadyToCommit);
    }

    [Fact]
    public void TransactionControllerDropsStaleGenerationSignals()
    {
        var controller = new ApplicateModeTransitionController(ApplicateMode.Viewer);
        var staleGeneration = controller.RequestMode(ApplicateMode.Edit);
        Assert.Equal(staleGeneration, controller.RequestMode(ApplicateMode.Edit));
        Assert.Equal(0, controller.RequestMode(ApplicateMode.Viewer));
        var activeGeneration = controller.RequestMode(ApplicateMode.Edit);

        Assert.NotEqual(staleGeneration, activeGeneration);
        Assert.False(controller.ApplyLayoutSettled(staleGeneration));
        Assert.False(controller.ApplyCommitCompleted(staleGeneration, ApplicateMode.Edit));
        Assert.False(controller.ApplyMinimapSettled(staleGeneration));
        Assert.False(controller.ApplyRendererSettled(staleGeneration));

        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.DisplayedMode);
        Assert.Equal(activeGeneration, controller.Snapshot.ActiveGeneration);

        Assert.False(controller.ApplyLayoutSettled(activeGeneration));
        Assert.False(controller.ApplyMinimapSettled(activeGeneration));
        Assert.False(controller.ApplyCommitCompleted(activeGeneration, ApplicateMode.Edit));
        Assert.True(controller.ApplyRendererSettled(activeGeneration));
        Assert.Equal(ApplicateMode.Edit, controller.Snapshot.DisplayedMode);
    }

    [Fact]
    public void TransactionControllerCancelsWhenRapidToggleReturnsToDisplayedMode()
    {
        var controller = new ApplicateModeTransitionController(ApplicateMode.Viewer);
        var staleGeneration = controller.RequestMode(ApplicateMode.Edit);

        var cancelGeneration = controller.RequestMode(ApplicateMode.Viewer);

        Assert.Equal(0, cancelGeneration);
        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.DisplayedMode);
        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.RequestedMode);
        Assert.False(controller.Snapshot.IsSwitching);
        Assert.Equal(0, controller.Snapshot.ActiveGeneration);

        Assert.False(controller.ApplyLayoutSettled(staleGeneration));
        Assert.False(controller.ApplyCommitCompleted(staleGeneration, ApplicateMode.Edit));
        Assert.False(controller.ApplyMinimapSettled(staleGeneration));
        Assert.False(controller.ApplyRendererSettled(staleGeneration));
        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.DisplayedMode);
    }

    [Fact]
    public void TransactionControllerAbortsOnRendererFailure()
    {
        var controller = new ApplicateModeTransitionController(ApplicateMode.Viewer);
        var generation = controller.RequestMode(ApplicateMode.Edit);

        Assert.True(controller.ApplyRendererFailed(generation));

        Assert.True(controller.Snapshot.IsAborted);
        Assert.False(controller.Snapshot.IsSwitching);
        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.DisplayedMode);
        Assert.Equal(ApplicateMode.Edit, controller.Snapshot.RequestedMode);
        Assert.Equal(0, controller.Snapshot.ActiveGeneration);

        Assert.False(controller.ApplyLayoutSettled(generation));
        Assert.False(controller.ApplyCommitCompleted(generation, ApplicateMode.Edit));
        Assert.False(controller.ApplyMinimapSettled(generation));
        Assert.False(controller.ApplyRendererSettled(generation));
        Assert.Equal(ApplicateMode.Viewer, controller.Snapshot.DisplayedMode);
    }

    [Fact]
    public void BridgeAppliesTransactionGenerationContextToRequestedSlotOnly()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var vm = new FakeMainWindowVm();
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            var inheritedChild = new Panel();
            editSlot.Children.Add(editContent);
            editSlot.Children.Add(inheritedChild);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);

            bridge.ApplyTransactionGenerationContext(ApplicateMode.Edit, 55);

            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(viewerSlot));
            Assert.Equal(55, ApplicateModeTransactionContext.GetTransactionGeneration(editSlot));
            Assert.Equal(55, ApplicateModeTransactionContext.GetTransactionGeneration(inheritedChild));

            bridge.ApplyTransactionGenerationContext(ApplicateMode.Viewer, 77);

            Assert.Equal(77, ApplicateModeTransactionContext.GetTransactionGeneration(viewerSlot));
            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(editSlot));
            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(inheritedChild));
        }, CancellationToken.None);
    }

    [Fact]
    public void BridgeClearsTransactionGenerationContextForLegacyPath()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var vm = new FakeMainWindowVm();
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);

            bridge.ApplyTransactionGenerationContext(ApplicateMode.Edit, 55);
            bridge.ClearTransactionGenerationContext();

            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(viewerSlot));
            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(editSlot));
        }, CancellationToken.None);
    }

    [Fact]
    public void BridgeModeSwitchPublishesTransactionGenerationContextThroughReconcile()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var sessionRef = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            var editChild = new Panel();
            editSlot.Children.Add(editContent);
            editSlot.Children.Add(editChild);
            using var bridge = MakeBridge(vm, viewerSlot, editSlot, editContent);

            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(viewerSlot));
            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(editSlot));

            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;

            var editGeneration = ApplicateModeTransactionContext.GetTransactionGeneration(editSlot);
            Assert.True(editGeneration > 0);
            Assert.Equal(editGeneration, ApplicateModeTransactionContext.GetTransactionGeneration(editChild));
            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(viewerSlot));

            bridge.ForceReconcile();
            Assert.Equal(editGeneration, ApplicateModeTransactionContext.GetTransactionGeneration(editSlot));

            vm.IsEditMode = false;

            var viewerGeneration = ApplicateModeTransactionContext.GetTransactionGeneration(viewerSlot);
            Assert.True(viewerGeneration > editGeneration);
            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(editSlot));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TransactionalModeSwitchWaitsForRendererSettledBeforeReveal()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(async () =>
        {
            var sessionRef = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            ArrangeForLayout(viewerSlot, editSlot);

            var host = new FakeTransactionHost(
                () => (viewerSlot.Opacity, editSlot.Opacity));
            using var bridge = MakeBridge(
                vm,
                viewerSlot,
                editSlot,
                editContent,
                transactionHost: host);

            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;

            var generation = ApplicateModeTransactionContext.GetTransactionGeneration(editSlot);
            Assert.True(generation > 0);
            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.False(viewerSlot.IsHitTestVisible);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(0.0, editSlot.Opacity);
            Assert.False(editSlot.IsHitTestVisible);
            Assert.Empty(host.RevealedGenerations);

            host.RaiseCommitCompleted(generation, ApplicateMode.Edit);
            await Task.Delay(50);

            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(0.0, editSlot.Opacity);
            Assert.Empty(host.RevealedGenerations);

            host.RaiseMinimapSettledNotApplicable(generation);
            await Task.Delay(50);

            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(0.0, editSlot.Opacity);
            Assert.False(editSlot.IsHitTestVisible);
            Assert.Empty(host.RevealedGenerations);

            host.RaiseRendererSettled(generation);
            await Task.Delay(50);

            Assert.False(viewerSlot.IsVisible);
            Assert.Equal(0.0, viewerSlot.Opacity);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(1.0, editSlot.Opacity);
            Assert.True(editSlot.IsHitTestVisible);
            Assert.Equal(0, ApplicateModeTransactionContext.GetTransactionGeneration(editSlot));

            var revealed = Assert.Single(host.RevealedGenerations);
            Assert.Equal(generation, revealed.Generation);
            Assert.Equal(0.0, revealed.ViewerOpacity);
            Assert.Equal(1.0, revealed.EditOpacity);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TransactionalModeSwitchHasNoTimeoutFallbackCommit()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(async () =>
        {
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            ArrangeForLayout(viewerSlot, editSlot);

            var host = new FakeTransactionHost(
                () => (viewerSlot.Opacity, editSlot.Opacity));
            using var bridge = MakeBridge(
                vm,
                viewerSlot,
                editSlot,
                editContent,
                transactionHost: host);

            vm.EditorSession = new object();
            vm.IsEditMode = true;

            var generation = ApplicateModeTransactionContext.GetTransactionGeneration(editSlot);
            host.RaiseCommitCompleted(generation, ApplicateMode.Edit);
            await Task.Delay(750);

            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(0.0, editSlot.Opacity);
            Assert.False(editSlot.IsHitTestVisible);
            Assert.Empty(host.RevealedGenerations);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TransactionalModeSwitchDropsStaleGenerationBeforeNativeReveal()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(async () =>
        {
            var sessionRef = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            ArrangeForLayout(viewerSlot, editSlot);

            var host = new FakeTransactionHost(
                () => (viewerSlot.Opacity, editSlot.Opacity));
            using var bridge = MakeBridge(
                vm,
                viewerSlot,
                editSlot,
                editContent,
                transactionHost: host);

            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;
            var staleGeneration = ApplicateModeTransactionContext.GetTransactionGeneration(editSlot);

            vm.IsEditMode = false;
            var activeGeneration = ApplicateModeTransactionContext.GetTransactionGeneration(viewerSlot);
            Assert.True(activeGeneration > staleGeneration);

            host.RaiseCommitCompleted(staleGeneration, ApplicateMode.Edit);
            host.RaiseMinimapSettledNotApplicable(staleGeneration);
            host.RaiseRendererSettled(staleGeneration);
            await Task.Delay(50);

            Assert.Empty(host.RevealedGenerations);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(1.0, editSlot.Opacity);
            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(0.0, viewerSlot.Opacity);

            host.RaiseCommitCompleted(activeGeneration, ApplicateMode.Viewer);
            host.RaiseMinimapSettled(activeGeneration);
            await Task.Delay(50);
            Assert.Empty(host.RevealedGenerations);

            host.RaiseRendererSettled(activeGeneration);
            await Task.Delay(50);

            var revealed = Assert.Single(host.RevealedGenerations);
            Assert.Equal(activeGeneration, revealed.Generation);
            Assert.Equal(1.0, revealed.ViewerOpacity);
            Assert.Equal(0.0, revealed.EditOpacity);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TransactionalModeSwitchRestoresShieldWhenNativeRevealIsRejected()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(async () =>
        {
            var sessionRef = new object();
            var vm = new FakeMainWindowVm
            {
                IsViewer = true,
                Document = new object()
            };
            var viewerSlot = new ContentControl();
            var editSlot = new Grid();
            var editContent = new ContentControl();
            editSlot.Children.Add(editContent);
            ArrangeForLayout(viewerSlot, editSlot);

            var host = new FakeTransactionHost(
                () => (viewerSlot.Opacity, editSlot.Opacity))
            {
                RejectReveals = true
            };
            using var bridge = MakeBridge(
                vm,
                viewerSlot,
                editSlot,
                editContent,
                transactionHost: host);

            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;

            var generation = ApplicateModeTransactionContext.GetTransactionGeneration(editSlot);
            host.RaiseCommitCompleted(generation, ApplicateMode.Edit);
            host.RaiseMinimapSettledNotApplicable(generation);
            host.RaiseRendererSettled(generation);
            await Task.Delay(50);

            var rejectedReveal = Assert.Single(host.RevealedGenerations);
            Assert.Equal(generation, rejectedReveal.Generation);
            Assert.Equal(0.0, rejectedReveal.ViewerOpacity);
            Assert.Equal(1.0, rejectedReveal.EditOpacity);

            Assert.True(viewerSlot.IsVisible);
            Assert.Equal(1.0, viewerSlot.Opacity);
            Assert.False(viewerSlot.IsHitTestVisible);
            Assert.True(editSlot.IsVisible);
            Assert.Equal(0.0, editSlot.Opacity);
            Assert.False(editSlot.IsHitTestVisible);
        }, CancellationToken.None);
    }

    private static void ArrangeForLayout(params Control[] controls)
    {
        foreach (var control in controls)
        {
            control.Measure(new Size(800, 600));
            control.Arrange(new Rect(0, 0, 800, 600));
        }
    }

    private static void AssertOpacityTransition(Control control)
        => AssertOpacityTransition(control, TimeSpan.FromMilliseconds(180));

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

    private static void AssertOpacityTransition(Control control, TimeSpan expectedDuration)
    {
        var transition = Assert.Single(Assert.IsType<Transitions>(control.Transitions).OfType<DoubleTransition>());
        Assert.Equal(Visual.OpacityProperty, transition.Property);
        Assert.Equal(expectedDuration, transition.Duration);
        Assert.NotNull(transition.Easing);
    }
}

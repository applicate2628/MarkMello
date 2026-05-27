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

    private static ApplicateSiblingMountBridge MakeBridge(
        FakeMainWindowVm vm,
        ContentControl viewer,
        Panel edit,
        Control editContent,
        FakeModeRevealSignal? modeRevealSignal = null) =>
        new(vm, viewer, edit, editContent,
            () => vm.IsViewer, () => vm.IsEditMode,
            () => vm.EditorSession, () => vm.Document,
            () => vm.ReadingPreferences,
            viewerContent: vm,
            modeRevealSignal: modeRevealSignal);

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
    public void ModeSwitchCoverUsesPopupTopLevelForNativeWebViewAirspace()
    {
        var source = File.ReadAllText(BridgeSourcePath);

        Assert.Contains("new Popup", source, StringComparison.Ordinal);
        Assert.Contains("ShouldUseOverlayLayer = false", source, StringComparison.Ordinal);
        Assert.Contains("Placement = PlacementMode.Center", source, StringComparison.Ordinal);
        Assert.Contains("_modeRevealCoverHost.Children.Add(_modeRevealCoverPopup)", source, StringComparison.Ordinal);
        Assert.Contains("_modeRevealCoverPopup.IsOpen = true", source, StringComparison.Ordinal);
        Assert.Contains("bridge-cover-cancelled", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_modeRevealCoverHost.Children.Add(_modeRevealCover)", source, StringComparison.Ordinal);
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

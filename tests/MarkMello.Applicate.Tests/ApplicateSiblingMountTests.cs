using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Tests.Fakes;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateSiblingMountTests
{
    private static ApplicateSiblingMountBridge MakeBridge(FakeMainWindowVm vm, ContentControl viewer, ContentControl edit) =>
        new(vm, viewer, edit,
            () => vm.IsViewer, () => vm.IsEditMode,
            () => vm.EditorSession, () => vm.Document,
            viewerContent: vm);

    [Fact]
    public void OpeningDocumentInReaderModeShowsViewerSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var vm = new FakeMainWindowVm();
            var viewerSlot = new ContentControl();
            var editSlot = new ContentControl();
            using var bridge = MakeBridge(vm, viewerSlot, editSlot);

            vm.Document = new object();
            vm.IsViewer = true;

            Assert.True(viewerSlot.IsVisible);
            Assert.True(viewerSlot.IsEnabled);
            Assert.True(viewerSlot.IsHitTestVisible);
            Assert.True(viewerSlot.IsTabStop);
            Assert.False(editSlot.IsVisible);
            Assert.False(editSlot.IsHitTestVisible);
            Assert.Null(editSlot.Content);
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
            var editSlot = new ContentControl();
            using var bridge = MakeBridge(vm, viewerSlot, editSlot);

            var sessionRef = new object();
            vm.EditorSession = sessionRef;
            vm.IsEditMode = true;

            Assert.False(viewerSlot.IsVisible);
            Assert.True(editSlot.IsVisible);
            Assert.True(editSlot.IsHitTestVisible);
            Assert.Same(sessionRef, editSlot.Content);
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
            var editSlot = new ContentControl();
            using var bridge = MakeBridge(vm, viewerSlot, editSlot);
            bridge.ForceReconcile();

            Assert.True(editSlot.IsVisible);
            Assert.Same(sessionRef, editSlot.Content);

            vm.IsEditMode = false;

            Assert.True(viewerSlot.IsVisible);
            Assert.False(editSlot.IsVisible);
            Assert.Same(sessionRef, editSlot.Content);
        }, CancellationToken.None);
    }

    [Fact]
    public void CloseFileFromEditMode_FourTickSequence_ViewerHidesAtDocumentNullNotIsViewerFalse()
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
            var editSlot = new ContentControl();
            using var bridge = MakeBridge(vm, viewerSlot, editSlot);
            bridge.ForceReconcile();

            Assert.True(editSlot.IsVisible);
            Assert.Same(sessionRef, editSlot.Content);

            vm.IsEditMode = false;
            Assert.False(editSlot.IsVisible);
            Assert.Same(sessionRef, editSlot.Content);
            Assert.True(viewerSlot.IsVisible);

            vm.EditorSession = null;
            Assert.Null(editSlot.Content);
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
    public async Task PropertyChangedFromBackgroundThreadReconcilesOnUIThread()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        Exception? capturedException = null;
        bool reconcileObservedOnUIThread = false;

        await session.Dispatch(async () =>
        {
            try
            {
                var vm = new FakeMainWindowVm();
                var viewerSlot = new ContentControl();
                var editSlot = new ContentControl();
                using var bridge = MakeBridge(vm, viewerSlot, editSlot);

                vm.Document = new object();
                await vm.FireFromBackgroundThreadAsync(nameof(FakeMainWindowVm.IsViewer));

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    reconcileObservedOnUIThread = Dispatcher.UIThread.CheckAccess();
                });
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        }, CancellationToken.None);

        Assert.Null(capturedException);
        Assert.True(reconcileObservedOnUIThread);
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
            var editSlot = new ContentControl();
            using var bridge = MakeBridge(vm, viewerSlot, editSlot);
            bridge.ForceReconcile();

            Assert.Same(firstSession, editSlot.Content);

            var secondSession = new object();
            vm.IsEditMode = false;
            vm.EditorSession = secondSession;
            vm.IsEditMode = true;

            Assert.Same(secondSession, editSlot.Content);
            Assert.True(editSlot.IsVisible);
        }, CancellationToken.None);
    }
}

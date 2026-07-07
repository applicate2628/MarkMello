using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Applicate.Desktop.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateTabsViewTests : IDisposable
{
    private readonly string _tempRoot;

    public ApplicateTabsViewTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MarkMello.Applicate.Tests.Tabs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task CloseToLeftClosesOnlyDocumentsBeforeAnchor()
    {
        var service = new OpenDocumentsService();
        var docA = await OpenTempAsync(service, "a.md");
        var docB = await OpenTempAsync(service, "b.md");
        var docC = await OpenTempAsync(service, "c.md");
        var docD = await OpenTempAsync(service, "d.md");

        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var view = new ApplicateTabsView(service);

            InvokePrivate(view, "CloseToLeft", docC);
        }, CancellationToken.None);

        Assert.Equal(new[] { docC, docD }, service.OpenDocuments);
        Assert.DoesNotContain(docA, service.OpenDocuments);
        Assert.DoesNotContain(docB, service.OpenDocuments);
    }

    [Fact]
    public async Task TabContextMenuPlacesCloseToLeftAdjacentToCloseToRight()
    {
        var service = new OpenDocumentsService();
        var docA = await OpenTempAsync(service, "a.md");
        var docB = await OpenTempAsync(service, "b.md");
        await OpenTempAsync(service, "c.md");

        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var view = new ApplicateTabsView(service);
            var firstMenuItems = BuildMenuItems(view, docA);
            var middleMenuItems = BuildMenuItems(view, docB);
            var middleHeaders = middleMenuItems.Select(item => item.Header as string).ToList();

            var leftIndex = middleHeaders.IndexOf("Close to the Left");
            var rightIndex = middleHeaders.IndexOf("Close to the Right");

            Assert.True(leftIndex >= 0, "Close to the Left should be present.");
            Assert.Equal(leftIndex + 1, rightIndex);
            Assert.False(firstMenuItems[leftIndex].IsEnabled);
            Assert.True(middleMenuItems[leftIndex].IsEnabled);
        }, CancellationToken.None);
    }

    private async Task<OpenDocument> OpenTempAsync(OpenDocumentsService service, string fileName)
    {
        var path = Path.Combine(_tempRoot, fileName);
        await File.WriteAllTextAsync(path, fileName);
        return await service.OpenAsync(path);
    }

    private static List<MenuItem> BuildMenuItems(ApplicateTabsView view, OpenDocument doc)
    {
        var menu = InvokePrivate<ContextMenu>(view, "BuildTabContextMenu", doc);
        return menu.Items.OfType<MenuItem>().ToList();
    }

    private static void InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method.Invoke(target, args);
    }

    private static T InvokePrivate<T>(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<T>(method.Invoke(target, args));
    }
}

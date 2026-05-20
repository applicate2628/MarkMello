using System;
using System.IO;
using System.Threading.Tasks;
using MarkMello.Applicate.Desktop.Editing;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class OpenDocumentsServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public OpenDocumentsServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MarkMello.Applicate.Tests.OpenDocs", Guid.NewGuid().ToString("N"));
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
            // Best-effort cleanup
        }
    }

    private string WriteTemp(string fileName, string contents)
    {
        var path = Path.Combine(_tempRoot, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void NoDocumentsNoActive()
    {
        var service = new OpenDocumentsService();

        Assert.Empty(service.OpenDocuments);
        Assert.Null(service.ActiveDocument);
    }

    [Fact]
    public async Task OpenAddsDocumentAndActivates()
    {
        var path = WriteTemp("a.md", "# Hello");
        var service = new OpenDocumentsService();

        var opened = await service.OpenAsync(path);

        Assert.Single(service.OpenDocuments);
        Assert.Same(opened, service.ActiveDocument);
        Assert.Equal(path, opened.FilePath);
        Assert.Equal("a.md", opened.DisplayName);
        Assert.Equal("# Hello", opened.SourceText);
    }

    [Fact]
    public async Task OpenSamePathTwiceActivatesExistingDoesNotDuplicate()
    {
        var path = WriteTemp("a.md", "# Hello");
        var service = new OpenDocumentsService();

        var first = await service.OpenAsync(path);
        var second = await service.OpenAsync(path);

        Assert.Single(service.OpenDocuments);
        Assert.Same(first, second);
        Assert.Same(first, service.ActiveDocument);
    }

    [Fact]
    public async Task OpenWithActivateFalseAddsDocumentButDoesNotChangeActive()
    {
        var a = WriteTemp("a.md", "A");
        var b = WriteTemp("b.md", "B");
        var service = new OpenDocumentsService();
        var docA = await service.OpenAsync(a);

        var docB = await service.OpenAsync(b, activate: false);

        Assert.Equal(2, service.OpenDocuments.Count);
        Assert.Same(docA, service.ActiveDocument);
        Assert.NotSame(docB, service.ActiveDocument);
    }

    [Fact]
    public async Task OpenWithActivateFalseDoesNotFireActiveDocumentChanged()
    {
        var a = WriteTemp("a.md", "A");
        var b = WriteTemp("b.md", "B");
        var service = new OpenDocumentsService();
        await service.OpenAsync(a);

        var events = 0;
        service.ActiveDocumentChanged += (_, _) => events++;

        await service.OpenAsync(b, activate: false);

        Assert.Equal(0, events);
    }

    [Fact]
    public async Task OpenExistingPathWithActivateFalseDoesNotChangeActive()
    {
        var a = WriteTemp("a.md", "A");
        var b = WriteTemp("b.md", "B");
        var service = new OpenDocumentsService();
        var docA = await service.OpenAsync(a);
        await service.OpenAsync(b);
        service.Activate(docA);

        var reopened = await service.OpenAsync(b, activate: false);

        Assert.Same(docA, service.ActiveDocument);
        Assert.NotSame(reopened, service.ActiveDocument);
    }

    [Fact]
    public async Task OpenDifferentPathsKeepsBothActivatesLast()
    {
        var a = WriteTemp("a.md", "A");
        var b = WriteTemp("b.md", "B");
        var service = new OpenDocumentsService();

        var docA = await service.OpenAsync(a);
        var docB = await service.OpenAsync(b);

        Assert.Equal(2, service.OpenDocuments.Count);
        Assert.Same(docB, service.ActiveDocument);
        Assert.Contains(docA, service.OpenDocuments);
        Assert.Contains(docB, service.OpenDocuments);
    }

    [Fact]
    public async Task ActivateChangesActiveDocument()
    {
        var a = WriteTemp("a.md", "A");
        var b = WriteTemp("b.md", "B");
        var service = new OpenDocumentsService();
        var docA = await service.OpenAsync(a);
        await service.OpenAsync(b);

        service.Activate(docA);

        Assert.Same(docA, service.ActiveDocument);
    }

    [Fact]
    public async Task CloseRemovesDocumentAndPicksNeighborActive()
    {
        var a = WriteTemp("a.md", "A");
        var b = WriteTemp("b.md", "B");
        var service = new OpenDocumentsService();
        var docA = await service.OpenAsync(a);
        var docB = await service.OpenAsync(b);

        service.Close(docB);

        Assert.Single(service.OpenDocuments);
        Assert.DoesNotContain(docB, service.OpenDocuments);
        Assert.Same(docA, service.ActiveDocument);
    }

    [Fact]
    public async Task CloseLastDocumentMakesActiveNull()
    {
        var a = WriteTemp("a.md", "A");
        var service = new OpenDocumentsService();
        var docA = await service.OpenAsync(a);

        service.Close(docA);

        Assert.Empty(service.OpenDocuments);
        Assert.Null(service.ActiveDocument);
    }

    [Fact]
    public async Task ActiveDocumentChangedFiresOnActivate()
    {
        var a = WriteTemp("a.md", "A");
        var b = WriteTemp("b.md", "B");
        var service = new OpenDocumentsService();
        var docA = await service.OpenAsync(a);
        await service.OpenAsync(b);

        var fired = 0;
        OpenDocument? lastActive = null;
        service.ActiveDocumentChanged += (_, args) =>
        {
            fired++;
            lastActive = args.ActiveDocument;
        };

        service.Activate(docA);

        Assert.Equal(1, fired);
        Assert.Same(docA, lastActive);
    }

    [Fact]
    public async Task UpdateStateStoresCaretAndScroll()
    {
        var path = WriteTemp("a.md", "# Hello");
        var service = new OpenDocumentsService();
        var doc = await service.OpenAsync(path);

        service.UpdateState(doc, caret: 42, scrollProgressPercent: 33.5);

        Assert.Equal(42, doc.EditorCaret);
        Assert.Equal(33.5, doc.ScrollProgressPercent);
    }

    [Fact]
    public async Task UpdateSourceTextRefreshesCachedDocumentTextWithoutChangingActiveTab()
    {
        var path = WriteTemp("a.md", "old");
        var service = new OpenDocumentsService();
        var doc = await service.OpenAsync(path);

        service.UpdateSourceText(doc, "new");

        Assert.Same(doc, service.ActiveDocument);
        Assert.Equal("new", doc.SourceText);
    }

    [Fact]
    public async Task OpenAsyncNonExistentPathThrows()
    {
        var service = new OpenDocumentsService();
        var missing = Path.Combine(_tempRoot, "missing.md");

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await service.OpenAsync(missing));
    }

    [Fact]
    public async Task OpenAsyncConcurrentSamePathDoesNotDuplicate()
    {
        var path = WriteTemp("race.md", "race content");
        var service = new OpenDocumentsService();

        var t1 = service.OpenAsync(path);
        var t2 = service.OpenAsync(path);
        await Task.WhenAll(t1, t2);

        Assert.Single(service.OpenDocuments);
    }

    // Multi-tab startup-scaling polish: lightweight stub tabs are added
    // to OpenDocuments without reading the file. Source text stays empty
    // and IsLoaded is false until EnsureLoadedAsync materializes them.
    [Fact]
    public async Task OpenStubAsyncAddsStubWithoutReadingFile()
    {
        var path = WriteTemp("stub.md", "stub body");
        var service = new OpenDocumentsService();

        var stub = await service.OpenStubAsync(path);

        Assert.Single(service.OpenDocuments);
        Assert.False(stub.IsLoaded);
        Assert.Equal(string.Empty, stub.SourceText);
        Assert.Null(service.ActiveDocument);
        Assert.Equal(path, stub.FilePath);
    }

    [Fact]
    public async Task OpenStubAsyncDoesNotRequireExistingFile()
    {
        // The file system check that OpenAsync performs is intentionally
        // skipped so a stale session entry whose file has since moved
        // does not block startup. The error surfaces when the user
        // activates the tab and EnsureLoadedAsync fails.
        var missing = Path.Combine(_tempRoot, "not-yet-created.md");
        var service = new OpenDocumentsService();

        var stub = await service.OpenStubAsync(missing);

        Assert.Single(service.OpenDocuments);
        Assert.False(stub.IsLoaded);
    }

    [Fact]
    public async Task EnsureLoadedAsyncReadsContentsAndFlipsIsLoaded()
    {
        var path = WriteTemp("late.md", "late content");
        var service = new OpenDocumentsService();
        var stub = await service.OpenStubAsync(path);

        await service.EnsureLoadedAsync(stub);

        Assert.True(stub.IsLoaded);
        Assert.Equal("late content", stub.SourceText);
    }

    [Fact]
    public async Task EnsureLoadedAsyncIsNoOpOnAlreadyLoadedDocument()
    {
        var path = WriteTemp("loaded.md", "fresh");
        var service = new OpenDocumentsService();
        var doc = await service.OpenAsync(path);

        // Mutate the file on disk; EnsureLoadedAsync must NOT re-read it
        // because the document is already loaded and one-shot semantics
        // for refresh live in UpdateSourceText / OpenPathAsync, not here.
        File.WriteAllText(path, "changed on disk");
        await service.EnsureLoadedAsync(doc);

        Assert.True(doc.IsLoaded);
        Assert.Equal("fresh", doc.SourceText);
    }

    [Fact]
    public async Task OpenStubAsyncReturnsExistingDocumentForKnownPath()
    {
        var path = WriteTemp("repeat.md", "data");
        var service = new OpenDocumentsService();
        var loaded = await service.OpenAsync(path);

        // A stub request for an already-loaded path returns the same
        // OpenDocument instance and does not regress its loaded state.
        var stubAttempt = await service.OpenStubAsync(path);

        Assert.Single(service.OpenDocuments);
        Assert.Same(loaded, stubAttempt);
        Assert.True(stubAttempt.IsLoaded);
    }

    [Fact]
    public async Task UpdateSourceTextFlipsStubToLoaded()
    {
        var path = WriteTemp("backfill.md", "disk");
        var service = new OpenDocumentsService();
        var stub = await service.OpenStubAsync(path);
        Assert.False(stub.IsLoaded);

        service.UpdateSourceText(stub, "fresh text");

        Assert.True(stub.IsLoaded);
        Assert.Equal("fresh text", stub.SourceText);
    }
}

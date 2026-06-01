using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MarkMello.Applicate.Desktop.Editing;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class JsonApplicateSessionStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public JsonApplicateSessionStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MarkMello.Applicate.Tests.Session", Guid.NewGuid().ToString("N"));
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
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task LoadReturnsEmptyWhenFileMissing()
    {
        var store = new JsonApplicateSessionStore(_tempRoot);

        var session = await store.LoadAsync();

        Assert.Empty(session.OpenPaths);
        Assert.Null(session.ActivePath);
    }

    [Fact]
    public async Task SaveThenLoadRoundtrips()
    {
        var store = new JsonApplicateSessionStore(_tempRoot);
        var session = new ApplicateSession
        {
            OpenPaths = new List<string> { @"C:\a\one.md", @"C:\a\two.md" },
            ActivePath = @"C:\a\two.md",
        };

        await store.SaveAsync(session);
        var loaded = await store.LoadAsync();

        Assert.Equal(2, loaded.OpenPaths.Count);
        Assert.Equal(@"C:\a\one.md", loaded.OpenPaths[0]);
        Assert.Equal(@"C:\a\two.md", loaded.OpenPaths[1]);
        Assert.Equal(@"C:\a\two.md", loaded.ActivePath);
    }

    [Fact]
    public void StartupDocumentPathPrefersActivePathThenFirstOpenPath()
    {
        var session = new ApplicateSession
        {
            OpenPaths = new List<string> { "", @"C:\a\one.md" },
            ActivePath = @"C:\a\two.md",
        };
        var legacySession = new ApplicateSession
        {
            OpenPaths = new List<string> { "", @"C:\a\one.md" },
        };

        Assert.Equal(@"C:\a\two.md", session.GetStartupDocumentPath());
        Assert.Equal(@"C:\a\one.md", legacySession.GetStartupDocumentPath());
        Assert.Null(ApplicateSession.Empty.GetStartupDocumentPath());
    }

    [Fact]
    public async Task LoadCorruptFileReturnsEmpty()
    {
        var sessionFile = Path.Combine(_tempRoot, "applicate-session.json");
        await File.WriteAllTextAsync(sessionFile, "{not valid json");
        var store = new JsonApplicateSessionStore(_tempRoot);

        var session = await store.LoadAsync();

        Assert.Empty(session.OpenPaths);
        Assert.Null(session.ActivePath);
    }

    [Fact]
    public async Task SaveEmptySessionPersistsEmpty()
    {
        var store = new JsonApplicateSessionStore(_tempRoot);

        await store.SaveAsync(ApplicateSession.Empty);
        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.OpenPaths);
        Assert.Null(loaded.ActivePath);
    }

    [Fact]
    public async Task SaveOverwritesPriorState()
    {
        var store = new JsonApplicateSessionStore(_tempRoot);
        await store.SaveAsync(new ApplicateSession
        {
            OpenPaths = new List<string> { @"C:\a\first.md" },
            ActivePath = @"C:\a\first.md",
        });

        await store.SaveAsync(new ApplicateSession
        {
            OpenPaths = new List<string> { @"C:\a\second.md", @"C:\a\third.md" },
            ActivePath = @"C:\a\third.md",
        });
        var loaded = await store.LoadAsync();

        Assert.Equal(2, loaded.OpenPaths.Count);
        Assert.Equal(@"C:\a\second.md", loaded.OpenPaths[0]);
        Assert.Equal(@"C:\a\third.md", loaded.ActivePath);
    }
}

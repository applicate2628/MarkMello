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

        Assert.NotNull(session);
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

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.OpenPaths.Count);
        Assert.Equal(@"C:\a\one.md", loaded.OpenPaths[0]);
        Assert.Equal(@"C:\a\two.md", loaded.OpenPaths[1]);
        Assert.Equal(@"C:\a\two.md", loaded.ActivePath);
    }

    [Fact]
    public async Task RecentPathsRoundtrip()
    {
        // D11: the persisted recent list must survive save/load. A legacy session file with no
        // RecentPaths key loads as an empty list (back-compat), covered by the null-tolerant read.
        var store = new JsonApplicateSessionStore(_tempRoot);
        var session = new ApplicateSession
        {
            OpenPaths = new List<string> { @"C:\a\one.md" },
            ActivePath = @"C:\a\one.md",
            RecentPaths = new List<string> { @"C:\a\one.md", @"C:\a\old.md" },
        };

        await store.SaveAsync(session);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.RecentPaths.Count);
        Assert.Equal(@"C:\a\one.md", loaded.RecentPaths[0]);
        Assert.Equal(@"C:\a\old.md", loaded.RecentPaths[1]);
    }

    [Fact]
    public async Task ClearedRecentPathsRoundtripsWithoutDisturbingOpenPathsOrActivePath()
    {
        // Recent-files DELTA (P2): clearing the recent list writes RecentPaths=[] -- this must
        // not perturb the unrelated OpenPaths/ActivePath fields SaveSession also owns.
        var store = new JsonApplicateSessionStore(_tempRoot);
        var session = new ApplicateSession
        {
            OpenPaths = new List<string> { @"C:\a\one.md", @"C:\a\two.md" },
            ActivePath = @"C:\a\one.md",
            RecentPaths = new List<string>(),
        };

        await store.SaveAsync(session);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Empty(loaded.RecentPaths);
        Assert.Equal(2, loaded.OpenPaths.Count);
        Assert.Equal(@"C:\a\one.md", loaded.OpenPaths[0]);
        Assert.Equal(@"C:\a\two.md", loaded.OpenPaths[1]);
        Assert.Equal(@"C:\a\one.md", loaded.ActivePath);
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

        Assert.NotNull(session);
        Assert.Empty(session.OpenPaths);
        Assert.Null(session.ActivePath);
    }

    [Fact]
    public async Task SaveEmptySessionPersistsEmpty()
    {
        var store = new JsonApplicateSessionStore(_tempRoot);

        await store.SaveAsync(ApplicateSession.Empty);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
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

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.OpenPaths.Count);
        Assert.Equal(@"C:\a\second.md", loaded.OpenPaths[0]);
        Assert.Equal(@"C:\a\third.md", loaded.ActivePath);
    }

    // -------------------------------------------------------------------------------------------
    // D13 (decision 2026-07-26-d13-persisted-session-observation-contract): "observed and empty" and
    // "could not observe" are different facts, and this store is the single owner of the difference.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// G1 -- d13 clause 2, the OBSERVED half. All four routes on which the store looked and found no
    /// usable session still return a session, because an empty baseline is TRUE for each of them.
    /// Mapping any one of them to null is a regression in the OTHER direction: absent/blank would
    /// stop first run from ever persisting, and unparseable would freeze persistence permanently on
    /// a corrupt file with no in-app recovery.
    /// </summary>
    [Fact]
    public async Task LoadReturnsAnEmptySessionForEveryObservedAbsenceOfState()
    {
        foreach (var (caseName, contents) in new (string, string?)[]
        {
            ("absent", null),
            ("blank", "   \r\n  "),
            ("json-null-literal", "null"),
            ("unparseable", "{not valid json"),
        })
        {
            var caseRoot = Path.Combine(_tempRoot, caseName);
            Directory.CreateDirectory(caseRoot);
            if (contents is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(caseRoot, "applicate-session.json"), contents);
            }

            var session = await new JsonApplicateSessionStore(caseRoot).LoadAsync();

            Assert.NotNull(session);
            Assert.Empty(session.OpenPaths);
            Assert.Null(session.ActivePath);
            Assert.Empty(session.RecentPaths);
        }
    }

    /// <summary>
    /// G2 -- d13 clause 1, the UNOBSERVED half, and the store-side guard for the filed defect. A read
    /// the store could not perform must report null, NOT a value-shaped sentinel the caller cannot
    /// tell from real data. The second half is the user-facing guarantee made executable: the file
    /// the process refused to read was never damaged, and a later successful read recovers it whole.
    /// </summary>
    [Fact]
    public async Task LoadReturnsNullOnlyWhenThePersistedStateCannotBeObserved()
    {
        var store = new JsonApplicateSessionStore(_tempRoot);
        await store.SaveAsync(new ApplicateSession
        {
            OpenPaths = new List<string> { @"C:\a\one.md", @"C:\a\two.md", @"C:\a\three.md" },
            ActivePath = @"C:\a\two.md",
            RecentPaths = new List<string> { @"C:\a\two.md", @"C:\a\recent.md" },
        });

        var sessionFile = Path.Combine(_tempRoot, "applicate-session.json");
        using (var _ = new FileStream(sessionFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(await store.LoadAsync());
        }

        var recovered = await store.LoadAsync();

        Assert.NotNull(recovered);
        Assert.Equal(
            new List<string> { @"C:\a\one.md", @"C:\a\two.md", @"C:\a\three.md" },
            recovered.OpenPaths);
        Assert.Equal(@"C:\a\two.md", recovered.ActivePath);
        Assert.Equal(new List<string> { @"C:\a\two.md", @"C:\a\recent.md" }, recovered.RecentPaths);
    }
}

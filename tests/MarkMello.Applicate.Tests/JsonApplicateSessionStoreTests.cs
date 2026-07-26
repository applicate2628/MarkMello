using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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
    /// G1 -- d13 clause 2, the OBSERVED half. Every route on which the store looked and found no
    /// usable session still returns a session, because an empty baseline is TRUE for each of them.
    /// Mapping any one of them to null is a regression in the OTHER direction: absent/blank would
    /// stop first run from ever persisting, and unparseable would freeze persistence permanently on
    /// a corrupt file with no in-app recovery.
    /// <para>
    /// <c>absent-directory</c> is the first launch after install, and it is a DISTINCT route from
    /// <c>absent</c>: the containing %AppData% directory does not exist yet, so the OS reports the
    /// path missing rather than the file, and only SaveAsync ever creates that directory. A fix that
    /// treats every failed read as unobserved lands here and freezes a fresh install permanently.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LoadReturnsAnEmptySessionForEveryObservedAbsenceOfState()
    {
        foreach (var (caseName, contents, createRoot) in new (string, string?, bool)[]
        {
            ("absent", null, true),
            ("absent-directory", null, false),
            ("blank", "   \r\n  ", true),
            ("json-null-literal", "null", true),
            ("unparseable", "{not valid json", true),
        })
        {
            var caseRoot = Path.Combine(_tempRoot, caseName);
            if (createRoot)
            {
                Directory.CreateDirectory(caseRoot);
            }

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

    /// <summary>
    /// G4 -- the half an existence preflight silently breaks. File.Exists is documented to return
    /// false "if any error occurs while trying to determine if the specified file exists ... a failing
    /// or missing disk, or if the caller does not have permission to read the file", and to do so
    /// WITHOUT throwing. So it cannot tell "there is no file" from "I could not find out", and a store
    /// that preflights with it reports a FORGED observed-empty session -- the exact collapse d13
    /// forbids, reached by this bug's own most-cited scenario (an unreachable redirected %AppData%).
    /// <para>
    /// G2's FileShare.None lock cannot reach this class: there File.Exists SUCCEEDS and the read
    /// throws. Each case below is a real access condition in which File.Exists itself returns false
    /// for state that was never observed, and each is a SEPARATE fact on purpose -- folded into one,
    /// the first failing assertion short-circuits the rest, so a later case would never actually run
    /// under a mutation and its RED would be unproven. The assertion on File.Exists is likewise
    /// deliberate: a case that stops reproducing must fail loudly rather than pass vacuously.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LoadReturnsNullWhenADirectoryOccupiesTheSessionPath()
    {
        // File.Exists is documented to return false when the path describes a directory; the read
        // then fails with UnauthorizedAccessException. Needs no ACL, so this holds even where
        // privileges forbid rewriting one.
        var occupiedRoot = Path.Combine(_tempRoot, "occupied");
        var occupiedFile = Path.Combine(occupiedRoot, "applicate-session.json");
        Directory.CreateDirectory(occupiedFile);

        Assert.False(File.Exists(occupiedFile));
        Assert.Null(await new JsonApplicateSessionStore(occupiedRoot).LoadAsync());
    }

    /// <summary>
    /// G4b -- the same collapse reached by a real permission denial, which is the route the filed
    /// defect actually cites (an unreachable or access-denied redirected %AppData%). The denial must
    /// sit on the CONTAINING DIRECTORY: that is the shape that makes File.Exists lie about a present,
    /// populated file. A denial on the file itself does NOT -- File.Exists still reports true there
    /// (measured on Windows 11 / .NET 10, not assumed), which is precisely why G2's lock-based guard
    /// cannot cover this.
    /// </summary>
    [Fact]
    public async Task LoadReturnsNullWhenAPermissionDenialMakesAPresentFileLookAbsent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var deniedRoot = Path.Combine(_tempRoot, "denied");
        Directory.CreateDirectory(deniedRoot);
        var store = new JsonApplicateSessionStore(deniedRoot);
        await store.SaveAsync(new ApplicateSession
        {
            OpenPaths = new List<string> { @"C:\a\one.md", @"C:\a\two.md" },
            ActivePath = @"C:\a\two.md",
            RecentPaths = new List<string> { @"C:\a\two.md", @"C:\a\recent.md" },
        });

        DenyAllAccess(deniedRoot);
        try
        {
            // The API lies: the file is there and populated, and File.Exists reports otherwise.
            Assert.False(File.Exists(Path.Combine(deniedRoot, "applicate-session.json")));
            Assert.Null(await store.LoadAsync());
        }
        finally
        {
            RestoreAccess(deniedRoot);
        }

        // ...and refusing cost nothing: the file the store declined to read was never damaged.
        var recovered = await store.LoadAsync();

        Assert.NotNull(recovered);
        Assert.Equal(new List<string> { @"C:\a\one.md", @"C:\a\two.md" }, recovered.OpenPaths);
        Assert.Equal(@"C:\a\two.md", recovered.ActivePath);
        Assert.Equal(new List<string> { @"C:\a\two.md", @"C:\a\recent.md" }, recovered.RecentPaths);
    }

    /// <summary>
    /// Denies the current user every right on <paramref name="directory"/>, with inheritance broken so
    /// no inherited Allow can survive. Deny FullControl is required: a partial deny (list + read
    /// attributes + traverse) leaves File.Exists returning true, so it would not reproduce the defect.
    /// The owner can always rewrite its own DACL, which is what lets <see cref="RestoreAccess"/> undo
    /// this without elevation.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void DenyAllAccess(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("no user SID on the current identity");
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreAccess(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny)
            {
                security.RemoveAccessRule(rule);
            }
        }

        info.SetAccessControl(security);
    }
}

using System;
using System.IO;
using System.Linq;
using MarkMello.Applicate.Desktop.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

// Guards the validate-before-assign of the WebView2 user-data folder.
//
// The defect being prevented: OnEnvironmentRequested used to assign
// UserDataFolder with no validation, and three confirmed stimuli make
// CoreWebView2 environment creation fail from there — the path exists as a
// FILE, its PARENT exists as a file, or the drive does not exist. Those
// failures do not share a shape: one faults loudly in ~150 ms, the other hangs
// forever with a live browser host process and no record on any channel.
//
// So the assertions below are deliberately split in two directions, and the
// SECOND one is the one that matters most:
//
//  - a bad primary must reach the alternate (the improvement), and
//  - a bad primary with a bad alternate must return the PRIMARY (the
//    ONLY-FORWARD invariant). That case reproduces the old behaviour exactly.
//    A fallback that returned something else there — empty, null, or the
//    unusable alternate — would be a NEW way for startup to fail, which is
//    inadmissible regardless of how well the happy path works.
public sealed class ApplicateWebViewUserDataFolderTests
{
    [Fact]
    public void AUsableFolderIsAssignedUnchanged()
    {
        using var scratch = new ScratchDirectory();
        var primary = scratch.Combine("primary");

        var decision = ApplicateWebMarkdownDocumentView.ResolveWebViewUserDataFolder(
            primary,
            scratch.Combine("alternate"));

        Assert.Equal(ApplicateWebViewUserDataFolderOutcome.Primary, decision.Outcome);
        Assert.Equal(primary, decision.Folder);
    }

    // Stimulus 1: the user-data path already exists as a FILE.
    [Fact]
    public void APrimaryThatExistsAsAFileFallsBackToTheAlternate()
    {
        using var scratch = new ScratchDirectory();
        var primary = scratch.Combine("primary");
        File.WriteAllText(primary, "not a directory");
        var alternate = scratch.Combine("alternate");

        var decision = ApplicateWebMarkdownDocumentView.ResolveWebViewUserDataFolder(primary, alternate);

        Assert.Equal(ApplicateWebViewUserDataFolderOutcome.Alternate, decision.Outcome);
        Assert.Equal(alternate, decision.Folder);
        Assert.Contains("IOException", decision.Detail, StringComparison.Ordinal);
    }

    // Stimulus 2: the PARENT of the user-data path exists as a file.
    [Fact]
    public void APrimaryWhoseParentIsAFileFallsBackToTheAlternate()
    {
        using var scratch = new ScratchDirectory();
        var parent = scratch.Combine("parent");
        File.WriteAllText(parent, "not a directory");
        var primary = Path.Combine(parent, "WebView2");
        var alternate = scratch.Combine("alternate");

        var decision = ApplicateWebMarkdownDocumentView.ResolveWebViewUserDataFolder(primary, alternate);

        Assert.Equal(ApplicateWebViewUserDataFolderOutcome.Alternate, decision.Outcome);
        Assert.Equal(alternate, decision.Folder);
    }

    // Stimulus 3: the drive does not exist.
    //
    // The letter is discovered at RUNTIME rather than hardcoded. A hardcoded
    // letter is exactly how this test would rot into a decoration: during
    // development the obvious pick (Q:) turned out to be a real mapped drive
    // on the machine under test, and the "missing drive" probe quietly
    // succeeded. Asking the OS which letters are free removes the assumption.
    [Fact]
    public void APrimaryOnAMissingDriveFallsBackToTheAlternate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var scratch = new ScratchDirectory();
        var missingDrive = FindUnusedDriveLetter();
        Assert.True(
            missingDrive is not null,
            "Every drive letter A-Z is in use on this machine, so the missing-drive stimulus cannot be staged. "
            + "This fails loudly rather than passing vacuously — free a letter or drop this test.");

        var primary = Path.Combine($"{missingDrive}:\\", "MarkMello", "Applicate", "WebView2");
        var alternate = scratch.Combine("alternate");

        var decision = ApplicateWebMarkdownDocumentView.ResolveWebViewUserDataFolder(primary, alternate);

        Assert.Equal(ApplicateWebViewUserDataFolderOutcome.Alternate, decision.Outcome);
        Assert.Equal(alternate, decision.Folder);
    }

    // THE ONLY-FORWARD INVARIANT. When neither candidate is usable the caller
    // must receive the PRIMARY, because that is precisely what the previous
    // unconditional `windows.UserDataFolder = GetWebViewUserDataFolder()`
    // assigned. Returning anything else here would be a backward step: it
    // would introduce a startup failure mode that did not exist before.
    [Fact]
    public void WhenNeitherCandidateIsUsableThePrimaryIsReturnedUnchanged()
    {
        using var scratch = new ScratchDirectory();
        var primary = scratch.Combine("primary");
        var alternate = scratch.Combine("alternate");
        File.WriteAllText(primary, "not a directory");
        File.WriteAllText(alternate, "not a directory either");

        var decision = ApplicateWebMarkdownDocumentView.ResolveWebViewUserDataFolder(primary, alternate);

        Assert.Equal(ApplicateWebViewUserDataFolderOutcome.NoUsableCandidate, decision.Outcome);
        Assert.Equal(primary, decision.Folder);
        Assert.Contains("primary=", decision.Detail, StringComparison.Ordinal);
        Assert.Contains("alternate=", decision.Detail, StringComparison.Ordinal);
    }

    // Guards the cache trap. Avalonia's CoreWebView2Environment keeps a
    // process-wide dictionary keyed by an options tuple whose equality includes
    // UserDataFolder, so a faulted or hung entry is cached for the life of the
    // process and re-offering the SAME folder is a guaranteed no-op. An
    // alternate equal to the primary would therefore do nothing at all while
    // still reporting Outcome.Alternate — a fallback that reads as working and
    // is not. The production pair must differ, including in the branch where
    // LocalApplicationData is empty and the primary is already temp-rooted.
    [Fact]
    public void TheProductionAlternateIsADifferentFolderFromTheProductionPrimary()
    {
        Assert.NotEqual(
            ApplicateWebMarkdownDocumentView.GetWebViewUserDataFolder(),
            ApplicateWebMarkdownDocumentView.GetAlternateWebViewUserDataFolder());

        // The empty-LocalApplicationData branch: the primary collapses onto the
        // temp path, which is also where the alternate lives. Reconstructing
        // that branch's value here proves the distinct leaf name — not the
        // distinct ROOT — is what keeps them apart.
        var primaryWithoutLocalAppData = Path.Combine(
            Path.GetTempPath(), "MarkMello", "Applicate", "WebView2");
        Assert.NotEqual(
            primaryWithoutLocalAppData,
            ApplicateWebMarkdownDocumentView.GetAlternateWebViewUserDataFolder());
    }

    // Trash hygiene: the write probe must not survive a successful validation.
    [Fact]
    public void TheWriteProbeLeavesNothingBehindInAValidatedFolder()
    {
        using var scratch = new ScratchDirectory();
        var folder = scratch.Combine("primary");

        Assert.True(ApplicateWebMarkdownDocumentView.TryPrepareWebViewUserDataFolder(folder, out var failure));
        Assert.Equal(string.Empty, failure);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    // The caller is an event handler on the startup path, so the contract is
    // "never throws" — not "throws only the three types we measured". These
    // inputs are outside the measured set on purpose.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0invalid")]
    [InlineData("::::")]
    public void PreparationReportsFailureInsteadOfThrowingOnAMalformedPath(string folder)
    {
        var prepared = ApplicateWebMarkdownDocumentView.TryPrepareWebViewUserDataFolder(folder, out var failure);

        Assert.False(prepared);
        Assert.NotEqual(string.Empty, failure);
    }

    private static char? FindUnusedDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();

        // Descending: the high letters are the least likely to be handed to a
        // newly-mounted volume while the test runs.
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter) && !Directory.Exists($"{letter}:\\"))
            {
                return letter;
            }
        }

        return null;
    }

    private sealed class ScratchDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"applicate-userdata-tests-{Guid.NewGuid():N}");

        public ScratchDirectory() => Directory.CreateDirectory(_root);

        public string Combine(string leaf) => Path.Combine(_root, leaf);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

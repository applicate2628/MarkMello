using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Application.Diagnostics;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

// Coverage for EarlyDocumentCache, which had none: two producers deposit into
// it (Program.Main's argv pre-read and its session-startup pre-read) and two
// consumers read from it (MainWindowViewModel.LoadDocumentAsync and
// OpenDocumentsService.EnsureLoadedAsync), and nothing exercised the contract
// they all depend on.
//
// The defect these tests were written for:
// work-items/bugs/2026-07-26-early-document-cache-outlives-a-rejected-session-read.md
// -- a deposited entry had no validity condition, so a document replaced during
// the ~2.3 s between the startup pre-read and the load that consumes it was
// still published as a successful load.
//
// The producers themselves are NOT covered here. Both are private static
// methods on Program that construct `new JsonApplicateSessionStore()` against
// the real %AppData%, and APPDATA redirection was measured not to affect
// Environment.GetFolderPath(ApplicationData) on .NET 10 -- driving them would
// mean writing to the developer's real session file.
public sealed class EarlyDocumentCacheTests : IDisposable
{
    // Same byte count by construction ('A' occurs exactly once): the guard must
    // not be rescued by a length difference here, or the same-length change --
    // the case where the timestamp is the only discriminator -- would go
    // untested.
    private const string StaleContent = "# CONTENT-A: the bytes the startup pre-read deposited";
    private static readonly string FreshSameLengthContent = StaleContent.Replace('A', 'B');
    private static readonly string FreshLongerContent = FreshSameLengthContent + " ...and then the file also grew";

    private readonly string _tempRoot;
    private readonly List<string> _depositedKeys = new();

    public EarlyDocumentCacheTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "MarkMello.Applicate.Tests.EarlyDocCache",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        // The cache is a process-wide static. Drain anything a test left behind
        // so it cannot outlive this test, even though every key here is unique.
        foreach (var key in _depositedKeys)
        {
            EarlyDocumentCache.TryConsume(key, out _);
        }

        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup
        }
    }

    // ---- the defect ------------------------------------------------------

    [Fact]
    public void AFileReplacedAfterThePreReadIsNotServedFromTheStaleEntry()
    {
        var path = WriteFile("replaced-same-length.md", StaleContent);
        DepositAsTheProducerDoes(path);

        RewriteWithAnObservablyNewTimestamp(path, FreshSameLengthContent);

        Assert.Equal(FreshSameLengthContent, ContentTheConsumerWouldSee(path));
    }

    [Fact]
    public void AFileReplacedWithADifferentLengthIsNotServedFromTheStaleEntry()
    {
        var path = WriteFile("replaced-longer.md", StaleContent);
        DepositAsTheProducerDoes(path);

        RewriteWithAnObservablyNewTimestamp(path, FreshLongerContent);

        Assert.Equal(FreshLongerContent, ContentTheConsumerWouldSee(path));
    }

    [Fact]
    public void AFileReplacedWithinOneTimestampTickIsStillCaughtByItsLength()
    {
        // The part of the window a timestamp cannot cover, made deterministic.
        // Last-write-time was measured to advance in ~0.3 ms steps on this
        // project's NTFS targets, so a fast enough replacement can still carry
        // the timestamp it had at deposit. Forcing the timestamp back
        // reproduces that collision exactly instead of racing for it, and
        // length is the discriminator that has to catch it.
        var path = WriteFile("same-timestamp.md", StaleContent);
        DepositAsTheProducerDoes(path);
        var timestampAtDeposit = File.GetLastWriteTimeUtc(path);

        File.WriteAllText(path, FreshLongerContent);
        File.SetLastWriteTimeUtc(path, timestampAtDeposit);
        Assert.Equal(timestampAtDeposit, File.GetLastWriteTimeUtc(path));

        Assert.Equal(FreshLongerContent, ContentTheConsumerWouldSee(path));
    }

    [Fact]
    public void AStaleEntryIsDiscardedRatherThanLeftToWinALaterLoad()
    {
        // The bug report's P5 shape: a deposit nothing consumed must not sit in
        // the cache waiting to beat fresh disk content at some later load.
        var path = WriteFile("stale-does-not-linger.md", StaleContent);
        DepositAsTheProducerDoes(path);
        RewriteWithAnObservablyNewTimestamp(path, FreshSameLengthContent);

        _ = ContentTheConsumerWouldSee(path);

        Assert.Equal(FreshSameLengthContent, ContentTheConsumerWouldSee(path));
    }

    [Fact]
    public void AFileDeletedAfterThePreReadIsNotServedFromTheStaleEntry()
    {
        var path = WriteFile("deleted.md", StaleContent);
        DepositAsTheProducerDoes(path);

        File.Delete(path);

        Assert.False(
            EarlyDocumentCache.TryConsume(path, out _),
            "a document deleted after the pre-read was still served from the cache, so the user sees a file " +
            "that no longer exists instead of the load error the disk read would have produced");
    }

    [Fact]
    public void BytesWhoseFileCannotBeIdentifiedAreNotCachedAtAll()
    {
        // Fail closed. Bytes whose file cannot be identified at deposit can
        // never be shown to still match it at consume, so caching them could
        // only ever serve them unverified.
        var path = Path.Combine(_tempRoot, "never-created.md");
        _depositedKeys.Add(path);

        var deposited = EarlyDocumentCache.Deposit(path, SourceFor(path, StaleContent));

        Assert.False(deposited, "the cache accepted bytes for a file it could not identify");
        Assert.False(EarlyDocumentCache.TryConsume(path, out _), "unverifiable bytes were served anyway");
    }

    // ---- the optimisation the guard must not cost ------------------------

    [Fact]
    public void AnUnchangedFileStillServesTheDepositedBytesWithoutTouchingDisk()
    {
        // The deposited bytes deliberately differ from the file's, so a value
        // that came from a disk read instead of the cache is visible: this is
        // what fails if the guard is "fixed" into always missing, which would
        // silently delete the startup-latency optimisation.
        var path = WriteFile("unchanged.md", "# ON-DISK: what a fallback read would return");
        var cacheOnlyBytes = "# FROM-CACHE: what only a cache hit can return";
        _depositedKeys.Add(path);
        Assert.True(EarlyDocumentCache.Deposit(path, SourceFor(path, cacheOnlyBytes)));

        Assert.Equal(cacheOnlyBytes, ContentTheConsumerWouldSee(path));
    }

    [Fact]
    public void ReadingTheFileDoesNotItselfInvalidateTheEntry()
    {
        // The guard rests on reads leaving last-write-time alone. If they did
        // not, every consume would miss and the cache would be dead weight
        // while still looking healthy.
        var path = WriteFile("read-does-not-disturb.md", StaleContent);
        DepositAsTheProducerDoes(path);

        _ = File.ReadAllText(path);
        _ = File.ReadAllText(path);

        Assert.True(
            EarlyDocumentCache.TryConsume(path, out _),
            "merely reading the file invalidated the cache entry, so the pre-read optimisation never pays off");
    }

    // ---- pre-existing contract that must survive -------------------------

    [Fact]
    public void ConsumeIsOneShot()
    {
        var path = WriteFile("one-shot.md", StaleContent);
        DepositAsTheProducerDoes(path);

        Assert.True(EarlyDocumentCache.TryConsume(path, out _));
        Assert.False(EarlyDocumentCache.TryConsume(path, out _), "the entry survived the load that consumed it");
    }

    [Fact]
    public void APathNeverDepositedIsAMiss()
    {
        var path = WriteFile("never-deposited.md", StaleContent);

        Assert.False(EarlyDocumentCache.TryConsume(path, out _));
    }

    [Fact]
    public void ANonCanonicalLookupPathStillFindsAndValidatesTheEntry()
    {
        // Keying canonicalizes through Path.GetFullPath so an argv-relative
        // path still hits an absolute-key deposit. The identity check has to
        // run against that same canonical key, or it would fail on a path
        // shape that used to be a hit.
        var path = WriteFile("canonical.md", StaleContent);
        DepositAsTheProducerDoes(path);

        var awkward = Path.Combine(_tempRoot, ".", "sub", "..", "canonical.md");

        Assert.True(
            EarlyDocumentCache.TryConsume(awkward, out var cached),
            "a non-canonical spelling of the deposited path no longer hits the entry");
        Assert.Equal(StaleContent, cached!.Content);
    }

    // ---- the OpenDocumentsService consumer, end to end -------------------

    [Fact]
    public async Task EnsureLoadedAsyncReadsFreshContentWhenTheFileChangedAfterThePreRead()
    {
        var path = WriteFile("ensure-loaded-changed.md", StaleContent);
        DepositAsTheProducerDoes(path);
        RewriteWithAnObservablyNewTimestamp(path, FreshSameLengthContent);

        using var service = new OpenDocumentsService();
        var document = await service.OpenStubAsync(path);
        await service.EnsureLoadedAsync(document);

        Assert.Equal(FreshSameLengthContent, document.SourceText);
    }

    [Fact]
    public async Task EnsureLoadedAsyncStillUsesTheCacheWhenTheFileIsUnchanged()
    {
        var path = WriteFile("ensure-loaded-unchanged.md", "# ON-DISK: what a fallback read would return");
        var cacheOnlyBytes = "# FROM-CACHE: what only a cache hit can return";
        _depositedKeys.Add(path);
        Assert.True(EarlyDocumentCache.Deposit(path, SourceFor(path, cacheOnlyBytes)));

        using var service = new OpenDocumentsService();
        var document = await service.OpenStubAsync(path);
        await service.EnsureLoadedAsync(document);

        Assert.Equal(cacheOnlyBytes, document.SourceText);
    }

    // ---- helpers ---------------------------------------------------------

    // What either consumer ends up with: the cached bytes on a hit, the file's
    // current bytes otherwise. Asserting on THIS makes a failure read as the
    // harm -- the user was shown the superseded document -- instead of as a
    // mechanism detail about a bool.
    private static string ContentTheConsumerWouldSee(string path)
        => EarlyDocumentCache.TryConsume(path, out var cached) && cached is not null
            ? cached.Content
            : File.ReadAllText(path);

    // Mirrors both real producers: read the file, then deposit what was read.
    private void DepositAsTheProducerDoes(string path)
    {
        var canonical = Path.GetFullPath(path);
        _depositedKeys.Add(canonical);
        var content = File.ReadAllText(canonical);
        Assert.True(
            EarlyDocumentCache.Deposit(canonical, SourceFor(canonical, content)),
            "the fixture could not deposit, so the test that follows would pass without exercising anything");
    }

    private static MarkdownSource SourceFor(string path, string content)
        => new(Path: path, FileName: Path.GetFileName(path), Content: content);

    private string WriteFile(string fileName, string contents)
    {
        var path = Path.Combine(_tempRoot, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    // Rewrites the file and does not return until the filesystem reports a
    // last-write-time that differs from the one before the rewrite.
    //
    // Measured on this project's NTFS targets: last-write-time advances in
    // ~0.3 ms steps, and 36-78% of back-to-back same-length write pairs
    // therefore share a single timestamp. A test that just rewrote the file
    // would be asserting on a change the filesystem cannot report, and would
    // fail for a reason that has nothing to do with the cache. One millisecond
    // of separation was enough for 0/60 collisions on every volume measured;
    // this loop waits for the observable fact instead of assuming it.
    private static void RewriteWithAnObservablyNewTimestamp(string path, string contents)
    {
        var before = File.GetLastWriteTimeUtc(path);

        for (var attempt = 0; attempt < 500; attempt++)
        {
            File.WriteAllText(path, contents);
            if (File.GetLastWriteTimeUtc(path) != before)
            {
                return;
            }

            Thread.Sleep(1);
        }

        throw new InvalidOperationException(
            $"The filesystem never reported a new last-write-time for '{path}' after 500 rewrites, so this test " +
            "cannot distinguish a changed file from an unchanged one.");
    }
}

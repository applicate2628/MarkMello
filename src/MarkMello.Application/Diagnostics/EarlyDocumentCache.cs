using System.Collections.Concurrent;
using MarkMello.Domain;

namespace MarkMello.Application.Diagnostics;

/// <summary>
/// Process-lifetime cache that holds the result of an active document
/// pre-read kicked off on the thread pool by <c>Program.Main</c> right
/// after <c>singleInstance.StartListening()</c> (PE r2 §2 item D —
/// "Parallelize active-document I/O with shell load"). The view model's
/// <c>LoadDocumentAsync</c> consumes the cache via
/// <see cref="TryConsume(string, out MarkdownSource)"/> before falling
/// back to the regular <see cref="Application.UseCases.OpenDocumentUseCase"/>
/// path; on a hit the file read + parse cost (~150-250 ms per PE r2 §1 P2)
/// overlaps the Avalonia init / window-open critical path instead of
/// running serially after it.
///
/// <para>Thread safety: writers are arbitrary thread-pool tasks; the
/// reader is the UI thread inside <c>MainWindowViewModel.LoadDocumentAsync</c>.
/// A <see cref="ConcurrentDictionary{TKey, TValue}"/> covers both sides
/// without explicit locking.</para>
///
/// <para>Lifetime: static, by design. The rendezvous crosses two
/// composition contexts that share no instance — a thread-pool task started
/// by <c>Program.Main</c> deposits, a view model resolved through DI
/// consumes — so a process-singleton is the shape that cannot end up
/// half-wired. See Layering below for why not an injected port. The cache
/// holds at most one entry per launch, for the first activation
/// document.</para>
///
/// <para>Keying: absolute path via <see cref="Path.GetFullPath(string)"/>
/// on both deposit and consume — matches the canonicalization already
/// applied by <c>CommandLineActivation</c> and <c>FileDocumentLoader</c>,
/// avoiding argv-relative vs absolute-path miss when the OS launches the
/// app with a relative path.</para>
///
/// <para>Validity: an entry is served only while the file it was read from
/// still looks like the same file. Every deposit records that file's
/// identity — last-write-time UTC plus length — and
/// <see cref="TryConsume(string, out MarkdownSource)"/> re-reads the
/// identity and compares before handing the bytes over; on a mismatch the
/// entry is dropped and the caller falls through to its ordinary read.
/// Without this an entry recorded only "these bytes were read for path P"
/// with nothing tying it to the file still being unchanged, so a document
/// replaced after the pre-read (another editor, a sync client, a build
/// step) was still published as a successful load. The window between
/// deposit and consume measured ~2.3 s on a real launch. Both deposit paths
/// carried the defect — not concurrently (they are mutually exclusive per
/// launch) but identically, because the invariant belongs to the cache and
/// neither call site owned it
/// (work-items/bugs/2026-07-26-early-document-cache-outlives-a-rejected-session-read.md).</para>
///
/// <para>Residual: last-write-time on this project's NTFS targets was
/// measured to advance in ~0.3 ms steps, and two same-length writes less
/// than ~1 ms apart can therefore share one timestamp. A replacement that
/// lands within about a millisecond of the deposit AND preserves the byte
/// count can still be served stale; everything from a millisecond out to
/// the full ~2.3 s window cannot. Closing the sub-millisecond remainder
/// would need content hashing at consume, which re-reads the file and so
/// costs exactly the optimisation this cache exists to provide.</para>
///
/// <para>Layering: this is the only type in <c>MarkMello.Application</c>
/// that reaches the disk, and the placement is deliberate. The invariant
/// "these bytes still match that file" needs exactly one owner: two
/// independent code paths deposit (<c>Program.Main</c>'s argv pre-read and
/// its session-startup pre-read) and two independent consumers read
/// (<c>MainWindowViewModel.LoadDocumentAsync</c> and
/// <c>OpenDocumentsService.EnsureLoadedAsync</c>), so enforcing it at the
/// call sites would make it a rule four sites must each remember rather
/// than a property of the cache. That the exception stays a single file is
/// enforced by <c>ApplicationLayerDiskAccessDisciplineTests</c>, not left to
/// review.</para>
///
/// <para>Why the disk access is not behind an injected filesystem port: NOT
/// because no service provider is reachable. One is — <c>Program.Main</c>
/// builds the provider before either pre-read and hands it to both, and the
/// task that deposits goes on to resolve four services from it. Three
/// reasons that do survive:</para>
///
/// <para>(1) Reference graph, compiler-enforced.
/// <c>MarkMello.Presentation</c> references only <c>MarkMello.Application</c>
/// and <c>MarkMello.Domain</c>. <c>MarkMello.Infrastructure</c> — the layer
/// that already owns file I/O — references Application, so it sits ABOVE it
/// and is invisible to the Presentation-side consumer. <c>MarkMello.Domain</c>
/// has no project references at all and is the wrong home for disk access.
/// Application is the only layer both consumers can see.</para>
///
/// <para>(2) A port would be null in the exact consumer this guard was
/// filed against. Desktop-injected services on the view model are null in
/// test contexts that construct it directly without the desktop DI graph
/// (see <c>MainWindowViewModel</c>'s <c>_rendererReadiness</c> fallback). An
/// injected probe inherits that, and null would have to mean either
/// fail-closed (the optimisation silently disappears wherever wiring is
/// thin) or fail-open (the guard is vacuous and the staleness bug returns
/// silently). Owning the probe here has neither branch: it cannot be
/// unwired, stubbed, or forgotten.</para>
///
/// <para>(3) A fakeable port would let the tests assert against a fiction.
/// The Residual bound above rests on MEASURED timestamp granularity on
/// real NTFS; that measurement is the only evidence this guard is not
/// vacuous, and a fake filesystem with idealised timestamps would erase
/// it.</para>
///
/// <para>Exception handling: deposit failures (I/O, parse) are the
/// thread-pool task's responsibility. If a deposit never lands, or lands
/// but cannot observe the file's identity and so returns
/// <see langword="false"/>, the cache stays empty for that key and
/// <see cref="TryConsume"/> returns false, so the VM falls through to the
/// normal load path which has its own typed-error handling via
/// <see cref="Application.UseCases.OpenDocumentResult"/>.</para>
///
/// <para>Consume is one-shot: once a key is consumed it is removed so a
/// later reload (user pressing reload after disk edits) goes through
/// the regular path and picks up the fresh disk content. Multi-tab
/// session-restore (future) deposits one entry per tab and each tab's
/// first load consumes its own entry.</para>
/// </summary>
public static class EarlyDocumentCache
{
    private static readonly ConcurrentDictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deposit a pre-read document into the cache. Called from a
    /// thread-pool task in <c>Program.Main</c>. Subsequent
    /// <see cref="TryConsume"/> calls keyed by the same canonicalized
    /// absolute path return <see langword="true"/> with this source, for
    /// as long as the file still carries the identity captured here.
    /// </summary>
    /// <param name="path">Absolute path; canonicalized via
    /// <see cref="Path.GetFullPath(string)"/> before insertion.</param>
    /// <param name="source">The pre-read markdown source.</param>
    /// <returns><see langword="true"/> when the source was cached;
    /// <see langword="false"/> when the file's identity could not be
    /// observed, in which case nothing is cached and the consumer's
    /// ordinary read path runs unchanged. Callers that trace startup should
    /// record the <see langword="false"/> case: it is the only signal that
    /// the pre-read ran but bought nothing.</returns>
    public static bool Deposit(string path, MarkdownSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(source);

        var key = Path.GetFullPath(path);

        // Fail closed. Bytes whose file cannot be identified now can never be
        // shown to still match it later, so caching them could only ever
        // serve them unverified.
        if (!FileIdentity.TryCapture(key, out var identity))
        {
            return false;
        }

        Entries[key] = new Entry(source, identity);
        return true;
    }

    /// <summary>
    /// Try to consume a pre-deposited document. On a hit, returns
    /// <see langword="true"/>, removes the entry from the cache, and
    /// emits the source via <paramref name="source"/>. On a miss,
    /// returns <see langword="false"/> and the caller falls through to
    /// its normal load path.
    ///
    /// <para>A deposited entry whose file no longer carries the identity it
    /// had at deposit is a miss: the entry is discarded and never offered
    /// again, so the caller reads current content from disk.</para>
    /// </summary>
    /// <param name="path">Path to look up; canonicalized via
    /// <see cref="Path.GetFullPath(string)"/> before lookup so callers
    /// that received a relative argv path still hit the deposited
    /// absolute-key entry.</param>
    /// <param name="source">When the method returns <see langword="true"/>,
    /// contains the cached source. Otherwise <see langword="null"/>.</param>
    public static bool TryConsume(string path, out MarkdownSource? source)
    {
        source = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var key = Path.GetFullPath(path);
        if (!Entries.TryRemove(key, out var entry))
        {
            return false;
        }

        // Removed above whatever the outcome: an entry that failed this check
        // is stale for good, and leaving it in place would only let a later
        // load serve the same superseded bytes.
        if (!FileIdentity.TryCapture(key, out var current) || current != entry.Identity)
        {
            return false;
        }

        source = entry.Source;
        return true;
    }

    private sealed record Entry(MarkdownSource Source, FileIdentity Identity);

    /// <summary>
    /// What the cache compares to decide whether a deposited entry still
    /// describes the file on disk.
    /// </summary>
    private readonly record struct FileIdentity(long LastWriteUtcTicks, long Length)
    {
        public static bool TryCapture(string fullPath, out FileIdentity identity)
        {
            try
            {
                // One FileInfo is one metadata snapshot: it caches the OS
                // attribute block on first access, so Exists / LastWriteTimeUtc
                // / Length below all read the SAME snapshot and cannot
                // disagree with one another. Measured at 2.9-4.6 us on this
                // project's NTFS targets — under 1% of the file read it
                // guards, and it opens no handle.
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    identity = default;
                    return false;
                }

                identity = new FileIdentity(info.LastWriteTimeUtc.Ticks, info.Length);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or System.Security.SecurityException)
            {
                // An identity that cannot be observed is not an identity that
                // matches. Both callers of this treat false as "no usable
                // cache entry" and take their ordinary read path, which owns
                // the typed error handling for a genuinely unreadable file.
                identity = default;
                return false;
            }
        }
    }
}

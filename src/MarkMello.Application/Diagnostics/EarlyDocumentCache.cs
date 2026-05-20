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
/// <para>Lifetime: static, by design. The cache is a process-singleton
/// because it must be visible to <c>Program.Main</c> (which has no
/// service provider yet at line 49) and to a Presentation-layer view
/// model that resolves through DI. A static is the simplest reliable
/// shape; the cache holds at most one entry per launch for the first
/// activation document.</para>
///
/// <para>Keying: absolute path via <see cref="Path.GetFullPath(string)"/>
/// on both deposit and consume — matches the canonicalization already
/// applied by <c>CommandLineActivation</c> and <c>FileDocumentLoader</c>,
/// avoiding argv-relative vs absolute-path miss when the OS launches the
/// app with a relative path.</para>
///
/// <para>Exception handling: deposit failures (I/O, parse) are the
/// thread-pool task's responsibility. If a deposit never lands, the
/// cache stays empty for that key and <see cref="TryConsume"/> returns
/// false, so the VM falls through to the normal load path which has its
/// own typed-error handling via <see cref="Application.UseCases.OpenDocumentResult"/>.</para>
///
/// <para>Consume is one-shot: once a key is consumed it is removed so a
/// later reload (user pressing reload after disk edits) goes through
/// the regular path and picks up the fresh disk content. Multi-tab
/// session-restore (future) deposits one entry per tab and each tab's
/// first load consumes its own entry.</para>
/// </summary>
public static class EarlyDocumentCache
{
    private static readonly ConcurrentDictionary<string, MarkdownSource> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deposit a pre-read document into the cache. Called from a
    /// thread-pool task in <c>Program.Main</c>. Subsequent
    /// <see cref="TryConsume"/> calls keyed by the same canonicalized
    /// absolute path return <see langword="true"/> with this source.
    /// </summary>
    /// <param name="path">Absolute path; canonicalized via
    /// <see cref="Path.GetFullPath(string)"/> before insertion.</param>
    /// <param name="source">The pre-read markdown source.</param>
    public static void Deposit(string path, MarkdownSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(source);

        var key = Path.GetFullPath(path);
        Entries[key] = source;
    }

    /// <summary>
    /// Try to consume a pre-deposited document. On a hit, returns
    /// <see langword="true"/>, removes the entry from the cache, and
    /// emits the source via <paramref name="source"/>. On a miss,
    /// returns <see langword="false"/> and the caller falls through to
    /// its normal load path.
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
        return Entries.TryRemove(key, out source);
    }
}

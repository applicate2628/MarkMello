using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkMello.Applicate.Desktop.Editing;

namespace MarkMello.Applicate.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="IApplicateSessionStore"/> double that hands out a PRE-QUEUED SEQUENCE of
/// load results, so a test can make the Nth window-side read disagree with the (N-1)th. That
/// disagreement is the whole subject of
/// <c>work-items/bugs/2026-07-26-reveal-gate-held-for-a-document-that-never-arrives.md</c>: one launch
/// performs several independent reads of one file and nothing reconciles them.
///
/// <para>No mocking library is used — the required behaviour is a short state machine, and the
/// hand-written form makes the d13 provenance explicit at the point it is enforced. Hand-written
/// doubles are the established practice in this project (see the shell/signal/timer/cover/document
/// doubles in <c>ApplicateAirspaceCompositorTests</c>).</para>
///
/// <para><b>d13 guard.</b> A <c>null</c> load result means "the persisted state could not be
/// OBSERVED" — never "empty". Once a consumer has consumed such a null, any subsequent
/// <see cref="SaveAsync"/> would be a write over an unobserved baseline, i.e. exactly the data-loss
/// this contract exists to prevent. This double does not throw on that (the production call is
/// fire-and-forget — <c>_ = store.SaveAsync(...).AsTask()</c> — so a throw would be swallowed and the
/// guard would silently never fire). It RECORDS the violation instead, in
/// <see cref="SavesOverUnobservedBaseline"/>, which a test asserts is zero.</para>
/// </summary>
internal sealed class SequentialApplicateSessionStore : IApplicateSessionStore
{
    private readonly Queue<ApplicateSession?> _results;
    private readonly List<ApplicateSession> _savedSessions = new();

    public SequentialApplicateSessionStore(params ApplicateSession?[] results)
    {
        _results = new Queue<ApplicateSession?>(results);
    }

    /// <summary>How many times a consumer physically asked the store to load.</summary>
    public int LoadCallCount { get; private set; }

    /// <summary>How many times a consumer asked the store to persist.</summary>
    public int SaveCallCount { get; private set; }

    /// <summary>
    /// Queued results NOT yet handed out. A test asserts this is non-zero to prove a contradictory
    /// second read was never physically performed.
    /// </summary>
    public int RemainingResults => _results.Count;

    /// <summary>True once a caller has consumed a <c>null</c> (unobserved) load result.</summary>
    public bool ConsumedUnobservedResult { get; private set; }

    /// <summary>
    /// d13 violations: saves that occurred AFTER an unobserved (null) observation was consumed. Must
    /// be zero — the composer is required to refuse to compose over an unobserved baseline.
    /// </summary>
    public int SavesOverUnobservedBaseline { get; private set; }

    /// <summary>Sessions handed to <see cref="SaveAsync"/>, in call order.</summary>
    public IReadOnlyList<ApplicateSession> SavedSessions => _savedSessions;

    public ValueTask<ApplicateSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCallCount++;
        if (_results.Count == 0)
        {
            // Deliberately NOT a silent fallback to Empty/null: the queue running dry means the code
            // under test performed MORE reads than the test described, which is itself the defect
            // class under investigation. Surface it instead of absorbing it.
            throw new System.InvalidOperationException(
                $"SequentialApplicateSessionStore exhausted: load #{LoadCallCount} was requested but "
                + "no result was queued for it. The code under test performed more session reads than "
                + "this test enumerated.");
        }

        var result = _results.Dequeue();
        if (result is null)
        {
            ConsumedUnobservedResult = true;
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask SaveAsync(ApplicateSession session, CancellationToken cancellationToken = default)
    {
        SaveCallCount++;
        if (ConsumedUnobservedResult)
        {
            SavesOverUnobservedBaseline++;
        }

        _savedSessions.Add(session);
        return ValueTask.CompletedTask;
    }
}

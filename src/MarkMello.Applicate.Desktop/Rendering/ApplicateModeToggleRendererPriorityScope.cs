using System;
using System.Collections.Generic;
using MarkMello.Applicate.Desktop.Diagnostics;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// The single logical writer of process-priority state for the mode-toggle
/// priority-boost WORKAROUND. One compositor-owned instance is shared by the
/// mode reveal session (which admits/replaces generations and owns every
/// terminal close) and each host reveal session (which requests the post-hide
/// bump or event-driven re-bump). A stateless native adapter, reachable only
/// through this writer, performs discovery and the priority calls.
///
/// <para>WORKAROUND, not a fix. Root cause (unfixed): Chromium teardown/demotion
/// of the hidden renderer to the Idle priority class under heavy ambient CPU
/// load. This scope bumps the app's renderer children to AboveNormal for the
/// bounded mode-toggle reveal-wait window and restores every surviving renderer
/// to Normal on EVERY exit path. The load-bearing safety invariant is: never
/// leave a renderer permanently bumped.</para>
///
/// <para>No timer, poll, watchdog, or periodic re-raise. The initial bump holds
/// through the reveal-wait (runtime-proven 332/332 samples); only visibility
/// (hide) events re-demote, so re-bumps are strictly event-driven off known
/// compositor hide callbacks.</para>
///
/// <para>UI-thread affine (every operation originates on the Avalonia
/// dispatcher). A monitor lock guards the state machine defensively; all public
/// operations are non-throwing into the reveal pipeline.</para>
/// </summary>
internal interface IApplicateModeTogglePriorityScope : IDisposable
{
    /// <summary>Record a positive current generation, closing any different
    /// existing generation first. Called at positive-generation admission,
    /// before outgoing suppression.</summary>
    void Arm(long generation, ApplicateMode target);

    /// <summary>Initial elevation, after the final transactional hide. Accepts
    /// only the currently armed generation; an already-elevated same generation
    /// is re-bumped idempotently.</summary>
    void BeginAfterFinalHide(long generation, int? browserProcessId);

    /// <summary>Event-driven idempotent re-bump for a known compositor hide.
    /// Runs only when the current generation is already elevated; never changes
    /// the generation or terminal lifetime.</summary>
    void ReapplyAfterKnownHide(string trigger, int? browserProcessId);

    /// <summary>Terminal close of the given generation: restore every surviving
    /// boosted renderer to Normal, close every handle, clear the generation, and
    /// emit the restored/committed event. A stale generation is inert.</summary>
    void Close(long generation, string reason);

    /// <summary>Unconditional close backstop for mode-session / compositor
    /// disposal. Closes whatever generation is current.</summary>
    void CloseAny(string reason);
}

/// <summary>Opaque, retained per-renderer lease. The native adapter creates it
/// and owns the underlying handle (a <c>SafeProcessHandle</c> in production).
/// The scope holds it and passes it back for elevate/restore/close so that
/// restoration always uses the retained handle and PID reuse can never redirect
/// a priority call to an unrelated process.</summary>
internal sealed class RendererPriorityLease
{
    public RendererPriorityLease(int processId, int? capturedPriorityClass, object handle)
    {
        ProcessId = processId;
        CapturedPriorityClass = capturedPriorityClass;
        Handle = handle;
    }

    public int ProcessId { get; }

    public int? CapturedPriorityClass { get; }

    /// <summary>The retained handle object. Production: a SafeProcessHandle.
    /// Tests: an opaque token. The scope never interprets it.</summary>
    internal object Handle { get; }

    /// <summary>Set true once AboveNormal has been successfully applied. Only
    /// elevated leases are restored on close; every lease is always handle-closed.</summary>
    internal bool ElevatedAboveNormal { get; set; }
}

internal enum RendererPriorityDiscoveryStatus
{
    Success,
    NoMatch,
    Unsupported,
    Failed,
}

/// <summary>Result of resolving + opening the complete direct <c>--type=renderer</c>
/// child set of a browser process. On any failure the native adapter has already
/// closed any partially-opened handles (atomic); <see cref="Leases"/> is empty
/// unless <see cref="Status"/> is <see cref="RendererPriorityDiscoveryStatus.Success"/>.</summary>
internal sealed class RendererPriorityDiscovery
{
    public RendererPriorityDiscoveryStatus Status { get; init; }

    public IReadOnlyList<RendererPriorityLease> Leases { get; init; } = Array.Empty<RendererPriorityLease>();

    public string FailedOperation { get; init; } = string.Empty;

    public int NativeErrorCode { get; init; }

    public static readonly RendererPriorityDiscovery NoMatch =
        new() { Status = RendererPriorityDiscoveryStatus.NoMatch };

    public static readonly RendererPriorityDiscovery Unsupported =
        new() { Status = RendererPriorityDiscoveryStatus.Unsupported };

    public static RendererPriorityDiscovery Fail(string operation, int nativeErrorCode)
        => new()
        {
            Status = RendererPriorityDiscoveryStatus.Failed,
            FailedOperation = operation,
            NativeErrorCode = nativeErrorCode,
        };

    public static RendererPriorityDiscovery Success(IReadOnlyList<RendererPriorityLease> leases)
        => new() { Status = RendererPriorityDiscoveryStatus.Success, Leases = leases };
}

internal enum RendererPriorityRestoreOutcome
{
    Restored,
    Exited,
    Failed,
}

internal readonly record struct RendererPriorityRestoreResult(
    RendererPriorityRestoreOutcome Outcome,
    string FailedOperation,
    int NativeErrorCode)
{
    public static readonly RendererPriorityRestoreResult Restored =
        new(RendererPriorityRestoreOutcome.Restored, string.Empty, 0);

    public static readonly RendererPriorityRestoreResult Exited =
        new(RendererPriorityRestoreOutcome.Exited, string.Empty, 0);

    public static RendererPriorityRestoreResult Fail(string operation, int nativeErrorCode)
        => new(RendererPriorityRestoreOutcome.Failed, operation, nativeErrorCode);
}

/// <summary>The stateless native boundary. Reachable only through the writer-owner.
/// Production impl: <see cref="WindowsWebViewRendererPriorityNative"/>. No WMI/CIM,
/// no polling, no background watcher.</summary>
internal interface IApplicateRendererPriorityNative
{
    /// <summary>Snapshot + direct-child filter + PEB command-line + exact
    /// <c>--type=renderer</c> token + open each matched renderer (query/set/
    /// synchronize) + capture prior class. Atomic: closes partial handles on
    /// any failure.</summary>
    RendererPriorityDiscovery Discover(int browserProcessId);

    /// <summary>Apply AboveNormal to an opened lease and read back to verify.</summary>
    bool TryApplyAboveNormal(RendererPriorityLease lease);

    /// <summary>Restore an opened lease to Normal and read back. An exited
    /// process is terminally safe.</summary>
    RendererPriorityRestoreResult RestoreNormal(RendererPriorityLease lease);

    /// <summary>Close the retained handle. Idempotent.</summary>
    void CloseLease(RendererPriorityLease lease);
}

internal sealed class ApplicateModeToggleRendererPriorityScope : IApplicateModeTogglePriorityScope
{
    internal const string DiagnosticGroup = "mode-toggle-priority";

    /// <summary>Downstream-observable restored/committed event id.</summary>
    internal const string ScopeClosedEvent = "mode-toggle-priority-scope-closed";

    /// <summary>Inert singleton for disabled or unsupported-platform composition.
    /// Performs no PID access, snapshot, PEB read, handle open, priority call,
    /// extra diagnostic, or background work.</summary>
    public static readonly IApplicateModeTogglePriorityScope NoOp = new NoOpScope();

    private enum ScopeState
    {
        Idle,
        Armed,
        Elevated,
        Unbumped,
        Disposed,
    }

    private readonly IApplicateRendererPriorityNative _native;
    private readonly object _sync = new();
    private readonly List<RendererPriorityLease> _leases = [];
    private ScopeState _state = ScopeState.Idle;
    private long _generation;
    private ApplicateMode _target;

    public ApplicateModeToggleRendererPriorityScope(IApplicateRendererPriorityNative native)
        => _native = native ?? throw new ArgumentNullException(nameof(native));

    public void Arm(long generation, ApplicateMode target)
    {
        lock (_sync)
        {
            try
            {
                if (_state == ScopeState.Disposed || generation <= 0)
                {
                    return;
                }

                if (_generation == generation)
                {
                    // Duplicate arm for the same generation is idempotent.
                    return;
                }

                if (_generation > 0 && _state != ScopeState.Idle)
                {
                    // Superseding generation: close the old scope BEFORE recording
                    // the new one so handles from two generations never coexist.
                    var oldGeneration = _generation;
                    var counts = RestoreAndCloseHandlesLocked();
                    _state = ScopeState.Idle;
                    _generation = 0;
                    EmitReplaced(oldGeneration, generation, counts);
                }

                _generation = generation;
                _target = target;
                _state = ScopeState.Armed;
                ApplicateTrace.DiagMs(
                    DiagnosticGroup,
                    "mode-toggle-priority-scope-armed",
                    $"generation={generation} target={target}");
            }
            catch (Exception ex)
            {
                EmitUnexpected("arm", ex);
            }
        }
    }

    public void BeginAfterFinalHide(long generation, int? browserProcessId)
    {
        lock (_sync)
        {
            try
            {
                if (_state == ScopeState.Disposed)
                {
                    return;
                }

                if (generation <= 0 || _generation != generation)
                {
                    EmitSkipped(generation, "stale-generation");
                    return;
                }

                if (_state == ScopeState.Elevated)
                {
                    // Re-entrant final hide for an already-elevated generation.
                    ReapplyLocked("commit-preparing", browserProcessId);
                    return;
                }

                if (_state != ScopeState.Armed)
                {
                    // Unbumped (a prior begin failed / found nothing) or Idle:
                    // do not retry within the same generation.
                    return;
                }

                if (browserProcessId is null)
                {
                    _state = ScopeState.Unbumped;
                    EmitSkipped(generation, "browser-pid-unavailable");
                    return;
                }

                var discovery = _native.Discover(browserProcessId.Value);
                switch (discovery.Status)
                {
                    case RendererPriorityDiscoveryStatus.Unsupported:
                        _state = ScopeState.Unbumped;
                        EmitSkipped(generation, "unsupported-platform");
                        return;
                    case RendererPriorityDiscoveryStatus.NoMatch:
                        _state = ScopeState.Unbumped;
                        EmitSkipped(generation, "no-renderer-match");
                        return;
                    case RendererPriorityDiscoveryStatus.Failed:
                        _state = ScopeState.Unbumped;
                        EmitNativeFailed(generation, discovery.FailedOperation, discovery.NativeErrorCode, processId: null);
                        return;
                }

                // Success: elevate the COMPLETE batch or unwind every changed lease.
                _leases.Clear();
                _leases.AddRange(discovery.Leases);
                var candidateCount = _leases.Count;
                var boostedCount = 0;
                var batchFailed = false;
                foreach (var lease in _leases)
                {
                    if (_native.TryApplyAboveNormal(lease))
                    {
                        lease.ElevatedAboveNormal = true;
                        boostedCount++;
                    }
                    else
                    {
                        EmitNativeFailed(generation, "set-above-normal", nativeErrorCode: 0, processId: lease.ProcessId);
                        batchFailed = true;
                        break;
                    }
                }

                if (batchFailed)
                {
                    var counts = RestoreAndCloseHandlesLocked();
                    _state = ScopeState.Unbumped;
                    EmitScopeClosed(generation, "begin-failed", counts);
                    return;
                }

                _state = ScopeState.Elevated;
                ApplicateTrace.DiagMs(
                    DiagnosticGroup,
                    "mode-toggle-priority-boost-began",
                    $"generation={generation} browserPid={browserProcessId.Value} candidateCount={candidateCount} boostedCount={boostedCount}");
            }
            catch (Exception ex)
            {
                SafeUnwindToUnbumpedLocked();
                EmitUnexpected("begin", ex);
            }
        }
    }

    public void ReapplyAfterKnownHide(string trigger, int? browserProcessId)
    {
        lock (_sync)
        {
            try
            {
                if (_state != ScopeState.Elevated)
                {
                    // Armed / Unbumped / Idle / Disposed: a known hide before the
                    // initial elevation (or after a failed one) elevates nothing.
                    return;
                }

                ReapplyLocked(trigger, browserProcessId);
            }
            catch (Exception ex)
            {
                EmitUnexpected("reapply", ex);
            }
        }
    }

    public void Close(long generation, string reason)
    {
        lock (_sync)
        {
            try
            {
                if (_state == ScopeState.Disposed)
                {
                    return;
                }

                if (generation <= 0 || _generation != generation)
                {
                    // Stale close: a superseding generation already replaced this
                    // one, or nothing is armed. Never touch the current scope.
                    return;
                }

                CloseCurrentLocked(reason);
            }
            catch (Exception ex)
            {
                EmitUnexpected("close", ex);
                ResetToIdleLocked();
            }
        }
    }

    public void CloseAny(string reason)
    {
        lock (_sync)
        {
            try
            {
                if (_state == ScopeState.Disposed)
                {
                    return;
                }

                if (_generation <= 0 && _leases.Count == 0)
                {
                    return;
                }

                CloseCurrentLocked(reason);
            }
            catch (Exception ex)
            {
                EmitUnexpected("close-any", ex);
                ResetToIdleLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_state == ScopeState.Disposed)
            {
                return;
            }

            try
            {
                if (_generation > 0 || _leases.Count > 0)
                {
                    CloseCurrentLocked("dispose-scope");
                }
            }
            catch (Exception ex)
            {
                EmitUnexpected("dispose", ex);
            }
            finally
            {
                _state = ScopeState.Disposed;
                _generation = 0;
                _leases.Clear();
            }
        }
    }

    private void CloseCurrentLocked(string reason)
    {
        var closedGeneration = _generation;
        var counts = RestoreAndCloseHandlesLocked();
        ResetToIdleLocked();
        EmitScopeClosed(closedGeneration, reason, counts);
    }

    private void ReapplyLocked(string trigger, int? browserProcessId)
    {
        // Re-apply AboveNormal to the retained live leases (best-effort; a
        // re-bump never unwinds the transaction).
        var existingCount = 0;
        foreach (var lease in _leases)
        {
            if (!lease.ElevatedAboveNormal)
            {
                continue;
            }

            if (_native.TryApplyAboveNormal(lease))
            {
                existingCount++;
            }
        }

        // Re-resolve membership so a renderer replacement that traverses a known
        // hide is also covered. Duplicates (same PID) keep the ORIGINAL retained
        // handle; only genuinely new children are opened, elevated, and retained.
        var addedCount = 0;
        if (browserProcessId is not null)
        {
            var discovery = _native.Discover(browserProcessId.Value);
            if (discovery.Status == RendererPriorityDiscoveryStatus.Success)
            {
                foreach (var candidate in discovery.Leases)
                {
                    if (OwnsProcessLocked(candidate.ProcessId))
                    {
                        _native.CloseLease(candidate);
                        continue;
                    }

                    if (_native.TryApplyAboveNormal(candidate))
                    {
                        candidate.ElevatedAboveNormal = true;
                        _leases.Add(candidate);
                        addedCount++;
                    }
                    else
                    {
                        _native.CloseLease(candidate);
                    }
                }
            }
        }

        ApplicateTrace.DiagMs(
            DiagnosticGroup,
            "mode-toggle-priority-boost-reapplied",
            $"generation={_generation} trigger={trigger} existingCount={existingCount} addedCount={addedCount}");
    }

    private bool OwnsProcessLocked(int processId)
    {
        foreach (var lease in _leases)
        {
            if (lease.ProcessId == processId)
            {
                return true;
            }
        }

        return false;
    }

    private RestoreCounts RestoreAndCloseHandlesLocked()
    {
        var counts = new RestoreCounts();
        foreach (var lease in _leases)
        {
            if (!lease.ElevatedAboveNormal)
            {
                continue;
            }

            counts.Boosted++;
            var result = _native.RestoreNormal(lease);
            switch (result.Outcome)
            {
                case RendererPriorityRestoreOutcome.Restored:
                    counts.RestoredNormal++;
                    break;
                case RendererPriorityRestoreOutcome.Exited:
                    counts.Exited++;
                    break;
                default:
                    counts.RestoreFailed++;
                    EmitNativeFailed(_generation, result.FailedOperation, result.NativeErrorCode, lease.ProcessId);
                    break;
            }
        }

        foreach (var lease in _leases)
        {
            _native.CloseLease(lease);
            counts.HandlesClosed++;
        }

        _leases.Clear();
        return counts;
    }

    private void SafeUnwindToUnbumpedLocked()
    {
        try
        {
            RestoreAndCloseHandlesLocked();
        }
        catch
        {
            _leases.Clear();
        }

        _state = ScopeState.Unbumped;
    }

    private void ResetToIdleLocked()
    {
        _leases.Clear();
        _generation = 0;
        _state = ScopeState.Idle;
    }

    private void EmitSkipped(long generation, string reason)
        => ApplicateTrace.DiagMs(
            DiagnosticGroup,
            "mode-toggle-priority-boost-skipped",
            $"generation={generation} reason={reason}");

    private void EmitNativeFailed(long generation, string operation, int nativeErrorCode, int? processId)
        => ApplicateTrace.DiagMs(
            DiagnosticGroup,
            "mode-toggle-priority-native-operation-failed",
            $"generation={generation} operation={operation} pid={RedactPid(processId)} nativeError={nativeErrorCode}");

    private void EmitReplaced(long oldGeneration, long newGeneration, RestoreCounts counts)
        => ApplicateTrace.DiagMs(
            DiagnosticGroup,
            "mode-toggle-priority-scope-replaced",
            $"oldGeneration={oldGeneration} newGeneration={newGeneration} boostedCount={counts.Boosted} restoredNormalCount={counts.RestoredNormal} exitedCount={counts.Exited} restoreFailedCount={counts.RestoreFailed} handlesClosedCount={counts.HandlesClosed}");

    private void EmitScopeClosed(long generation, string reason, RestoreCounts counts)
        => ApplicateTrace.DiagMs(
            DiagnosticGroup,
            ScopeClosedEvent,
            $"generation={generation} reason={reason} boostedCount={counts.Boosted} restoredNormalCount={counts.RestoredNormal} exitedCount={counts.Exited} restoreFailedCount={counts.RestoreFailed} handlesClosedCount={counts.HandlesClosed}");

    private void EmitUnexpected(string operation, Exception ex)
        => ApplicateTrace.DiagMs(
            DiagnosticGroup,
            "mode-toggle-priority-unexpected",
            $"generation={_generation} operation={operation} exceptionType={ex.GetType().Name}");

    // PIDs are ephemeral OS identifiers, but the design requires them redacted
    // in native-failure diagnostics. A short non-reversible token preserves
    // correlation across a single failure sequence without emitting the raw PID.
    private static string RedactPid(int? processId)
        => processId is null
            ? "none"
            : "#" + unchecked((uint)(processId.Value * 2654435761u) % 100000u)
                .ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    private struct RestoreCounts
    {
        public int Boosted;
        public int RestoredNormal;
        public int Exited;
        public int RestoreFailed;
        public int HandlesClosed;
    }

    private sealed class NoOpScope : IApplicateModeTogglePriorityScope
    {
        public void Arm(long generation, ApplicateMode target)
        {
        }

        public void BeginAfterFinalHide(long generation, int? browserProcessId)
        {
        }

        public void ReapplyAfterKnownHide(string trigger, int? browserProcessId)
        {
        }

        public void Close(long generation, string reason)
        {
        }

        public void CloseAny(string reason)
        {
        }

        public void Dispose()
        {
        }
    }
}

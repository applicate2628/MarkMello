using System.Threading;
using System.Threading.Tasks;

namespace MarkMello.Applicate.Desktop.Editing;

public interface IApplicateSessionStore
{
    /// <summary>
    /// d13 clause 1: returns <c>null</c> for "the persisted state could not be OBSERVED" -- and for
    /// nothing else. An absent, blank, JSON-<c>null</c> or unparseable file was observed to hold no
    /// usable session and still returns a session, because an empty baseline is TRUE for those and
    /// overwriting them is correct. Only an IO or access failure yields <c>null</c>, where the real
    /// state may still be intact on disk and must not be overwritten.
    /// <para>
    /// d13 clause 3: this store is the SINGLE owner of that distinction. No consumer may re-derive it
    /// -- not by reference identity against <see cref="ApplicateSession.Empty"/>, not by inspecting
    /// <c>OpenPaths.Count</c>.
    /// </para>
    /// </summary>
    ValueTask<ApplicateSession?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(ApplicateSession session, CancellationToken cancellationToken = default);
}

using System.Threading;
using System.Threading.Tasks;

namespace MarkMello.Applicate.Desktop.Editing;

public interface IApplicateSessionStore
{
    ValueTask<ApplicateSession> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(ApplicateSession session, CancellationToken cancellationToken = default);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MarkMello.Applicate.Desktop.Editing;

/// <summary>
/// JSON-backed session store under <c>%AppData%/MarkMello/applicate-session.json</c>.
/// Mirrors the upstream <c>JsonSettingsStore</c> style: best-effort,
/// atomic .tmp + rename, falls back to <see cref="ApplicateSession.Empty"/>
/// on missing or corrupt data -- both of which were OBSERVED to hold no usable
/// session. A read that could not observe the state at all returns <c>null</c>
/// instead; see <see cref="IApplicateSessionStore.LoadAsync"/> (d13).
/// </summary>
public sealed partial class JsonApplicateSessionStore : IApplicateSessionStore
{
    private readonly string _sessionFilePath;

    public JsonApplicateSessionStore(string? sessionRootDirectory = null)
    {
        var rootDirectory = ResolveSessionRootDirectory(sessionRootDirectory);
        _sessionFilePath = Path.Combine(rootDirectory, "applicate-session.json");
    }

    public ValueTask<ApplicateSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The read IS the existence check. File.Exists must not be used as a preflight here: it is
        // documented to return false "if any error occurs while trying to determine if the specified
        // file exists ... a failing or missing disk, or if the caller does not have permission to read
        // the file", and to do so WITHOUT throwing. That is measurably real -- a denied ACL on the
        // CONTAINING DIRECTORY makes File.Exists report false for a file that is present and populated
        // (a deny on the file itself does not; it still reports true). Preflighting with it therefore
        // forges an OBSERVED-empty answer out of a failure to observe, which is the exact collapse
        // this store exists to prevent. Absence is instead read off the exception the OS raises.
        string json;
        try
        {
            json = File.ReadAllText(_sessionFilePath);
        }
        catch (FileNotFoundException)
        {
            // OBSERVED: the OS positively reports there is no such file. A legitimate first run --
            // an empty baseline is true, and the next ordinary save may create the file.
            return ValueTask.FromResult<ApplicateSession?>(ApplicateSession.Empty);
        }
        catch (DirectoryNotFoundException)
        {
            // OBSERVED: the OS positively reports there is no such directory -- a first run before
            // anything has created %AppData%/MarkMello. Mapping this to null would stop a freshly
            // installed app from EVER persisting, since only SaveAsync creates that directory.
            // Where this instead means unreachable storage (an unmapped drive), the save it permits
            // fails inside Directory.CreateDirectory and is swallowed, so nothing can be destroyed.
            return ValueTask.FromResult<ApplicateSession?>(ApplicateSession.Empty);
        }
        catch
        {
            // NOT OBSERVED: the bytes were never read and the user's real session may be sitting on
            // disk intact -- a denied ACL, a sharing lock, a failing disk, an unreachable network
            // path. Fail closed. Catch-all by design, and deliberately scoped to the read call alone
            // so nothing raised while interpreting bytes below can be mistaken for an absence.
            return ValueTask.FromResult<ApplicateSession?>(null);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return ValueTask.FromResult<ApplicateSession?>(ApplicateSession.Empty);
            }

            var model = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionFileModel);
            if (model is null)
            {
                return ValueTask.FromResult<ApplicateSession?>(ApplicateSession.Empty);
            }

            var session = new ApplicateSession
            {
                OpenPaths = model.OpenPaths ?? new List<string>(),
                ActivePath = string.IsNullOrWhiteSpace(model.ActivePath) ? null : model.ActivePath,
                RecentPaths = model.RecentPaths ?? new List<string>(),
            };
            return ValueTask.FromResult<ApplicateSession?>(session);
        }
        catch (JsonException)
        {
            // OBSERVED: the bytes were read and they are not a session. No future read makes them
            // one, so an empty baseline is true and the next ordinary save may overwrite the file.
            // Mapping this to null instead would FREEZE persistence forever on a corrupt file.
            return ValueTask.FromResult<ApplicateSession?>(ApplicateSession.Empty);
        }
        catch
        {
            // NOT OBSERVED as a non-session either: the bytes were read, but interpreting them failed
            // for a reason that is not "these are not a session". Fail closed for the same reason as
            // the read's catch-all -- anything not positively identified as an observed non-session is
            // treated as possibly-intact. It also keeps LoadAsync non-throwing, which Program.cs
            // depends on: it dereferences the result outside its own try.
            return ValueTask.FromResult<ApplicateSession?>(null);
        }
    }

    public ValueTask SaveAsync(ApplicateSession session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            var directory = Path.GetDirectoryName(_sessionFilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return ValueTask.CompletedTask;
            }

            Directory.CreateDirectory(directory);

            var model = new SessionFileModel
            {
                OpenPaths = session.OpenPaths,
                ActivePath = session.ActivePath,
                RecentPaths = session.RecentPaths,
            };
            var json = JsonSerializer.Serialize(model, SessionJsonContext.Default.SessionFileModel);

            var tempFilePath = _sessionFilePath + ".tmp";
            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, _sessionFilePath, overwrite: true);
        }
        catch
        {
            // Best-effort: missing the save must not crash the app.
        }

        return ValueTask.CompletedTask;
    }

    private static string ResolveSessionRootDirectory(string? sessionRootDirectory)
    {
        if (!string.IsNullOrWhiteSpace(sessionRootDirectory))
        {
            return Path.GetFullPath(sessionRootDirectory);
        }

        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appDataDirectory))
        {
            return Path.Combine(AppContext.BaseDirectory, "MarkMello");
        }

        return Path.Combine(appDataDirectory, "MarkMello");
    }

    internal sealed class SessionFileModel
    {
        public List<string>? OpenPaths { get; set; }

        public string? ActivePath { get; set; }

        public List<string>? RecentPaths { get; set; }
    }

    [JsonSerializable(typeof(SessionFileModel))]
    internal sealed partial class SessionJsonContext : JsonSerializerContext
    {
    }
}

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

        try
        {
            if (!File.Exists(_sessionFilePath))
            {
                return ValueTask.FromResult<ApplicateSession?>(ApplicateSession.Empty);
            }

            var json = File.ReadAllText(_sessionFilePath);
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
            // NOT OBSERVED: File.Exists/File.ReadAllText failed, so the bytes were never read and the
            // user's real session may be sitting on disk intact. Fail closed -- the caller must not
            // persist over a baseline this process could not see. Catch-all by design: anything not
            // positively identified as an observed non-session is treated as possibly-intact.
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

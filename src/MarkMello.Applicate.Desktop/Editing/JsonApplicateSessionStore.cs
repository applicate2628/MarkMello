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
/// on missing or corrupt data.
/// </summary>
public sealed partial class JsonApplicateSessionStore : IApplicateSessionStore
{
    private readonly string _sessionFilePath;

    public JsonApplicateSessionStore(string? sessionRootDirectory = null)
    {
        var rootDirectory = ResolveSessionRootDirectory(sessionRootDirectory);
        _sessionFilePath = Path.Combine(rootDirectory, "applicate-session.json");
    }

    public ValueTask<ApplicateSession> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!File.Exists(_sessionFilePath))
            {
                return ValueTask.FromResult(ApplicateSession.Empty);
            }

            var json = File.ReadAllText(_sessionFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return ValueTask.FromResult(ApplicateSession.Empty);
            }

            var model = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionFileModel);
            if (model is null)
            {
                return ValueTask.FromResult(ApplicateSession.Empty);
            }

            var session = new ApplicateSession
            {
                OpenPaths = model.OpenPaths ?? new List<string>(),
                ActivePath = string.IsNullOrWhiteSpace(model.ActivePath) ? null : model.ActivePath,
            };
            return ValueTask.FromResult(session);
        }
        catch
        {
            return ValueTask.FromResult(ApplicateSession.Empty);
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
    }

    [JsonSerializable(typeof(SessionFileModel))]
    internal sealed partial class SessionJsonContext : JsonSerializerContext
    {
    }
}

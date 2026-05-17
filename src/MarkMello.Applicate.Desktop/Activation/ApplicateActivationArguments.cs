using System.Text.Json;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Activation;

internal static class ApplicateActivationArguments
{
    public static IReadOnlyList<string> GetSupportedFilePaths(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var paths = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || !File.Exists(arg))
            {
                continue;
            }

            if (SupportedDocumentTypes.IsSupportedPath(arg))
            {
                paths.Add(Path.GetFullPath(arg));
            }
        }

        return paths;
    }

    public static string CreatePayload(IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        return JsonSerializer.Serialize(new Payload(filePaths));
    }

    public static bool TryParsePayload(string payload, out IReadOnlyList<string> filePaths)
    {
        filePaths = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Payload>(payload);
            if (parsed?.FilePaths is null)
            {
                return false;
            }

            filePaths = parsed.FilePaths
                .Where(path => !string.IsNullOrWhiteSpace(path)
                    && File.Exists(path)
                    && SupportedDocumentTypes.IsSupportedPath(path))
                .Select(Path.GetFullPath)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private sealed record Payload(IReadOnlyList<string> FilePaths);
}

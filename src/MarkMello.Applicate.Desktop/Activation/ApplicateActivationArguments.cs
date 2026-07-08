using System.Text.Json;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Activation;

internal static class ApplicateActivationArguments
{
    public const string ShutdownArgument = "--shutdown";

    public static bool IsShutdownRequest(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(IsShutdownArgument);
    }

    public static ApplicateActivationRequest CreateRequest(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var argArray = args as string[] ?? args.ToArray();
        return IsShutdownRequest(argArray)
            ? ApplicateActivationRequest.Shutdown
            : ApplicateActivationRequest.Open(GetSupportedFilePaths(argArray));
    }

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
        return CreatePayload(ApplicateActivationRequest.Open(filePaths));
    }

    public static string CreatePayload(ApplicateActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(new Payload(request.FilePaths, request.ShutdownRequested));
    }

    public static bool TryParsePayload(string payload, out ApplicateActivationRequest request)
    {
        request = ApplicateActivationRequest.Open(Array.Empty<string>());
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

            if (parsed.ShutdownRequested)
            {
                request = ApplicateActivationRequest.Shutdown;
                return true;
            }

            var filePaths = parsed.FilePaths
                .Where(path => !string.IsNullOrWhiteSpace(path)
                    && File.Exists(path)
                    && SupportedDocumentTypes.IsSupportedPath(path))
                .Select(Path.GetFullPath)
                .ToArray();
            request = ApplicateActivationRequest.Open(filePaths);
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

    private static bool IsShutdownArgument(string? arg)
        => string.Equals(arg?.Trim(), ShutdownArgument, StringComparison.OrdinalIgnoreCase);

    private sealed record Payload(IReadOnlyList<string>? FilePaths, bool ShutdownRequested = false);
}

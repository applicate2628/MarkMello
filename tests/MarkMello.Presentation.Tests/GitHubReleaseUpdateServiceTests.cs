using MarkMello.Application.Updates;
using MarkMello.Infrastructure.Updates;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;

namespace MarkMello.Presentation.Tests;

public sealed class GitHubReleaseUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsyncReturnsUpdateAvailableForCurrentRuntimeAsset()
    {
        var assetName = GetCurrentRuntimeAssetName();
        if (assetName is null)
        {
            return;
        }

        var service = CreateService(
            $$"""
            {
              "tag_name": "v1.2.3",
              "name": "v1.2.3",
              "html_url": "https://github.com/dartdavros/MarkMello/releases/tag/v1.2.3",
              "published_at": "2026-04-19T12:00:00Z",
              "assets": [
                {
                  "name": "{{assetName}}",
                  "browser_download_url": "https://github.com/dartdavros/MarkMello/releases/download/v1.2.3/{{assetName}}",
                  "state": "uploaded"
                }
              ]
            }
            """);

        var result = await service.CheckForUpdatesAsync();

        var available = Assert.IsType<UpdateCheckResult.UpdateAvailable>(result);
        Assert.Equal("1.0.0", available.Package.CurrentVersion);
        Assert.Equal("1.2.3", available.Package.ReleaseVersion);
        Assert.Equal(assetName, available.Package.AssetName);
    }

    [Fact]
    public async Task CheckForUpdatesAsyncReturnsUpToDateWhenVersionsMatch()
    {
        var assetName = GetCurrentRuntimeAssetName();
        if (assetName is null)
        {
            return;
        }

        var service = CreateService(
            $$"""
            {
              "tag_name": "v1.0.0",
              "name": "v1.0.0",
              "html_url": "https://github.com/dartdavros/MarkMello/releases/tag/v1.0.0",
              "published_at": "2026-04-19T12:00:00Z",
              "assets": [
                {
                  "name": "{{assetName}}",
                  "browser_download_url": "https://github.com/dartdavros/MarkMello/releases/download/v1.0.0/{{assetName}}",
                  "state": "uploaded"
                }
              ]
            }
            """);

        var result = await service.CheckForUpdatesAsync();

        var upToDate = Assert.IsType<UpdateCheckResult.UpToDate>(result);
        Assert.Equal("1.0.0", upToDate.CurrentVersion);
        Assert.Equal("1.0.0", upToDate.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsyncUsesReleaseAssetPrefixMetadata()
    {
        var assetName = GetCurrentRuntimeAssetName("MarkMello.Applicate");
        if (assetName is null)
        {
            return;
        }

        var service = CreateService(
            $$"""
            {
              "tag_name": "v1.2.3",
              "name": "v1.2.3",
              "html_url": "https://github.com/applicate2628/MarkMello/releases/tag/v1.2.3",
              "published_at": "2026-04-19T12:00:00Z",
              "assets": [
                {
                  "name": "{{assetName}}",
                  "browser_download_url": "https://github.com/applicate2628/MarkMello/releases/download/v1.2.3/{{assetName}}",
                  "state": "uploaded"
                }
              ]
            }
            """,
            CreateMetadataAssembly("applicate2628", "MarkMello", "MarkMello.Applicate"));

        var result = await service.CheckForUpdatesAsync();

        var available = Assert.IsType<UpdateCheckResult.UpdateAvailable>(result);
        Assert.Equal(assetName, available.Package.AssetName);
    }

    private static GitHubReleaseUpdateService CreateService(string responseJson, Assembly? assembly = null)
    {
        var handler = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MarkMello.Tests/updates");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return new GitHubReleaseUpdateService(
            client,
            assembly ?? typeof(GitHubReleaseUpdateServiceTests).Assembly);
    }

    private static string? GetCurrentRuntimeAssetName(string prefix = "MarkMello")
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => $"{prefix}-setup-win-x64.exe",
                Architecture.Arm64 => $"{prefix}-setup-win-arm64.exe",
                _ => null
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => $"{prefix}-macos-x64.dmg",
                Architecture.Arm64 => $"{prefix}-macos-arm64.dmg",
                _ => null
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => $"{prefix}-linux-x86_64.AppImage",
                Architecture.Arm64 => $"{prefix}-linux-aarch64.AppImage",
                _ => null
            };
        }

        return null;
    }

    private static AssemblyBuilder CreateMetadataAssembly(string owner, string repo, string assetPrefix)
    {
        var assemblyName = new AssemblyName("MarkMello.Presentation.Tests.DynamicMetadata");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);

        AddMetadata(assembly, "MarkMelloReleaseOwner", owner);
        AddMetadata(assembly, "MarkMelloReleaseRepo", repo);
        AddMetadata(assembly, "MarkMelloReleaseAssetPrefix", assetPrefix);
        return assembly;
    }

    private static void AddMetadata(AssemblyBuilder assembly, string key, string value)
    {
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])
            ?? throw new InvalidOperationException("AssemblyMetadataAttribute constructor was not found.");
        assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [key, value]));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }
}

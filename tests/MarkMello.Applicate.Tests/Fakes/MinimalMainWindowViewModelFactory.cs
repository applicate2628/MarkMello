using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Application.Updates;
using MarkMello.Domain;
using MarkMello.Domain.Diagnostics;
using MarkMello.Infrastructure.Markdown;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Tests.Fakes;

/// <summary>
/// Builds a real, minimally-wired <see cref="MainWindowViewModel"/> for tests that need
/// actual view-model behaviour (commands, headings, property-changed plumbing) rather than
/// the loose <see cref="FakeMainWindowVm"/> shape used by bridge/property-name tests. Every
/// dependency is a no-op/in-memory stub; nothing here touches disk, network, or settings.
///
/// <para>Extracted from <c>BulkCloseSessionLossReproTests</c>'s private
/// <c>CreateViewModel()</c>/stub set — the only other place in this project that constructs
/// a real <see cref="MainWindowViewModel"/> — so a second test file needing the same minimal
/// wiring (ApplicateTocPanelKeyboardAccessibilityTests) does not re-implement the same eight
/// dependency interfaces.</para>
/// </summary>
internal static class MinimalMainWindowViewModelFactory
{
    public static MainWindowViewModel Create()
        => new(
            new OpenDocumentUseCase(new StubLoader()),
            new SaveDocumentUseCase(new StubSaver()),
            new StubFilePicker(),
            new StubCommandLine(),
            new LocalizationService(AppLanguage.English),
            new StubSettingsStore(),
            new StubThemeService(),
            new StubStartupMetrics(),
            new RenderMarkdownDocumentUseCase(new PlainTextRenderer()),
            new StubUpdateService(),
            new MarkdigTableCellSourceEditor());

    private sealed class PlainTextRenderer : IMarkdownDocumentRenderer
    {
        public RenderedMarkdownDocument Render(string markdown)
            => RenderedMarkdownDocument.PlainText(markdown);
    }

    private sealed class StubLoader : IDocumentLoader
    {
        public Task<MarkdownSource> LoadAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(new MarkdownSource(path, Path.GetFileName(path), string.Empty));
    }

    private sealed class StubSaver : IDocumentSaver
    {
        public Task SaveAsync(string path, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubFilePicker : IFilePicker
    {
        public Task<string?> PickMarkdownFileAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> PickSaveMarkdownFileAsync(string suggestedFileName, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(FileSavePickerSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubCommandLine : ICommandLineActivation
    {
        public string? GetActivationFilePath() => null;
    }

    private sealed class StubSettingsStore : ISettingsStore
    {
        public ValueTask<ReadingPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ReadingPreferences.Default);

        public ValueTask SavePreferencesAsync(ReadingPreferences preferences, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<ThemeMode> LoadThemeAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ThemeMode.Light);

        public ValueTask SaveThemeAsync(ThemeMode theme, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<AppLanguage> LoadLanguageAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AppLanguage.English);

        public ValueTask SaveLanguageAsync(AppLanguage language, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<WindowPlacement?> LoadWindowPlacementAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<WindowPlacement?>(null);

        public ValueTask SaveWindowPlacementAsync(WindowPlacement? placement, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class StubThemeService : IThemeService
    {
        public void Apply(ThemeMode mode, LightPaletteMode lightPalette)
        {
        }

        public ThemeMode GetEffectiveTheme() => ThemeMode.Light;
    }

    private sealed class StubStartupMetrics : IStartupMetrics
    {
        public void Mark(StartupStage stage)
        {
        }

        public StartupSnapshot Snapshot()
            => new(new Dictionary<StartupStage, TimeSpan>());
    }

    private sealed class StubUpdateService : IUpdateService
    {
        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<UpdateCheckResult>(new UpdateCheckResult.SourceNotConfigured("test"));

        public Task<UpdateDownloadResult> DownloadUpdateAsync(
            AppUpdatePackage package,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<UpdateDownloadResult>(new UpdateDownloadResult.Failed("test"));

        public Task<UpdatePrepareResult> PrepareDownloadedUpdateAsync(
            AppUpdatePackage package,
            string downloadedFilePath,
            CancellationToken cancellationToken = default)
            => Task.FromResult<UpdatePrepareResult>(new UpdatePrepareResult.Failed("test"));
    }
}

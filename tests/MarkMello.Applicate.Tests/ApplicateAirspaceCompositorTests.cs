using System.IO;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation.Services;
using MarkMello.Presentation.ViewModels;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateAirspaceCompositorTests
{
    private static readonly string AirspaceCompositorSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Rendering",
        "ApplicateAirspaceCompositor.cs");
    private static readonly string BridgeSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "ApplicateSiblingMountBridge.cs");

    [Fact]
    public void DocumentTransitionStartingRaisesCoverBeforeDocumentMutationForActiveSurface()
    {
        var oldSource = Source("old.md");
        var state = new FakeDocumentRevealState { Document = oldSource };
        var host = new FakeDocumentSignals();
        var covers = new FakeCoverFactory();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            new FakePaintGate());
        compositor.RegisterDocumentSession(
            host,
            ApplicateMode.Viewer,
            isActiveSurface: () => true);

        state.RaiseDocumentTransitionStarting();

        var cover = Assert.Single(covers.Created);
        Assert.Equal(1, cover.ShowCount);
        Assert.Same(oldSource, state.Document);
    }

    [Fact]
    public void DocumentSessionHidesOnlyAfterMatchingNonTransactionalCommitAndRevealReadyPaintGate()
    {
        var state = new FakeDocumentRevealState { Document = Source("old.md") };
        var host = new FakeDocumentSignals();
        var covers = new FakeCoverFactory();
        var paintGate = new FakePaintGate();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            paintGate);
        compositor.RegisterDocumentSession(
            host,
            ApplicateMode.Viewer,
            isActiveSurface: () => true);
        state.RaiseDocumentTransitionStarting();
        var cover = Assert.Single(covers.Created);

        host.RaiseCommitCompleted(transactionGeneration: 7, ApplicateMode.Viewer);
        host.RaiseDocumentRevealReady();
        paintGate.Flush();

        Assert.Equal(0, cover.HideAnimatedCount);
        Assert.Equal(0, cover.HideImmediateCount);

        host.RaiseCommitCompleted(transactionGeneration: 0, ApplicateMode.Edit);
        paintGate.Flush();

        Assert.Equal(0, cover.HideAnimatedCount);
        Assert.Equal(0, cover.HideImmediateCount);

        host.RaiseCommitCompleted(transactionGeneration: 0, ApplicateMode.Viewer);

        Assert.Equal(1, paintGate.PendingCount);
        Assert.Equal(0, cover.HideAnimatedCount);

        paintGate.Flush();

        Assert.Equal(1, cover.HideAnimatedCount);
        Assert.Equal(0, cover.HideImmediateCount);
    }

    [Fact]
    public void SamePathEditReloadSkipsDocumentCover()
    {
        var state = new FakeDocumentRevealState { Document = Source(@"C:\docs\same.md") };
        var host = new FakeDocumentSignals();
        var covers = new FakeCoverFactory();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            new FakePaintGate());
        compositor.RegisterDocumentSession(
            host,
            ApplicateMode.Edit,
            isActiveSurface: () => true,
            suppressSamePathReloadCover: true);

        state.RaiseDocumentTransitionStarting();
        state.SetDocument(Source(@"c:\DOCS\same.md"));

        Assert.Equal(0, Assert.Single(covers.Created).ShowCount);
    }

    [Fact]
    public void SuppressNextDocumentRevealSkipsTransitionAndMatchingDocumentChange()
    {
        var state = new FakeDocumentRevealState { Document = Source("old.md") };
        var host = new FakeDocumentSignals();
        var covers = new FakeCoverFactory();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            new FakePaintGate());
        compositor.RegisterDocumentSession(
            host,
            ApplicateMode.Viewer,
            isActiveSurface: () => true);

        state.RaiseSuppressNextDocumentReveal();
        state.RaiseDocumentTransitionStarting();
        state.SetDocument(Source("suppressed.md"));

        Assert.Equal(0, Assert.Single(covers.Created).ShowCount);

        state.RaiseDocumentTransitionStarting();

        var cover = Assert.Single(covers.Created);
        Assert.Equal(1, cover.ShowCount);
    }

    [Fact]
    public void RendererFailureHidesCoverAndClearsHeadingsOnlyWhenConfigured()
    {
        var readerState = new FakeDocumentRevealState { Document = Source("reader.md") };
        var readerHost = new FakeDocumentSignals();
        var readerCovers = new FakeCoverFactory();
        using var readerCompositor = new ApplicateAirspaceCompositor(
            new Panel(),
            readerState,
            readerCovers.Create,
            new FakePaintGate());
        readerCompositor.RegisterDocumentSession(
            readerHost,
            ApplicateMode.Viewer,
            isActiveSurface: () => true,
            clearHeadingsOnRendererFailure: true);
        readerState.RaiseDocumentTransitionStarting();

        readerHost.RaiseRendererFailed();

        Assert.Equal(1, Assert.Single(readerCovers.Created).HideImmediateCount);
        Assert.Equal(1, readerState.ClearHeadingsCount);

        var editState = new FakeDocumentRevealState { Document = Source("edit.md") };
        var editHost = new FakeDocumentSignals();
        var editCovers = new FakeCoverFactory();
        using var editCompositor = new ApplicateAirspaceCompositor(
            new Panel(),
            editState,
            editCovers.Create,
            new FakePaintGate());
        editCompositor.RegisterDocumentSession(
            editHost,
            ApplicateMode.Edit,
            isActiveSurface: () => true,
            clearHeadingsOnRendererFailure: false);
        editState.RaiseDocumentTransitionStarting();

        editHost.RaiseRendererFailed();

        Assert.Equal(1, Assert.Single(editCovers.Created).HideImmediateCount);
        Assert.Equal(0, editState.ClearHeadingsCount);
    }

    [Fact]
    public void StartupSessionWaitsForWindowRevealReadyHeadingsRendererSettleAndPaint()
    {
        var state = new FakeDocumentRevealState
        {
            Document = Source("heavy.md", new string('x', 1024 * 1024 + 1)),
            IsTocPreferredVisible = true,
            HasDocumentHeadings = false
        };
        var startupSignals = new FakeStartupSignals();
        var startupShell = new FakeStartupShell();
        var covers = new FakeCoverFactory();
        var paintGate = new FakePaintGate();
        var scheduler = new FakeAirspaceScheduler();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            paintGate,
            scheduler);
        compositor.RegisterStartupSession(startupShell, startupSignals, state);
        var cover = Assert.Single(covers.Created);

        startupSignals.RaiseDocumentRevealReady();
        scheduler.Flush();
        Assert.Equal(0, cover.HideImmediateCount);

        startupShell.RaiseOpened();
        scheduler.Flush();
        Assert.Equal(1, cover.StartupSplashShowCount);
        Assert.Equal("heavy.md", cover.StartupSplashDocumentName);

        startupSignals.RaiseHeadingsChanged([new DocumentHeading("intro", 1, "Intro", 0)]);
        scheduler.Flush();
        Assert.Equal(1, startupSignals.SettleProbeRequestCount);
        Assert.Equal(0, paintGate.PendingCount);

        startupSignals.RaiseRendererSettled();
        scheduler.Flush();
        Assert.Equal(1, paintGate.PendingCount);
        Assert.Equal(0, cover.HideImmediateCount);

        paintGate.Flush();
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.Flush();

        Assert.Equal(1, cover.HideImmediateCount);
        Assert.Equal(TimeSpan.Zero, cover.LastHideDuration);
        Assert.Equal(1.0, Assert.Single(startupShell.OpacityAssignments));
    }

    [Fact]
    public void StartupSessionFallbacksAndRendererFailureReleaseWithoutPaintGate()
    {
        var state = new FakeDocumentRevealState { Document = Source("doc.md") };
        var startupSignals = new FakeStartupSignals();
        var startupShell = new FakeStartupShell();
        var covers = new FakeCoverFactory();
        var paintGate = new FakePaintGate();
        var scheduler = new FakeAirspaceScheduler();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            paintGate,
            scheduler);
        compositor.RegisterStartupSession(startupShell, startupSignals, state);
        var cover = Assert.Single(covers.Created);

        startupShell.RaiseOpened();
        scheduler.Flush();
        scheduler.FireTimer(TimeSpan.FromSeconds(15));
        scheduler.Flush();

        Assert.Equal(1, cover.HideImmediateCount);
        Assert.Equal(0, paintGate.PendingCount);
        Assert.Equal(1.0, Assert.Single(startupShell.OpacityAssignments));

        var failureState = new FakeDocumentRevealState { Document = Source("failure.md") };
        var failureSignals = new FakeStartupSignals();
        var failureShell = new FakeStartupShell();
        var failureCovers = new FakeCoverFactory();
        var failureScheduler = new FakeAirspaceScheduler();
        using var failureCompositor = new ApplicateAirspaceCompositor(
            new Panel(),
            failureState,
            failureCovers.Create,
            new FakePaintGate(),
            failureScheduler);
        failureCompositor.RegisterStartupSession(failureShell, failureSignals, failureState);
        var failureCover = Assert.Single(failureCovers.Created);

        failureShell.RaiseOpened();
        failureScheduler.Flush();
        failureSignals.RaiseRendererFailed();
        failureScheduler.Flush();
        failureScheduler.Flush();

        Assert.Equal(1, failureCover.HideImmediateCount);
        Assert.Equal(1.0, Assert.Single(failureShell.OpacityAssignments));
    }

    [Fact]
    public void StartupSessionPaintFallbackHidesIfFrameGateDoesNotComplete()
    {
        var state = new FakeDocumentRevealState { Document = Source("doc.md") };
        var startupSignals = new FakeStartupSignals();
        var startupShell = new FakeStartupShell();
        var covers = new FakeCoverFactory();
        var scheduler = new FakeAirspaceScheduler();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            new FakePaintGate(),
            scheduler);
        compositor.RegisterStartupSession(startupShell, startupSignals, state);
        var cover = Assert.Single(covers.Created);

        startupShell.RaiseOpened();
        scheduler.Flush();
        startupSignals.RaiseDocumentRevealReady();
        scheduler.Flush();

        scheduler.FireTimer(TimeSpan.FromMilliseconds(250));

        Assert.Equal(1, cover.HideImmediateCount);
        Assert.Equal(TimeSpan.Zero, cover.LastHideDuration);
        Assert.Equal(1.0, Assert.Single(startupShell.OpacityAssignments));
    }

    [Fact]
    public void ThemeSessionWaitsForMatchingRendererPaintAck()
    {
        var source = Source("theme.md");
        var state = new FakeDocumentRevealState { Document = source };
        var signals = new FakeThemeSignals { HasLoadedDocument = true };
        var covers = new FakeCoverFactory();
        var paintGate = new FakePaintGate();
        var scheduler = new FakeAirspaceScheduler();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            paintGate,
            scheduler);
        compositor.RegisterThemeSession(signals, isActiveSurface: () => true);

        state.RaiseThemeTransitionStarting(ThemeMode.Dark);

        var cover = Assert.Single(covers.Created);
        Assert.Equal(1, cover.ShowCount);
        Assert.Same(ThemeVariant.Dark, cover.LastShowThemeVariant);
        Assert.Same(source, signals.LastLoadedSource);

        signals.RaiseThemeChangeSent("dark", requestId: 41);
        signals.RaiseThemeApplied("light", requestId: 41);
        signals.RaiseThemeApplied("dark", requestId: 42);
        paintGate.Flush();

        Assert.Equal(0, cover.HideAnimatedCount);
        Assert.Equal(0, cover.HideImmediateCount);

        signals.RaiseThemeApplied("dark", requestId: 41);

        Assert.Equal(1, paintGate.PendingCount);

        paintGate.Flush();

        Assert.Equal(1, cover.HideAnimatedCount);
        Assert.Equal(0, cover.HideImmediateCount);
    }

    [Fact]
    public void ThemeSessionFallbackFailureAndInactiveSurfaceReleaseWithoutPaintGate()
    {
        var fallbackState = new FakeDocumentRevealState { Document = Source("fallback.md") };
        var fallbackSignals = new FakeThemeSignals { HasLoadedDocument = true };
        var fallbackCovers = new FakeCoverFactory();
        var fallbackPaintGate = new FakePaintGate();
        var fallbackScheduler = new FakeAirspaceScheduler();
        using var fallbackCompositor = new ApplicateAirspaceCompositor(
            new Panel(),
            fallbackState,
            fallbackCovers.Create,
            fallbackPaintGate,
            fallbackScheduler);
        fallbackCompositor.RegisterThemeSession(fallbackSignals, isActiveSurface: () => true);
        fallbackState.RaiseThemeTransitionStarting(ThemeMode.Dark);

        fallbackScheduler.FireTimer(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Assert.Single(fallbackCovers.Created).HideImmediateCount);
        Assert.Equal(0, fallbackPaintGate.PendingCount);

        var failureState = new FakeDocumentRevealState { Document = Source("failure.md") };
        var failureSignals = new FakeThemeSignals { HasLoadedDocument = true };
        var failureCovers = new FakeCoverFactory();
        using var failureCompositor = new ApplicateAirspaceCompositor(
            new Panel(),
            failureState,
            failureCovers.Create,
            new FakePaintGate(),
            new FakeAirspaceScheduler());
        failureCompositor.RegisterThemeSession(failureSignals, isActiveSurface: () => true);
        failureState.RaiseThemeTransitionStarting(ThemeMode.Dark);

        failureSignals.RaiseRendererFailed();

        Assert.Equal(1, Assert.Single(failureCovers.Created).HideImmediateCount);

        var active = true;
        var inactiveState = new FakeDocumentRevealState { Document = Source("inactive.md") };
        var inactiveSignals = new FakeThemeSignals { HasLoadedDocument = true };
        var inactiveCovers = new FakeCoverFactory();
        using var inactiveCompositor = new ApplicateAirspaceCompositor(
            new Panel(),
            inactiveState,
            inactiveCovers.Create,
            new FakePaintGate(),
            new FakeAirspaceScheduler());
        inactiveCompositor.RegisterThemeSession(inactiveSignals, isActiveSurface: () => active);
        inactiveState.RaiseThemeTransitionStarting(ThemeMode.Dark);

        active = false;
        inactiveState.RaisePropertyChanged(nameof(MainWindowViewModel.IsEditMode));

        Assert.Equal(1, Assert.Single(inactiveCovers.Created).HideImmediateCount);
    }

    [Fact]
    public void ThemeSessionRetargetsCoveredSessionAndIgnoresPriorAck()
    {
        var state = new FakeDocumentRevealState { Document = Source("retarget.md") };
        var signals = new FakeThemeSignals { HasLoadedDocument = true };
        var covers = new FakeCoverFactory();
        var paintGate = new FakePaintGate();
        var scheduler = new FakeAirspaceScheduler();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            paintGate,
            scheduler);
        compositor.RegisterThemeSession(signals, isActiveSurface: () => true);

        state.RaiseThemeTransitionStarting(ThemeMode.Dark);
        signals.RaiseThemeChangeSent("dark", requestId: 7);
        state.RaiseThemeTransitionStarting(ThemeMode.ClassicWhite);

        var cover = Assert.Single(covers.Created);
        Assert.Equal(1, cover.ShowCount);
        Assert.Equal(1, cover.UpdateBrushCount);
        Assert.Same(AvaloniaThemeService.ClassicWhiteThemeVariant, cover.LastUpdateThemeVariant);
        Assert.Equal(1, scheduler.ActiveTimerCount(TimeSpan.FromSeconds(2)));

        signals.RaiseThemeApplied("dark", requestId: 7);
        paintGate.Flush();

        Assert.Equal(0, cover.HideAnimatedCount);
        Assert.Equal(0, cover.HideImmediateCount);

        signals.RaiseThemeChangeSent("classic-white", requestId: 8);
        signals.RaiseThemeApplied("classic-white", requestId: 8);
        paintGate.Flush();

        Assert.Equal(1, cover.HideAnimatedCount);
    }

    [Fact]
    public void ModeSessionWaitsForLayoutCommitMinimapAndRendererBeforeNativeReveal()
    {
        var state = new FakeDocumentRevealState();
        var events = new List<string>();
        var slotAdapter = new FakeModeSlotAdapter(events);
        var host = new FakeModeHostRevealIntents(slotAdapter.RevealSnapshot, events);
        var covers = new FakeCoverFactory(events);
        var scheduler = new FakeAirspaceScheduler();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            new FakePaintGate(),
            scheduler);
        using var modeSession = compositor.RegisterModeSession(
            host,
            slotAdapter,
            () => ReadingPreferences.Default);

        Assert.True(modeSession.TryReconcile(ApplicateMode.Edit, modeSlotSwitch: true));
        var cover = Assert.Single(covers.Created);
        Assert.Equal(1, cover.ShowCount);
        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal(0, slotAdapter.EditGeneration);

        scheduler.Flush();
        Assert.Equal(1, slotAdapter.ReconcileRequestCount);
        Assert.True(modeSession.TryReconcile(ApplicateMode.Edit, modeSlotSwitch: true));

        var generation = slotAdapter.EditGeneration;
        Assert.True(generation > 0);
        Assert.Equal(0, slotAdapter.ViewerGeneration);
        Assert.Equal(new[] { ApplicateMode.Viewer }, host.SuppressedModes);
        Assert.True(slotAdapter.ViewerState.IsVisible);
        Assert.False(slotAdapter.ViewerState.IsInteractive);
        Assert.True(slotAdapter.EditState.IsVisible);
        Assert.False(slotAdapter.EditState.IsInteractive);
        Assert.Equal(0.0, slotAdapter.EditState.Opacity);

        host.RaiseCommitCompleted(generation, ApplicateMode.Edit);
        scheduler.Flush();
        Assert.Empty(host.RevealedGenerations);

        host.RaiseMinimapSettledNotApplicable(generation);
        scheduler.Flush();
        Assert.Empty(host.RevealedGenerations);

        host.RaiseRendererSettled(generation);
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.Flush();

        var reveal = Assert.Single(host.RevealedGenerations);
        Assert.Equal(generation, reveal.Generation);
        Assert.Equal(0.0, reveal.ViewerOpacity);
        Assert.Equal(1.0, reveal.EditOpacity);
        Assert.Equal(ApplicateMode.Edit, slotAdapter.CommittedMode);
        Assert.Equal(1, slotAdapter.ClearGenerationCount);
        Assert.Empty(host.RestoredModes);
        Assert.Equal(1, cover.HideAnimatedCount);
        Assert.Equal(ApplicateMotion.ModeSwitchDuration(ReadingPreferences.Default), cover.LastHideDuration);
    }

    [Fact]
    public void ModeSessionRestoresOutgoingBeforeCoverHideWhenNativeRevealIsRejected()
    {
        var state = new FakeDocumentRevealState();
        var events = new List<string>();
        var slotAdapter = new FakeModeSlotAdapter(events);
        var host = new FakeModeHostRevealIntents(slotAdapter.RevealSnapshot, events)
        {
            RejectReveals = true
        };
        var covers = new FakeCoverFactory(events);
        var scheduler = new FakeAirspaceScheduler();
        using var compositor = new ApplicateAirspaceCompositor(
            new Panel(),
            state,
            covers.Create,
            new FakePaintGate(),
            scheduler);
        using var modeSession = compositor.RegisterModeSession(
            host,
            slotAdapter,
            () => ReadingPreferences.Default);

        Assert.True(modeSession.TryReconcile(ApplicateMode.Edit, modeSlotSwitch: true));
        scheduler.Flush();
        Assert.True(modeSession.TryReconcile(ApplicateMode.Edit, modeSlotSwitch: true));
        var generation = slotAdapter.EditGeneration;

        host.RaiseCommitCompleted(generation, ApplicateMode.Edit);
        host.RaiseMinimapSettledNotApplicable(generation);
        host.RaiseRendererSettled(generation);
        scheduler.Flush();

        Assert.Equal(new[] { ApplicateMode.Viewer }, host.RestoredModes);
        Assert.Equal(ApplicateMode.Viewer, slotAdapter.CommittedMode);
        Assert.Equal(1, Assert.Single(covers.Created).HideImmediateCount);
        var restore = events.LastIndexOf("restore:Viewer");
        var hide = events.LastIndexOf("cover-hide:immediate");
        Assert.True(restore >= 0);
        Assert.True(hide > restore);
    }

    [Fact]
    public void ModePolicyLivesInCompositorAndBridgeIsOnlySlotAdapter()
    {
        var compositor = File.ReadAllText(AirspaceCompositorSourcePath);
        var bridge = File.ReadAllText(BridgeSourcePath);

        Assert.Contains("RegisterModeSession", compositor, StringComparison.Ordinal);
        Assert.Contains("ModeRevealSession", compositor, StringComparison.Ordinal);
        Assert.Contains("IApplicateHostRevealIntents", compositor, StringComparison.Ordinal);
        Assert.Contains("bridge-cover-visible", compositor, StringComparison.Ordinal);
        Assert.Contains("bridge-cover-hidden", compositor, StringComparison.Ordinal);
        Assert.Contains("bridge-transaction-outgoing-native-suppressed", compositor, StringComparison.Ordinal);
        Assert.Contains("bridge-transaction-outgoing-native-restored-before-cover-hide", compositor, StringComparison.Ordinal);
        Assert.Contains("bridge-transaction-committed", compositor, StringComparison.Ordinal);

        Assert.DoesNotContain("IApplicateHostRevealIntents", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("_hostRevealIntents", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicateModeRevealCoverWindow", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPrimeModeRevealCover", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueModeTransactionCommit", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("RollbackActiveModeTransaction", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicateModeTransitionController", bridge, StringComparison.Ordinal);
    }

    private static MarkdownSource Source(string path)
        => Source(path, $"# {path}");

    private static MarkdownSource Source(string path, string content)
        => new(path, System.IO.Path.GetFileName(path), content);

    private sealed class FakeDocumentRevealState : IApplicateDocumentRevealState, IApplicateStartupRevealState
    {
        private MarkdownSource? _document;

        public MarkdownSource? Document
        {
            get => _document;
            set => _document = value;
        }

        public ReadingPreferences ReadingPreferences { get; set; } = ReadingPreferences.Default;

        public bool IsTocPreferredVisible { get; set; }

        public bool HasDocumentHeadings { get; set; }

        public int ClearHeadingsCount { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public event EventHandler? DocumentTransitionStarting;

        public event EventHandler? SuppressNextDocumentReveal;

        public event EventHandler<ThemeTransitionStartingEventArgs>? ThemeTransitionStarting;

        public void SetDocument(MarkdownSource? document)
        {
            _document = document;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Document)));
        }

        public void RaiseDocumentTransitionStarting()
            => DocumentTransitionStarting?.Invoke(this, EventArgs.Empty);

        public void RaiseSuppressNextDocumentReveal()
            => SuppressNextDocumentReveal?.Invoke(this, EventArgs.Empty);

        public void RaiseThemeTransitionStarting(ThemeMode theme)
            => ThemeTransitionStarting?.Invoke(this, new ThemeTransitionStartingEventArgs(theme));

        public void RaisePropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public void ClearDocumentHeadings()
            => ClearHeadingsCount++;
    }

    private sealed class FakeThemeSignals : IApplicateThemeRevealSignals
    {
        public bool HasLoadedDocument { get; set; }

        public MarkdownSource? LastLoadedSource { get; private set; }

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

        public event EventHandler<ApplicateWebThemeChangeSentEventArgs>? ThemeChangeSent;

        public event EventHandler<ApplicateWebThemeAppliedEventArgs>? ThemeApplied;

        public bool HasLoadedDocumentForSource(MarkdownSource? source)
        {
            LastLoadedSource = source;
            return HasLoadedDocument;
        }

        public void RaiseRendererFailed()
            => RendererFailed?.Invoke(
                this,
                new ApplicateRendererFailureEvent(
                    ApplicateRendererFailureKind.DocumentRenderFailed,
                    "document.md",
                    DateTime.UtcNow));

        public void RaiseThemeChangeSent(string theme, long requestId)
            => ThemeChangeSent?.Invoke(this, new ApplicateWebThemeChangeSentEventArgs(theme, requestId));

        public void RaiseThemeApplied(string theme, long requestId)
            => ThemeApplied?.Invoke(this, new ApplicateWebThemeAppliedEventArgs(theme, requestId));
    }

    private sealed class FakeStartupShell : IApplicateStartupRevealShell
    {
        public Control CoverHost { get; } = new Panel();

        public List<double> OpacityAssignments { get; } = [];

        public double Opacity
        {
            get => OpacityAssignments.Count == 0 ? 0 : OpacityAssignments[^1];
            set => OpacityAssignments.Add(value);
        }

        public event EventHandler? Opened;

        public event EventHandler? SizeChanged;

        public event EventHandler? Closed;

        public void RaiseOpened()
            => Opened?.Invoke(this, EventArgs.Empty);

        public void RaiseSizeChanged()
            => SizeChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseClosed()
            => Closed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeStartupSignals : IApplicateStartupRevealSignals
    {
        public int SettleProbeRequestCount { get; private set; }

        public TimeSpan RendererSettleFallbackTimeout => ApplicateSharedWebViewHost.RendererSettleFallbackTimeout;

        public event EventHandler? DocumentRevealReady;

        public event EventHandler<IReadOnlyList<DocumentHeading>>? HeadingsChanged;

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

        public event EventHandler? RendererSettled;

        public bool ShouldSkipRendererFrameWait(MarkdownSource? source, long transactionGeneration)
            => ApplicateSharedWebViewHost.ShouldSkipRendererFrameWait(source, transactionGeneration);

        public void RequestRendererSettleProbe()
            => SettleProbeRequestCount++;

        public void RaiseDocumentRevealReady()
            => DocumentRevealReady?.Invoke(this, EventArgs.Empty);

        public void RaiseHeadingsChanged(IReadOnlyList<DocumentHeading> headings)
            => HeadingsChanged?.Invoke(this, headings);

        public void RaiseRendererFailed()
            => RendererFailed?.Invoke(
                this,
                new ApplicateRendererFailureEvent(
                    ApplicateRendererFailureKind.DocumentRenderFailed,
                    "document.md",
                    DateTime.UtcNow));

        public void RaiseRendererSettled()
            => RendererSettled?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeDocumentSignals : IApplicateDocumentRevealSignals
    {
        public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

        public event EventHandler? DocumentRevealReady;

        public void RaiseCommitCompleted(long transactionGeneration, ApplicateMode mode)
            => CommitCompleted?.Invoke(
                this,
                new ApplicateCommitCompletedEventArgs(
                    mode,
                    new Rect(0, 0, 800, 600),
                    transactionGeneration));

        public void RaiseDocumentRevealReady()
            => DocumentRevealReady?.Invoke(this, EventArgs.Empty);

        public void RaiseRendererFailed()
            => RendererFailed?.Invoke(
                this,
                new ApplicateRendererFailureEvent(
                    ApplicateRendererFailureKind.DocumentRenderFailed,
                    "document.md",
                    DateTime.UtcNow));
    }

    private sealed class FakeModeSlotAdapter(List<string>? events = null) : IApplicateModeTransitionSlotAdapter
    {
        private readonly List<string>? _events = events;

        public ApplicateModeSlotState ViewerState { get; private set; }

        public ApplicateModeSlotState EditState { get; private set; }

        public long ViewerGeneration { get; private set; }

        public long EditGeneration { get; private set; }

        public ApplicateMode? CommittedMode { get; private set; }

        public int ClearGenerationCount { get; private set; }

        public int ReconcileRequestCount { get; private set; }

        public bool TargetLayoutSettled { get; set; } = true;

        public (double ViewerOpacity, double EditOpacity) RevealSnapshot()
            => (ViewerState.Opacity, EditState.Opacity);

        public void ApplyTransactionGenerationContext(ApplicateMode requestedMode, long generation)
        {
            ViewerGeneration = requestedMode == ApplicateMode.Viewer ? generation : 0;
            EditGeneration = requestedMode == ApplicateMode.Edit ? generation : 0;
            _events?.Add($"generation:{requestedMode}:{generation}");
        }

        public void ClearTransactionGenerationContext()
        {
            ViewerGeneration = 0;
            EditGeneration = 0;
            ClearGenerationCount++;
            _events?.Add("generation:clear");
        }

        public void ApplyTransactionalModeState(
            ApplicateMode requestedMode,
            ApplicateModeSlotState viewer,
            ApplicateModeSlotState edit)
        {
            ViewerState = viewer;
            EditState = edit;
            _events?.Add($"slot-transaction:{requestedMode}");
        }

        public void ApplyCommittedModeState(ApplicateMode mode, bool applySlotState)
        {
            CommittedMode = mode;
            if (applySlotState)
            {
                ViewerState = new ApplicateModeSlotState(
                    mode == ApplicateMode.Viewer,
                    mode == ApplicateMode.Viewer,
                    mode == ApplicateMode.Viewer ? 1.0 : 0.0);
                EditState = new ApplicateModeSlotState(
                    mode == ApplicateMode.Edit,
                    mode == ApplicateMode.Edit,
                    mode == ApplicateMode.Edit ? 1.0 : 0.0);
            }
            _events?.Add($"slot-committed:{mode}:apply={applySlotState}");
        }

        public bool IsModeSlotLayoutSettled(ApplicateMode mode)
            => TargetLayoutSettled;

        public void ReconcileModeTransition()
        {
            ReconcileRequestCount++;
            _events?.Add("reconcile-requested");
        }
    }

    private sealed class FakeModeHostRevealIntents(
        Func<(double ViewerOpacity, double EditOpacity)> slotSnapshot,
        List<string>? events = null) : IApplicateHostRevealIntents
    {
        public readonly record struct RevealSnapshot(
            long Generation,
            double ViewerOpacity,
            double EditOpacity);

        private readonly List<string>? _events = events;

        public List<RevealSnapshot> RevealedGenerations { get; } = [];

        public List<ApplicateMode> SuppressedModes { get; } = [];

        public List<ApplicateMode> RestoredModes { get; } = [];

        public bool RejectReveals { get; set; }

        public bool ThrowOnRestore { get; set; }

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

        public event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled;

        public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

        public event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled;

        public void SuppressOutgoingNativeRenderer(ApplicateMode displayedMode)
        {
            SuppressedModes.Add(displayedMode);
            _events?.Add($"suppress:{displayedMode}");
        }

        public void RestoreOutgoingNativeRenderer(ApplicateMode displayedMode)
        {
            RestoredModes.Add(displayedMode);
            _events?.Add($"restore:{displayedMode}");
            if (ThrowOnRestore)
            {
                throw new InvalidOperationException("restore failed");
            }
        }

        public bool RevealNativeRendererForCommittedTransaction(long transactionGeneration)
        {
            var snapshot = slotSnapshot();
            RevealedGenerations.Add(
                new RevealSnapshot(
                    transactionGeneration,
                    snapshot.ViewerOpacity,
                    snapshot.EditOpacity));
            _events?.Add($"reveal:{transactionGeneration}");
            return !RejectReveals;
        }

        public void RaiseCommitCompleted(long generation, ApplicateMode mode)
            => CommitCompleted?.Invoke(
                this,
                new ApplicateCommitCompletedEventArgs(
                    mode,
                    new Rect(0, 0, 800, 600),
                    generation));

        public void RaiseMinimapSettledNotApplicable(long generation)
            => MinimapSettled?.Invoke(
                this,
                ApplicateMinimapSettledEventArgs.NotApplicable(generation));

        public void RaiseMinimapSettled(long generation, bool visible = true, double reservedWidth = 168)
            => MinimapSettled?.Invoke(
                this,
                new ApplicateMinimapSettledEventArgs(
                    generation,
                    new ApplicateWebMinimapStateEventArgs(visible, reservedWidth)));

        public void RaiseRendererSettled(long generation)
            => RendererSettled?.Invoke(
                this,
                new ApplicateRendererSettledEventArgs(generation));

        public void RaiseRendererFailed()
            => RendererFailed?.Invoke(
                this,
                new ApplicateRendererFailureEvent(
                    ApplicateRendererFailureKind.DocumentRenderFailed,
                    DocumentPath: null,
                    DateTime.UtcNow));
    }

    private sealed class FakeCoverFactory(List<string>? events = null)
    {
        private readonly List<string>? _events = events;

        public List<FakeCoverPresenter> Created { get; } = [];

        public FakeCoverPresenter Create()
        {
            var presenter = new FakeCoverPresenter(_events);
            Created.Add(presenter);
            return presenter;
        }
    }

    private sealed class FakeCoverPresenter(List<string>? events = null) : IApplicateAirspaceCoverPresenter
    {
        private readonly List<string>? _events = events;

        public int ShowCount { get; private set; }

        public int StartupSplashShowCount { get; private set; }

        public string? StartupSplashDocumentName { get; private set; }

        public ThemeVariant? LastShowThemeVariant { get; private set; }

        public int UpdateBrushCount { get; private set; }

        public ThemeVariant? LastUpdateThemeVariant { get; private set; }

        public int HideImmediateCount { get; private set; }

        public int HideAnimatedCount { get; private set; }

        public TimeSpan? LastHideDuration { get; private set; }

        public bool Show(Control host)
        {
            ShowCount++;
            _events?.Add("cover-show");
            return true;
        }

        public bool Show(Control host, ThemeVariant? themeVariant)
        {
            ShowCount++;
            LastShowThemeVariant = themeVariant;
            _events?.Add("cover-show");
            return true;
        }

        public bool ShowStartupSplash(Control host, string? documentName)
        {
            StartupSplashShowCount++;
            StartupSplashDocumentName = documentName;
            _events?.Add("cover-show-startup");
            return true;
        }

        public bool UpdateBrush(Control host, ThemeVariant? themeVariant)
        {
            UpdateBrushCount++;
            LastUpdateThemeVariant = themeVariant;
            return true;
        }

        public void Hide()
        {
            HideImmediateCount++;
            _events?.Add("cover-hide:immediate");
        }

        public void Hide(TimeSpan duration)
        {
            LastHideDuration = duration;
            if (duration <= TimeSpan.Zero)
            {
                HideImmediateCount++;
                _events?.Add("cover-hide:immediate");
                return;
            }

            HideAnimatedCount++;
            _events?.Add("cover-hide:animated");
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePaintGate : IApplicateAirspacePaintGate
    {
        private readonly List<Action> _pending = [];

        public int PendingCount => _pending.Count;

        public void AfterTwoFrames(Control anchor, Action action)
            => _pending.Add(action);

        public void Flush()
        {
            var pending = _pending.ToArray();
            _pending.Clear();
            foreach (var action in pending)
            {
                action();
            }
        }
    }

    private sealed class FakeAirspaceScheduler : IApplicateAirspaceScheduler
    {
        private readonly Queue<Action> _posted = [];
        private readonly List<FakeAirspaceTimer> _timers = [];

        public int PendingCount => _posted.Count;

        public void Post(Action action, DispatcherPriority priority)
            => _posted.Enqueue(action);

        public IApplicateAirspaceTimer CreateTimer(TimeSpan interval, EventHandler tick)
        {
            var timer = new FakeAirspaceTimer(interval, tick);
            _timers.Add(timer);
            return timer;
        }

        public void Flush()
        {
            var pending = _posted.ToArray();
            _posted.Clear();
            foreach (var action in pending)
            {
                action();
            }
        }

        public void FireTimer(TimeSpan interval)
        {
            var timer = Assert.Single(
                _timers,
                candidate => candidate.Interval == interval && candidate.IsStarted);
            timer.Fire();
        }

        public int ActiveTimerCount(TimeSpan interval)
            => _timers.Count(candidate => candidate.Interval == interval && candidate.IsStarted);
    }

    private sealed class FakeAirspaceTimer(TimeSpan interval, EventHandler tick) : IApplicateAirspaceTimer
    {
        private readonly EventHandler _tick = tick;

        public TimeSpan Interval { get; } = interval;

        public bool IsStarted { get; private set; }

        public void Start()
            => IsStarted = true;

        public void Stop()
            => IsStarted = false;

        public void Dispose()
            => Stop();

        public void Fire()
        {
            if (IsStarted)
            {
                _tick(this, EventArgs.Empty);
            }
        }
    }
}

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateAirspaceCompositorTests
{
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

    private static MarkdownSource Source(string path)
        => new(path, System.IO.Path.GetFileName(path), $"# {path}");

    private sealed class FakeDocumentRevealState : IApplicateDocumentRevealState
    {
        private MarkdownSource? _document;

        public MarkdownSource? Document
        {
            get => _document;
            set => _document = value;
        }

        public ReadingPreferences ReadingPreferences { get; set; } = ReadingPreferences.Default;

        public int ClearHeadingsCount { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public event EventHandler? DocumentTransitionStarting;

        public event EventHandler? SuppressNextDocumentReveal;

        public void SetDocument(MarkdownSource? document)
        {
            _document = document;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Document)));
        }

        public void RaiseDocumentTransitionStarting()
            => DocumentTransitionStarting?.Invoke(this, EventArgs.Empty);

        public void RaiseSuppressNextDocumentReveal()
            => SuppressNextDocumentReveal?.Invoke(this, EventArgs.Empty);

        public void ClearDocumentHeadings()
            => ClearHeadingsCount++;
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

    private sealed class FakeCoverFactory
    {
        public List<FakeCoverPresenter> Created { get; } = [];

        public FakeCoverPresenter Create()
        {
            var presenter = new FakeCoverPresenter();
            Created.Add(presenter);
            return presenter;
        }
    }

    private sealed class FakeCoverPresenter : IApplicateAirspaceCoverPresenter
    {
        public int ShowCount { get; private set; }

        public int HideImmediateCount { get; private set; }

        public int HideAnimatedCount { get; private set; }

        public bool Show(Control host)
        {
            ShowCount++;
            return true;
        }

        public void Hide()
            => HideImmediateCount++;

        public void Hide(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                HideImmediateCount++;
                return;
            }

            HideAnimatedCount++;
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
}

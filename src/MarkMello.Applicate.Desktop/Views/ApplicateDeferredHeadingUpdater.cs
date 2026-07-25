using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop.Views;

internal sealed class ApplicateDeferredHeadingUpdater
{
    private const int LargeHeadingUpdateThreshold = 250;
    private static readonly TimeSpan LargeHeadingFlushDelay = TimeSpan.FromMilliseconds(80);
    private int _version;
    private int _pendingVersion;
    private DocumentHeading[]? _pendingHeadings;
    private MainWindowViewModel? _pendingViewModel;
    private Func<bool>? _pendingCanApply;

    public void Invalidate()
    {
        _version = unchecked(_version + 1);
        ClearPending();
    }

    public void Apply(
        IReadOnlyList<DocumentHeading> headings,
        MainWindowViewModel viewModel,
        Func<bool> canApply,
        bool deferLargeUntilExplicitFlush = true)
    {
        ArgumentNullException.ThrowIfNull(headings);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(canApply);

        var version = unchecked(_version + 1);
        _version = version;

        if (!ShouldDefer(headings))
        {
            // toc-seq diagnostic (2026-07-25 bug: TOC intermittently empty on
            // open). Proves the immediate branch was actually taken rather
            // than assumed (research.md already ruled this class out for the
            // reporting user's file sizes, but the assumption itself needs a
            // runtime witness). Temporary — swept in the same commit cycle
            // as the eventual fix (ApplicateTrace.cs Diag facility contract).
            ApplicateTrace.DiagMs(
                "toc-seq",
                "updater-decision",
                $"decision=immediate count={headings.Count} defer={deferLargeUntilExplicitFlush} version={version}");
            ClearPending();
            viewModel.UpdateDocumentHeadings(headings);
            return;
        }

        var snapshot = headings.ToArray();
        if (!deferLargeUntilExplicitFlush && headings.Count >= LargeHeadingUpdateThreshold)
        {
            ApplicateTrace.DiagMs(
                "toc-seq",
                "updater-decision",
                $"decision=scheduled count={headings.Count} defer={deferLargeUntilExplicitFlush} version={version}");
            ClearPending();
            ScheduleApply(version, snapshot, viewModel, canApply);
            return;
        }

        _pendingVersion = version;
        _pendingHeadings = snapshot;
        _pendingViewModel = viewModel;
        _pendingCanApply = canApply;
        ApplicateTrace.DiagMs(
            "toc-seq",
            "updater-decision",
            $"decision=parked count={headings.Count} defer={deferLargeUntilExplicitFlush} version={version}");
    }

    private static bool ShouldDefer(IReadOnlyList<DocumentHeading> headings)
        => headings.Count == 0 || headings.Count >= LargeHeadingUpdateThreshold;

    public void FlushPending()
    {
        var version = _pendingVersion;
        var snapshot = _pendingHeadings;
        var viewModel = _pendingViewModel;
        var canApply = _pendingCanApply;
        ClearPending();
        if (snapshot is null || viewModel is null || canApply is null)
        {
            return;
        }

        ScheduleApply(version, snapshot, viewModel, canApply);
    }

    private void ScheduleApply(
        int version,
        IReadOnlyList<DocumentHeading> snapshot,
        MainWindowViewModel viewModel,
        Func<bool> canApply)
    {
        _ = Task.Delay(LargeHeadingFlushDelay).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (_version != version || !canApply())
                {
                    return;
                }

                viewModel.UpdateDocumentHeadings(snapshot);
            }, DispatcherPriority.Background),
            TaskScheduler.Default);
    }

    private void ClearPending()
    {
        _pendingHeadings = null;
        _pendingViewModel = null;
        _pendingCanApply = null;
        _pendingVersion = 0;
    }
}

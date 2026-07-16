using System;
using System.Linq;
using Avalonia.Controls;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Presentation.Views;

namespace MarkMello.Applicate.Desktop;

internal sealed class ApplicateMountPoints
{
    private const string DiagnosticGroup = "mount-points";

    /// <summary>
    /// Name of the viewer slot anchor in MainWindow.axaml. The fork owns that file, so a missing
    /// anchor is an integration break (an upstream merge dropping the Name), never a runtime state.
    /// </summary>
    private const string ViewerContentSlotName = "ViewerContentSlot";

    private readonly Action<string, string, string> _emitDiagnostic;
    private readonly Panel? _bodyPanel;
    private bool _viewerContentSlotResolved;
    private ContentControl? _viewerContentSlot;

    public ApplicateMountPoints(Panel? bodyPanel)
        : this(bodyPanel, ApplicateTrace.DiagMs)
    {
    }

    internal ApplicateMountPoints(
        Panel? bodyPanel,
        Action<string, string, string> emitDiagnostic)
    {
        _bodyPanel = bodyPanel;
        _emitDiagnostic = emitDiagnostic;
        if (_bodyPanel is null)
        {
            EmitMissing("body-panel");
            throw MissingAnchor("body-panel", "BodyPanel", "MainWindow.axaml");
        }
    }

    public static ApplicateMountPoints Resolve(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return new ApplicateMountPoints(root.FindControl<Panel>("BodyPanel"));
    }

    public Panel? BodyPanel => _bodyPanel;

    /// <summary>
    /// Cached on first touch, which happens BEFORE the tabs install clears and
    /// re-parents BodyPanel's children (the slot then lives outside BodyPanel
    /// for the window's lifetime). A post-install consumer re-resolving from
    /// BodyPanel would find nothing — consume this cached reference only.
    /// </summary>
    public ContentControl? ViewerContentSlot
    {
        get
        {
            if (!_viewerContentSlotResolved)
            {
                _viewerContentSlotResolved = true;
                // Matched by NAME, not by shape. The old heuristic ("first unnamed ContentControl
                // whose type is exactly ContentControl") silently resolved the wrong control — or
                // nothing — the moment upstream reordered BodyPanel's children, named that control,
                // or added another bare ContentControl above it.
                _viewerContentSlot = _bodyPanel?.Children
                    .OfType<ContentControl>()
                    .FirstOrDefault(static control =>
                        string.Equals(control.Name, ViewerContentSlotName, StringComparison.Ordinal));
                if (_viewerContentSlot is null)
                {
                    EmitMissing("viewer-content-slot");
                }
            }

            if (_viewerContentSlot is null)
            {
                throw MissingAnchor("viewer-content-slot", ViewerContentSlotName, "MainWindow.axaml");
            }

            return _viewerContentSlot;
        }
    }

    public ApplicateEditPreviewMountPoints ResolveEditPreviewMountPoints(
        EditWorkspaceView editWorkspace,
        Control replacementPreview)
    {
        ArgumentNullException.ThrowIfNull(editWorkspace);
        ArgumentNullException.ThrowIfNull(replacementPreview);

        var nativePreviewDocumentView =
            editWorkspace.FindControl<MarkdownDocumentView>("PreviewDocumentView");
        if (nativePreviewDocumentView is null)
        {
            EmitMissing("preview-document-view");
            throw MissingAnchor("preview-document-view", "PreviewDocumentView", "EditWorkspaceView.axaml");
        }

        var namedFrame = editWorkspace.FindControl<Border>("PreviewDocumentFrame");
        if (namedFrame is null)
        {
            EmitMissing("preview-document-frame");
        }

        var usedPreviewDocumentFrameFallback = namedFrame is null;
        var previewDocumentFrame = namedFrame ?? nativePreviewDocumentView.Parent as Border;
        if (previewDocumentFrame is null)
        {
            // Both routes exhausted. The EFFECTIVE frame is load-bearing even though the NAMED one
            // is not: without it the caller silently skips mounting the edit preview, which is the
            // same silent-disable this fail-fast exists to kill.
            EmitMissing("preview-document-view-parent-frame");
            throw MissingPreviewFrame();
        }

        if (namedFrame is null)
        {
            EmitFallback(
                "preview-document-frame",
                "preview-document-view-parent");
        }

        var previewSourceLineSync = replacementPreview as ISourceLineScrollSyncPreview
            ?? previewDocumentFrame.Child as ISourceLineScrollSyncPreview;
        if (previewSourceLineSync is null)
        {
            // Optional by contract: the edit workspace's sync-target setter accepts null.
            EmitMissing("preview-source-line-sync");
        }

        return new ApplicateEditPreviewMountPoints(
            previewDocumentFrame,
            nativePreviewDocumentView,
            previewSourceLineSync,
            usedPreviewDocumentFrameFallback);
    }

    /// <summary>
    /// Fail-fast for a NAMED load-bearing anchor (body-panel, viewer-content-slot,
    /// preview-document-view). The fork owns the .axaml files these live in, so a miss is an
    /// integration break — an upstream merge dropping a Name= — not a runtime condition. Previously
    /// each miss only emitted a diagnostic and returned null, and the consumers quietly returned, so
    /// the viewer/edit surface silently never mounted and the app merely looked broken.
    /// NOT used for the NAMED preview-document-frame: that one has a DESIGNED parent-Border fallback
    /// (the real EditWorkspaceView.axaml ships an unnamed parent Border), so a miss there is
    /// recoverable. The EFFECTIVE frame is still load-bearing — see <see cref="MissingPreviewFrame"/>.
    /// </summary>
    private static InvalidOperationException MissingAnchor(string anchor, string name, string file)
        => new(
            $"Applicate mount point '{anchor}' is missing: no control named '{name}' was found in {file}. "
            + "The fork mounts into that anchor; an upstream merge that renamed or removed it must be "
            + "reconciled rather than silently disabling the surface.");

    /// <summary>
    /// Fail-fast when BOTH routes to the edit-preview frame are exhausted. Distinct from
    /// <see cref="MissingAnchor"/>: this is not one missing Name= but the loss of the named frame AND
    /// the parent-Border relationship the fallback depends on, so the message must name both.
    /// </summary>
    private static InvalidOperationException MissingPreviewFrame()
        => new(
            "Applicate mount point 'preview-document-view-parent-frame' is missing: EditWorkspaceView.axaml "
            + "has no Border named 'PreviewDocumentFrame', and PreviewDocumentView's parent is not a Border "
            + "either, so both routes to the edit-preview frame are gone. The fork mounts the edit preview "
            + "into that frame; without it the edit surface would silently never mount.");

    private void EmitMissing(string anchor)
        => _emitDiagnostic(DiagnosticGroup, "mount-point-miss", $"anchor={anchor}");

    private void EmitFallback(string anchor, string fallback)
        => _emitDiagnostic(
            DiagnosticGroup,
            "mount-point-fallback",
            $"anchor={anchor} fallback={fallback}");
}

internal sealed record ApplicateEditPreviewMountPoints(
    Border? PreviewDocumentFrame,
    MarkdownDocumentView? NativePreviewDocumentView,
    ISourceLineScrollSyncPreview? PreviewSourceLineSync,
    bool UsedPreviewDocumentFrameFallback);

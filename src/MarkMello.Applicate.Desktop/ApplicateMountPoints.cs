using System;
using System.Linq;
using Avalonia.Controls;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Presentation.Views;

namespace MarkMello.Applicate.Desktop;

internal sealed class ApplicateMountPoints
{
    private const string DiagnosticGroup = "mount-points";

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
                _viewerContentSlot = _bodyPanel?.Children
                    .OfType<ContentControl>()
                    .FirstOrDefault(static control =>
                        control.GetType() == typeof(ContentControl)
                        && control.Name is null);
                if (_viewerContentSlot is null)
                {
                    EmitMissing("viewer-content-slot");
                }
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
        }

        var namedFrame = editWorkspace.FindControl<Border>("PreviewDocumentFrame");
        if (namedFrame is null)
        {
            EmitMissing("preview-document-frame");
        }

        var usedPreviewDocumentFrameFallback = namedFrame is null;
        var previewDocumentFrame = namedFrame ?? nativePreviewDocumentView?.Parent as Border;
        if (previewDocumentFrame is null)
        {
            EmitMissing("preview-document-view-parent-frame");
        }
        else if (namedFrame is null)
        {
            EmitFallback(
                "preview-document-frame",
                "preview-document-view-parent");
        }

        // No frame => the preview is never mounted; pinning a sync target to an
        // orphaned preview would let the editor scroll the live shared WebView
        // host behind the scenes (fable gate B2). Match the old behavior: the
        // visual-descendants scan found nothing, so the sync target is null.
        var previewSourceLineSync = previewDocumentFrame is null
            ? null
            : replacementPreview as ISourceLineScrollSyncPreview
              ?? previewDocumentFrame.Child as ISourceLineScrollSyncPreview;
        if (previewSourceLineSync is null)
        {
            EmitMissing("preview-source-line-sync");
        }

        return new ApplicateEditPreviewMountPoints(
            previewDocumentFrame,
            nativePreviewDocumentView,
            previewSourceLineSync,
            usedPreviewDocumentFrameFallback);
    }

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

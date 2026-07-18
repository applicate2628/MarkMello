using System;
using System.Threading.Tasks;
using MarkMello.Application.UseCases;
using MarkMello.Domain;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Task-checkbox edit strategy. It keeps the task marker identity validation
/// and the legacy blanket exception contract together.
/// </summary>
internal sealed class TaskCheckboxEditKind : IInDocumentEditKind
{
    private readonly IRealtimeInDocumentEditHost _host;
    private readonly int _line;
    private readonly bool _isChecked;
    private readonly string? _expectedKey;
    private readonly TaskToggleOrigin _origin;

    public TaskCheckboxEditKind(
        IRealtimeInDocumentEditHost host,
        int line,
        bool isChecked,
        string? expectedKey,
        TaskToggleOrigin origin)
    {
        _host = host;
        _line = line;
        _isChecked = isChecked;
        _expectedKey = expectedKey;
        _origin = origin;
    }

    // Existing task-toggle busy behavior is a silent drop.
    public void PublishBusy()
    {
    }

    public Task ApplyAsync()
    {
        try
        {
            if (_origin == TaskToggleOrigin.EditPreview)
            {
                if (_host.EditorSession is not { } session)
                {
                    _host.PublishEditPreviewTaskToggleRevert(
                        new TaskToggleRevertRequest(
                            _line,
                            !_isChecked,
                            _host.CurrentDocumentPath ?? string.Empty));
                    return Task.CompletedTask;
                }

                if (TryFlipMarker(
                    session.SourceText,
                    _line,
                    _isChecked,
                    _expectedKey,
                    out var editedBuffer,
                    out var markerOffset))
                {
                    _host.PublishEditPreviewTaskToggleCommit(new TaskToggleCommit(
                        new MarkdownSource(session.CurrentPath ?? string.Empty, session.FileName, editedBuffer),
                        _line,
                        _isChecked)
                    {
                        Start = markerOffset,
                        Length = 1,
                        Replacement = _isChecked ? "x" : " ",
                    });
                    return Task.CompletedTask;
                }

                _host.PublishEditPreviewTaskToggleRevert(
                    new TaskToggleRevertRequest(
                        _line,
                        ReadCheckedState(session.SourceText, _line, !_isChecked),
                        session.CurrentPath ?? string.Empty));
                return Task.CompletedTask;
            }

            // Reading-mode (Viewer) leg: resolve the in-memory source (session
            // buffer when a session exists, else the rendered document) and flip
            // it as an UNSAVED edit. No per-edit disk read, no disk write, no
            // reload branch — the coordinator serialized us and Ctrl+S owns the
            // write. A refused flip surgically restores the one checkbox.
            var path = _host.CurrentDocumentPath;
            var source = _host.EditorSession?.SourceText ?? _host.CurrentDocument?.Content;
            if (source is null)
            {
                return Task.CompletedTask;
            }

            if (TryFlipMarker(source, _line, _isChecked, _expectedKey, out var newBuffer))
            {
                _host.CommitInPlaceTaskFlip(newBuffer, _line, _isChecked);
                return Task.CompletedTask;
            }

            _host.PublishTaskToggleRevert(
                new TaskToggleRevertRequest(
                    _line,
                    ReadCheckedState(source, _line, !_isChecked),
                    path ?? string.Empty));
        }
        catch (Exception)
        {
            // Preserve the task-list channel's legacy blanket-catch contract:
            // ToggleTaskLineAsync must never throw to the caller.
        }

        return Task.CompletedTask;
    }

    internal static bool TryFlipMarker(
        string content,
        int line,
        bool isChecked,
        string? expectedKey,
        out string newContent)
        => TryFlipMarker(content, line, isChecked, expectedKey, out newContent, out _);

    internal static bool TryFlipMarker(
        string content,
        int line,
        bool isChecked,
        string? expectedKey,
        out string newContent,
        out int markerOffset)
    {
        newContent = content;
        markerOffset = -1;
        if (string.IsNullOrEmpty(content) || line < 0 || string.IsNullOrEmpty(expectedKey))
        {
            return false;
        }

        var lines = content.Split('\n');
        if (line >= lines.Length)
        {
            return false;
        }

        var match = TaskListIdentity.TaskMarkerPattern.Match(lines[line]);
        if (!match.Success)
        {
            return false;
        }

        if (!string.Equals(TaskListIdentity.ComputeKey(lines[line]), expectedKey, StringComparison.Ordinal))
        {
            return false;
        }

        var currentChecked = !string.Equals(match.Groups[2].Value, " ", StringComparison.Ordinal);
        if (currentChecked == isChecked)
        {
            return false;
        }

        var lineStart = 0;
        for (var index = 0; index < line; index++)
        {
            lineStart += lines[index].Length + 1;
        }

        markerOffset = lineStart + match.Groups[2].Index;
        newContent = content.Remove(markerOffset, 1).Insert(markerOffset, isChecked ? "x" : " ");
        return true;
    }

    private static bool ReadCheckedState(string content, int line, bool fallback)
    {
        var lines = content.Split('\n');
        if (line < 0 || line >= lines.Length)
        {
            return fallback;
        }

        var match = TaskListIdentity.TaskMarkerPattern.Match(lines[line]);
        return match.Success
            ? !string.Equals(match.Groups[2].Value, " ", StringComparison.Ordinal)
            : fallback;
    }
}

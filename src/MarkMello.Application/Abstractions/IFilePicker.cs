namespace MarkMello.Application.Abstractions;

public sealed record FileSavePickerSpec(
    string Title,
    string SuggestedFileName,
    string DefaultExtension,
    string FileTypeName,
    IReadOnlyList<string> Patterns);

/// <summary>
/// Системный файловый picker. Реализация — в Presentation, потому что зависит от Avalonia
/// (TopLevel.StorageProvider). VM зовёт через интерфейс — без знания про UI framework.
/// </summary>
public interface IFilePicker
{
    /// <summary>Открыть picker для выбора одного markdown-файла. Возвращает null, если отменено.</summary>
    Task<string?> PickMarkdownFileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Открыть системный Save As picker для markdown-файла.
    /// Возвращает null, если пользователь отменил сохранение.
    /// </summary>
    Task<string?> PickSaveMarkdownFileAsync(string suggestedFileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a framework-neutral Save As picker described by <paramref name="spec"/>.
    /// Returns null when the picker is unavailable or the user cancels it.
    /// </summary>
    Task<string?> PickSaveFileAsync(
        FileSavePickerSpec spec,
        CancellationToken cancellationToken = default);
}

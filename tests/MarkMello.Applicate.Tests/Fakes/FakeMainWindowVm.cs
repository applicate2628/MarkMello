using System.ComponentModel;
using System.Threading.Tasks;
using MarkMello.Domain;

namespace MarkMello.Applicate.Tests.Fakes;

internal sealed class FakeMainWindowVm : INotifyPropertyChanged
{
    private bool _isViewer;
    private bool _isEditMode;
    private object? _editorSession;
    private object? _document;
    private ReadingPreferences _readingPreferences = ReadingPreferences.Default;

    public bool IsViewer
    {
        get => _isViewer;
        set { if (_isViewer != value) { _isViewer = value; Fire(nameof(IsViewer)); } }
    }

    public bool IsEditMode
    {
        get => _isEditMode;
        set { if (_isEditMode != value) { _isEditMode = value; Fire(nameof(IsEditMode)); } }
    }

    public object? EditorSession
    {
        get => _editorSession;
        set { if (!ReferenceEquals(_editorSession, value)) { _editorSession = value; Fire(nameof(EditorSession)); } }
    }

    public object? Document
    {
        get => _document;
        set { if (!ReferenceEquals(_document, value)) { _document = value; Fire(nameof(Document)); } }
    }

    public ReadingPreferences ReadingPreferences
    {
        get => _readingPreferences;
        set { if (_readingPreferences != value) { _readingPreferences = value; Fire(nameof(ReadingPreferences)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Fire(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Task FireFromBackgroundThreadAsync(string propertyName) =>
        Task.Run(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
}

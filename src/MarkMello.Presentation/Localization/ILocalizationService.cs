using System.ComponentModel;
using System.Globalization;
using MarkMello.Domain;

namespace MarkMello.Presentation.Localization;

public interface ILocalizationService : INotifyPropertyChanged
{
    AppLanguage SelectedLanguage { get; }

    AppLanguage EffectiveLanguage { get; }

    CultureInfo Culture { get; }

    string this[string key] { get; }

    string Format(string key, params object?[] args);

    void SetLanguage(AppLanguage language);
}

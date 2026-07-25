using System.Reflection;
using MarkMello.Domain;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Tests;

/// <summary>
/// Recent-files DELTA (P3, extended by the P5 cascade replacement): every localization key this
/// delta adds must resolve in both languages, be exposed as a public read-only VM property of the
/// same name, and be registered in <c>LocalizedBindingPropertyNames</c> -- an unregistered
/// property renders stale after a language switch (the welcome row and the app-menu cascade
/// panel bind these properties).
/// </summary>
public sealed class RecentFilesLocalizationTests
{
    private static readonly string[] RecentFilesLocalizationProperties =
    [
        "AppMenuRecentHeader",
        "AppMenuRecentHint",
        "AppRecentHeader",
        "OverlayCloseRecent",
        "RecentClearLabel",
        "RecentClearHint",
        "RecentRemoveEntryTooltip",
    ];

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Russian)]
    public void RecentFilesKeysResolveAsRegisteredNonEmptyViewModelProperties(AppLanguage language)
    {
        var localization = new LocalizationService(language);
        var viewModelType = typeof(MainWindowViewModel);
        var localizedNamesField = viewModelType.GetField(
            "LocalizedBindingPropertyNames",
            BindingFlags.Static | BindingFlags.NonPublic);
        var localizedNames = Assert.IsType<string[]>(localizedNamesField?.GetValue(null));

        foreach (var propertyName in RecentFilesLocalizationProperties)
        {
            var resolved = localization[propertyName];
            Assert.False(string.IsNullOrEmpty(resolved), $"{propertyName} should resolve to a non-empty string.");
            Assert.DoesNotContain("[[", resolved, StringComparison.Ordinal);

            var property = viewModelType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(property);
            Assert.True(property!.CanRead);
            Assert.False(property.CanWrite);
            Assert.Contains(propertyName, localizedNames);
        }
    }
}

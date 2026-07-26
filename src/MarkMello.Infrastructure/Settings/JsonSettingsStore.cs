using System.Text.Json;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using MarkMello.Infrastructure.Serialization;

namespace MarkMello.Infrastructure.Settings;

/// <summary>
/// JSON-backed settings store for M4. Reads and writes a tiny settings file
/// from the platform config directory and falls back to safe defaults if the
/// file is missing or corrupted.
/// <para>
/// It distinguishes "I looked and there is nothing usable there" from "I could not look at all"
/// (decision d13, clause 2). Every single-field setter serializes the WHOLE file, so three of the
/// four persisted values always come from the in-memory baseline; writing that file after a read
/// this store could not perform would replace the user's real theme, reading preferences, language
/// and window placement with fabricated defaults. So an unobservable read neither latches nor
/// authorizes a write -- it is simply retried on the next call.
/// </para>
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly Lock _gate = new();
    private readonly string _settingsFilePath;

    /// <summary>
    /// True once the persisted baseline was actually OBSERVED -- including the observations that
    /// legitimately found nothing (absent file, absent directory, blank, JSON <c>null</c>,
    /// unparseable). Deliberately NOT set by a read that failed to happen: that is the difference
    /// between caching a fact and latching a forgery for the lifetime of the process.
    /// </summary>
    private bool _hasObservedBaseline;
    private ReadingPreferences _preferences = ReadingPreferences.Default;
    private ThemeMode _theme = ThemeMode.System;
    private AppLanguage _language = AppLanguage.System;
    private WindowPlacement? _windowPlacement;

    public JsonSettingsStore(string? settingsRootDirectory = null)
    {
        var rootDirectory = ResolveSettingsRootDirectory(settingsRootDirectory);
        _settingsFilePath = Path.Combine(rootDirectory, "settings.json");
    }

    public ValueTask<ReadingPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            EnsureObservedCore();
            return ValueTask.FromResult(_preferences);
        }
    }

    public ValueTask SavePreferencesAsync(ReadingPreferences preferences, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!EnsureObservedCore())
            {
                return ValueTask.CompletedTask;
            }

            _preferences = ReadingPreferences.Normalize(preferences);
            PersistCore();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ThemeMode> LoadThemeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            EnsureObservedCore();
            return ValueTask.FromResult(_theme);
        }
    }

    public ValueTask SaveThemeAsync(ThemeMode theme, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!EnsureObservedCore())
            {
                return ValueTask.CompletedTask;
            }

            _theme = NormalizeTheme(theme);
            PersistCore();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<AppLanguage> LoadLanguageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            EnsureObservedCore();
            return ValueTask.FromResult(_language);
        }
    }

    public ValueTask SaveLanguageAsync(AppLanguage language, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!EnsureObservedCore())
            {
                return ValueTask.CompletedTask;
            }

            _language = NormalizeLanguage(language);
            PersistCore();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<WindowPlacement?> LoadWindowPlacementAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            EnsureObservedCore();
            return ValueTask.FromResult(_windowPlacement);
        }
    }

    public ValueTask SaveWindowPlacementAsync(
        WindowPlacement? placement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!EnsureObservedCore())
            {
                return ValueTask.CompletedTask;
            }

            _windowPlacement = WindowPlacement.Normalize(placement);
            PersistCore();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // Deliberately NOT gated on observation, and this is the one write that must not be.
            // Every value below is a constant: Reset takes nothing from the baseline, so it cannot
            // fabricate a field out of one it never read, and the file it produces is identical
            // whether or not the previous contents were legible. Gating it would remove the only
            // in-app way back from a settings file this process cannot read -- trading a
            // repairable state for an unrepairable one, which is what d13 clause 2 forbids in its
            // second direction. The call is kept so an observable baseline is still cached.
            EnsureObservedCore();
            _theme = ThemeMode.Light;
            _preferences = ReadingPreferences.Default;
            _language = AppLanguage.System;
            _windowPlacement = null;
            PersistCore();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Observes the persisted baseline at most once and reports whether it was observed AT ALL.
    /// <c>true</c> means the fields below describe what is on disk (possibly legitimately empty);
    /// <c>false</c> means the bytes were never read and the user's real settings may be sitting
    /// there intact, so no caller may serialize the in-memory state over them.
    /// <para>
    /// The read IS the existence check. <see cref="File.Exists"/> must not be used as a preflight:
    /// it is documented to return false "if any error occurs while trying to determine if the
    /// specified file exists ... a failing or missing disk, or if the caller does not have
    /// permission to read the file", and to do so WITHOUT throwing. Measured on Windows 11 26100 /
    /// .NET 10.0.10: a denied ACL on the CONTAINING DIRECTORY makes File.Exists report false for a
    /// file that is present and populated (a deny on the file itself does not -- it still reports
    /// true). Preflighting with it turns a failure to observe into a silent, exception-free skip
    /// that never even reaches a catch. Absence is read off the exception the OS raises instead.
    /// </para>
    /// </summary>
    private bool EnsureObservedCore()
    {
        if (_hasObservedBaseline)
        {
            return true;
        }

        string json;
        try
        {
            json = File.ReadAllText(_settingsFilePath);
        }
        catch (FileNotFoundException)
        {
            // OBSERVED: the OS positively reports there is no such file. A legitimate first run --
            // defaults are the true baseline and the next ordinary save may create the file.
            ApplyObservedDefaults();
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            // OBSERVED: the OS positively reports there is no such directory -- a first launch
            // before anything has created %AppData%/MarkMello. Mapping this to "not observed"
            // would stop a freshly installed app from EVER persisting a setting, since only
            // PersistCore creates that directory. Both absence exceptions derive from IOException,
            // so they are caught by type AHEAD of the catch-all below; that ordering is the whole
            // reason "just delete the preflight and let the read throw" is wrong as stated.
            ApplyObservedDefaults();
            return true;
        }
        catch
        {
            // NOT OBSERVED: a denied ACL, a sharing lock, a failing disk, an unreachable network
            // path. Do not touch the fields, and above all do not latch -- the condition may be
            // transient, and the very next call gets a fresh chance to read the truth. Catch-all
            // by design, and scoped to the read call ALONE so nothing raised while interpreting
            // bytes below can be mistaken for an absence.
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                var fileModel = JsonSerializer.Deserialize(
                    json,
                    MarkMelloJsonSerializerContext.Default.SettingsFileModel);
                if (fileModel is not null)
                {
                    _theme = NormalizeTheme(fileModel.Theme);
                    _preferences = ReadingPreferences.Normalize(fileModel.Preferences);
                    _language = NormalizeLanguage(fileModel.Language);
                    _windowPlacement = WindowPlacement.Normalize(fileModel.WindowPlacement);
                    _hasObservedBaseline = true;
                    return true;
                }
            }

            // OBSERVED: the bytes were read and they carry no settings -- a blank file, or a
            // literal JSON `null`. An empty baseline is true for both.
            ApplyObservedDefaults();
            return true;
        }
        catch (JsonException)
        {
            // OBSERVED: the bytes were read, and they are not settings. Defaults are the correct
            // answer AND persisting over them is correct -- otherwise one corrupt file would
            // freeze settings permanently with no in-app way out.
            ApplyObservedDefaults();
            return true;
        }
        catch
        {
            // Read, but not positively identified as a non-settings file either. Fail closed for
            // the same reason as the read's catch-all: anything not positively identified as an
            // observed non-settings state is treated as possibly-intact.
            return false;
        }
    }

    /// <summary>
    /// Adopts the defaults as an OBSERVED baseline. Reached only from routes where the store
    /// looked and the true answer is "nothing usable is stored", so the state may be cached and
    /// may be written over.
    /// </summary>
    private void ApplyObservedDefaults()
    {
        _theme = ThemeMode.System;
        _preferences = ReadingPreferences.Default;
        _language = AppLanguage.System;
        _windowPlacement = null;
        _hasObservedBaseline = true;
    }

    private void PersistCore()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);

            var tempFilePath = _settingsFilePath + ".tmp";
            var fileModel = new SettingsFileModel(_theme, _preferences, _language, _windowPlacement);
            var json = JsonSerializer.Serialize(
                fileModel,
                MarkMelloJsonSerializerContext.Default.SettingsFileModel);

            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, _settingsFilePath, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort: reading must keep working even if the
            // config directory is unavailable or unwritable on this machine.
        }
    }

    private static ThemeMode NormalizeTheme(ThemeMode theme)
        => theme switch
        {
            ThemeMode.Light => ThemeMode.Light,
            ThemeMode.Dark => ThemeMode.Dark,
            ThemeMode.ClassicWhite => ThemeMode.Light,
            _ => ThemeMode.System
        };

    private static AppLanguage NormalizeLanguage(AppLanguage language)
        => language switch
        {
            AppLanguage.English => AppLanguage.English,
            AppLanguage.Russian => AppLanguage.Russian,
            _ => AppLanguage.System
        };

    private static string ResolveSettingsRootDirectory(string? settingsRootDirectory)
    {
        if (!string.IsNullOrWhiteSpace(settingsRootDirectory))
        {
            return Path.GetFullPath(settingsRootDirectory);
        }

        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appDataDirectory))
        {
            return Path.Combine(AppContext.BaseDirectory, "MarkMello");
        }

        return Path.Combine(appDataDirectory, "MarkMello");
    }
}

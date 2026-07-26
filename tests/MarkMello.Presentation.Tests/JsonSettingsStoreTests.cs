using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MarkMello.Domain;
using MarkMello.Infrastructure.Settings;

namespace MarkMello.Presentation.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoadRoundTripsSettings()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var store = new JsonSettingsStore(rootDirectory);
            var expectedPreferences = new ReadingPreferences(
                FontFamilyMode.Mono,
                19,
                1.8,
                ReadingPreferences.WideContentWidth,
                DocumentMinimapMode.On,
                MarkdownRendererBackend.WebView,
                WidthResizerVisibility.Always,
                LightPaletteMode.White,
                ModeSwitchSmoothEnabled: false,
                ModeSwitchSmoothDurationMs: 260,
                TocColumnWidth: 333);

            await store.SavePreferencesAsync(expectedPreferences);
            await store.SaveThemeAsync(ThemeMode.Dark);
            await store.SaveLanguageAsync(AppLanguage.Russian);
            await store.SaveWindowPlacementAsync(new WindowPlacement(120, 80, 900, 700, IsMaximized: true));

            var reloadedStore = new JsonSettingsStore(rootDirectory);
            var actualPreferences = await reloadedStore.LoadPreferencesAsync();
            var actualTheme = await reloadedStore.LoadThemeAsync();
            var actualLanguage = await reloadedStore.LoadLanguageAsync();
            var actualWindowPlacement = await reloadedStore.LoadWindowPlacementAsync();

            Assert.Equal(expectedPreferences, actualPreferences);
            Assert.Equal(ThemeMode.Dark, actualTheme);
            Assert.Equal(AppLanguage.Russian, actualLanguage);
            Assert.Equal(new WindowPlacement(120, 80, 900, 700, IsMaximized: true), actualWindowPlacement);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public async Task SaveAndLoadMigratesLegacyClassicWhiteThemeToLight()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var store = new JsonSettingsStore(rootDirectory);

            await store.SaveThemeAsync(ThemeMode.ClassicWhite);

            var reloadedStore = new JsonSettingsStore(rootDirectory);
            var theme = await reloadedStore.LoadThemeAsync();

            Assert.Equal(ThemeMode.Light, theme);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public async Task ResetRestoresAllSettingsDefaults()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var store = new JsonSettingsStore(rootDirectory);
            await store.SavePreferencesAsync(ReadingPreferences.Default with
            {
                FontFamily = FontFamilyMode.Mono,
                FontSize = 22,
                LineHeight = 2.4,
                ContentWidth = ReadingPreferences.WideContentWidth,
                DocumentMinimapMode = DocumentMinimapMode.On,
                LightPalette = LightPaletteMode.Original
            });
            await store.SaveThemeAsync(ThemeMode.Dark);
            await store.SaveLanguageAsync(AppLanguage.Russian);
            await store.SaveWindowPlacementAsync(new WindowPlacement(120, 80, 900, 700, IsMaximized: true));

            await store.ResetAsync();

            var reloadedStore = new JsonSettingsStore(rootDirectory);
            Assert.Equal(ReadingPreferences.Default, await reloadedStore.LoadPreferencesAsync());
            Assert.Equal(ThemeMode.Light, await reloadedStore.LoadThemeAsync());
            Assert.Equal(AppLanguage.System, await reloadedStore.LoadLanguageAsync());
            Assert.Null(await reloadedStore.LoadWindowPlacementAsync());
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public async Task LoadFallsBackToDefaultsWhenSettingsFileIsCorrupted()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootDirectory, "settings.json"), "{ invalid json");

            var store = new JsonSettingsStore(rootDirectory);

            var preferences = await store.LoadPreferencesAsync();
            var theme = await store.LoadThemeAsync();
            var language = await store.LoadLanguageAsync();
            var windowPlacement = await store.LoadWindowPlacementAsync();

            Assert.Equal(ReadingPreferences.Default, preferences);
            Assert.Equal(ThemeMode.System, theme);
            Assert.Equal(DocumentMinimapMode.Auto, preferences.DocumentMinimapMode);
            Assert.Equal(AppLanguage.System, language);
            Assert.Null(windowPlacement);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public async Task LoadNormalizesOutOfRangePreferenceValues()
    {
        var rootDirectory = CreateTempDirectory();
        const string json = """
        {
          "theme": "Light",
          "preferences": {
            "fontFamily": "Mono",
            "fontSize": 4,
            "lineHeight": 9.0,
            "contentWidth": 99999,
            "documentMinimapMode": "Off"
          }
        }
        """;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootDirectory, "settings.json"), json);

            var store = new JsonSettingsStore(rootDirectory);
            var preferences = await store.LoadPreferencesAsync();
            var theme = await store.LoadThemeAsync();
            var language = await store.LoadLanguageAsync();
            var windowPlacement = await store.LoadWindowPlacementAsync();

            Assert.Equal(ThemeMode.Light, theme);
            Assert.Equal(FontFamilyMode.Mono, preferences.FontFamily);
            Assert.Equal(ReadingPreferences.MinFontSize, preferences.FontSize);
            Assert.Equal(ReadingPreferences.MaxLineHeight, preferences.LineHeight);
            Assert.Equal(ReadingPreferences.MaxContentWidth, preferences.ContentWidth);
            Assert.Equal(DocumentMinimapMode.Off, preferences.DocumentMinimapMode);
            Assert.Equal(AppLanguage.System, language);
            Assert.Null(windowPlacement);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }


    [Fact]
    public async Task LoadUsesAutoMinimapModeWhenLegacySettingsHaveNoMinimapMode()
    {
        var rootDirectory = CreateTempDirectory();
        const string json = """
        {
          "theme": "Light",
          "preferences": {
            "fontFamily": "Serif",
            "fontSize": 18,
            "lineHeight": 1.7,
            "contentWidth": 820
          }
        }
        """;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootDirectory, "settings.json"), json);

            var store = new JsonSettingsStore(rootDirectory);
            var preferences = await store.LoadPreferencesAsync();

            Assert.Equal(DocumentMinimapMode.Auto, preferences.DocumentMinimapMode);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public async Task LoadUsesWebViewRendererWhenLegacySettingsHaveNoRendererBackend()
    {
        var rootDirectory = CreateTempDirectory();
        const string json = """
        {
          "theme": "Light",
          "preferences": {
            "fontFamily": "Serif",
            "fontSize": 18,
            "lineHeight": 1.7,
            "contentWidth": 820,
            "documentMinimapMode": "Auto"
          }
        }
        """;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootDirectory, "settings.json"), json);

            var store = new JsonSettingsStore(rootDirectory);
            var preferences = await store.LoadPreferencesAsync();

            Assert.Equal(MarkdownRendererBackend.WebView, preferences.RendererBackend);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public async Task LoadUsesOnHoverResizerWhenLegacySettingsHaveNoWidthResizerVisibility()
    {
        var rootDirectory = CreateTempDirectory();
        const string json = """
        {
          "theme": "Light",
          "preferences": {
            "fontFamily": "Serif",
            "fontSize": 18,
            "lineHeight": 1.7,
            "contentWidth": 820,
            "documentMinimapMode": "Auto",
            "rendererBackend": "WebView"
          }
        }
        """;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootDirectory, "settings.json"), json);

            var store = new JsonSettingsStore(rootDirectory);
            var preferences = await store.LoadPreferencesAsync();

            Assert.Equal(WidthResizerVisibility.OnHover, preferences.WidthResizerVisibility);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public async Task LoadUsesDefaultModeSwitchSmoothSettingsWhenLegacySettingsHaveNoSmoothFields()
    {
        var rootDirectory = CreateTempDirectory();
        const string json = """
        {
          "theme": "Light",
          "preferences": {
            "fontFamily": "Serif",
            "fontSize": 18,
            "lineHeight": 1.7,
            "contentWidth": 820,
            "documentMinimapMode": "Auto",
            "rendererBackend": "WebView",
            "widthResizerVisibility": "OnHover",
            "lightPalette": "White"
          }
        }
        """;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootDirectory, "settings.json"), json);

            var store = new JsonSettingsStore(rootDirectory);
            var preferences = await store.LoadPreferencesAsync();

            Assert.True(preferences.ModeSwitchSmoothEnabled);
            Assert.Equal(ReadingPreferences.DefaultModeSwitchSmoothDurationMs, preferences.ModeSwitchSmoothDurationMs);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    [Fact]
    public async Task LoadFallsBackToNullWindowPlacementWhenPlacementIsInvalid()
    {
        var rootDirectory = CreateTempDirectory();
        const string json = """
        {
          "theme": "Light",
          "preferences": {
            "fontFamily": "Serif",
            "fontSize": 18,
            "lineHeight": 1.7,
            "contentWidth": 720
          },
          "language": "English",
          "windowPlacement": {
            "x": 100,
            "y": 100,
            "width": 0,
            "height": 640,
            "isMaximized": false
          }
        }
        """;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootDirectory, "settings.json"), json);

            var store = new JsonSettingsStore(rootDirectory);
            var windowPlacement = await store.LoadWindowPlacementAsync();

            Assert.Null(windowPlacement);
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    // ===============================================================================================
    // d13 clause 2 -- "observed and empty" and "could not observe" are different facts.
    //
    // Every single-field setter on this store serializes the WHOLE file, so a save always carries
    // three fields straight out of the in-memory baseline. That makes an unobserved baseline
    // actively destructive rather than merely unhelpful: one unreadable read replaces the user's
    // real theme, reading preferences, language AND window placement with fabricated defaults, and
    // the old code latched that forgery for the entire process in a `finally`.
    //
    // The guards below come in two halves that MUST both hold. G1-G3 close the forgery; G4-G7 keep
    // the two regressions d13 clause 2 forbids in the OTHER direction closed -- a genuinely absent
    // file must still yield usable defaults (or a fresh install has no settings at all) and an
    // unparseable file must still yield defaults AND still persist (or one corrupt file freezes
    // settings permanently with no in-app way out).
    // ===============================================================================================

    /// <summary>
    /// G1 -- the latch. An unobservable read must not be cached as an observation.
    /// <para>
    /// This is the half with no counterpart in the sibling session-store fix: that store re-read the
    /// file on every call, so each call had a fresh chance to observe correctly. This one caches, so
    /// setting the cache flag in a <c>finally</c> turned one transient failure at startup into a
    /// permanent one, poisoning every later read and write in the process.
    /// </para>
    /// <para>
    /// The denial must sit on the CONTAINING DIRECTORY. That is the shape that makes
    /// <see cref="File.Exists"/> report false for a file that is present and populated, which is why
    /// the old preflight skipped the load without ever raising and without ever reaching a catch. A
    /// denial on the file itself does NOT reproduce it -- File.Exists still returns true there
    /// (measured on Windows 11 26100 / .NET 10.0.10 this session, not recalled). The File.Exists
    /// assertion below is deliberate: if the ACL trick ever stops reproducing, this fails loudly
    /// instead of passing vacuously.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnUnobservableReadIsNotLatchedAsAnObservationForTheProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootDirectory = CreateTempDirectory();
        try
        {
            await WriteRealSettingsAsync(rootDirectory);
            var store = new JsonSettingsStore(rootDirectory);

            DenyAllAccess(rootDirectory);
            try
            {
                // The API lies: the file is there and populated, and File.Exists reports otherwise.
                Assert.False(File.Exists(Path.Combine(rootDirectory, SettingsFileName)));

                // Reading must still answer with something usable rather than throwing...
                Assert.Equal(ThemeMode.System, await store.LoadThemeAsync());
            }
            finally
            {
                RestoreAllAccess(rootDirectory);
            }

            // ...but it must not have decided that answer is the truth. The same instance, asked
            // again once the condition has passed, must report what is actually on disk.
            Assert.Equal(ThemeMode.Dark, await store.LoadThemeAsync());
            Assert.Equal(AppLanguage.Russian, await store.LoadLanguageAsync());
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// G2 -- the completed destruction chain, with a PERSISTENT read failure and no timing window.
    /// <para>
    /// The denial here sits on the FILE and grants only ReadData, so the containing directory stays
    /// writable. Measured this session: <see cref="File.Exists"/> still returns true, the read fails
    /// with <see cref="UnauthorizedAccessException"/>, and <c>File.Move(tmp, target, overwrite:
    /// true)</c> SUCCEEDS and replaces the file. So this is a route on which the store cannot read
    /// but can very much write -- exactly the state in which a store that treats an unreadable file
    /// as an empty one destroys the user's settings.
    /// </para>
    /// <para>
    /// It is also a route the old preflight had nothing to do with (File.Exists is honest here),
    /// which is what makes it a separate fact from G1 rather than a restatement of it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnUnobservableBaselineIsNeverSerializedOverByALaterSave()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootDirectory = CreateTempDirectory();
        var settingsFilePath = Path.Combine(rootDirectory, SettingsFileName);
        try
        {
            await WriteRealSettingsAsync(rootDirectory);
            var store = new JsonSettingsStore(rootDirectory);

            DenyFileRead(settingsFilePath);
            try
            {
                // Unlike G1, the existence check is honest here -- only the read is denied.
                Assert.True(File.Exists(settingsFilePath));

                await store.LoadThemeAsync();
                await store.SaveWindowPlacementAsync(new WindowPlacement(10, 20, 800, 600, IsMaximized: false));
            }
            finally
            {
                RestoreFileAccess(settingsFilePath);
            }

            var reloaded = new JsonSettingsStore(rootDirectory);
            Assert.Equal(ThemeMode.Dark, await reloaded.LoadThemeAsync());
            Assert.Equal(AppLanguage.Russian, await reloaded.LoadLanguageAsync());
            Assert.Equal(DistinctivePreferences(), await reloaded.LoadPreferencesAsync());
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// G3 -- refusing must cost nothing once the condition passes. The transient chain: the store
    /// could not read at startup, the access failure clears, and the next save must land on the
    /// TRUE baseline -- keeping the three fields it did not touch and writing the one it did.
    /// <para>
    /// This is the guard that would fail if "do not latch" were implemented as "never cache", or if
    /// the refusal were implemented as a second, sticky flag: both would leave the store stuck.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SavesResumeOnTheTrueBaselineOnceAnEarlierFailedReadBecomesPossible()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootDirectory = CreateTempDirectory();
        try
        {
            await WriteRealSettingsAsync(rootDirectory);
            var store = new JsonSettingsStore(rootDirectory);

            DenyAllAccess(rootDirectory);
            try
            {
                await store.LoadThemeAsync();
            }
            finally
            {
                RestoreAllAccess(rootDirectory);
            }

            var placement = new WindowPlacement(10, 20, 800, 600, IsMaximized: false);
            await store.SaveWindowPlacementAsync(placement);

            var reloaded = new JsonSettingsStore(rootDirectory);
            Assert.Equal(placement, await reloaded.LoadWindowPlacementAsync());
            Assert.Equal(ThemeMode.Dark, await reloaded.LoadThemeAsync());
            Assert.Equal(AppLanguage.Russian, await reloaded.LoadLanguageAsync());
            Assert.Equal(DistinctivePreferences(), await reloaded.LoadPreferencesAsync());
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// G4 -- forbidden regression, first direction: a genuinely absent file is an OBSERVATION, and
    /// defaults are the true baseline for it. Asserting the save LANDS is the load-bearing half:
    /// a fix that mapped every failed read to "not observed" would leave a first run unable to
    /// persist anything at all, and a defaults-only assertion would not notice.
    /// </summary>
    [Fact]
    public async Task AnAbsentFileYieldsDefaultsAndStillPersists()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var store = new JsonSettingsStore(rootDirectory);

            Assert.Equal(ThemeMode.System, await store.LoadThemeAsync());

            await store.SaveThemeAsync(ThemeMode.Dark);

            Assert.Equal(ThemeMode.Dark, await new JsonSettingsStore(rootDirectory).LoadThemeAsync());
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// G5 -- the same regression reached by the FIRST LAUNCH AFTER INSTALL, which is a distinct
    /// route: %AppData%/MarkMello does not exist yet, so the OS reports the path missing
    /// (DirectoryNotFoundException) rather than the file (FileNotFoundException), and only a save
    /// ever creates that directory. Both derive from IOException, so a fix that lets the read throw
    /// into a single catch-all lands here and freezes a fresh install permanently.
    /// </summary>
    [Fact]
    public async Task AnAbsentDirectoryYieldsDefaultsAndStillPersists()
    {
        var caseRoot = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", Guid.NewGuid().ToString("N"));
        var rootDirectory = Path.Combine(caseRoot, "never-created");
        try
        {
            Assert.False(Directory.Exists(rootDirectory));

            var store = new JsonSettingsStore(rootDirectory);

            Assert.Equal(ThemeMode.System, await store.LoadThemeAsync());

            await store.SaveThemeAsync(ThemeMode.Dark);

            Assert.Equal(ThemeMode.Dark, await new JsonSettingsStore(rootDirectory).LoadThemeAsync());
        }
        finally
        {
            DeleteDirectory(caseRoot);
        }
    }

    /// <summary>
    /// G6 -- forbidden regression, second direction: an unparseable file was READ, so defaults are
    /// the true answer and writing over it is correct. Without the persisting half, one corrupt
    /// settings file would freeze settings permanently with no in-app recovery -- trading a
    /// repairable defect for an unrepairable one.
    /// </summary>
    [Fact]
    public async Task AnUnparseableFileYieldsDefaultsAndStillPersists()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootDirectory, SettingsFileName), "{not valid json");

            var store = new JsonSettingsStore(rootDirectory);

            Assert.Equal(ThemeMode.System, await store.LoadThemeAsync());

            await store.SaveThemeAsync(ThemeMode.Dark);

            Assert.Equal(ThemeMode.Dark, await new JsonSettingsStore(rootDirectory).LoadThemeAsync());
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// G7 -- the remaining observed-but-empty routes. A blank file and a literal JSON <c>null</c>
    /// are both states the store LOOKED at; an empty baseline is true for each, and each must stay
    /// writable. Kept as one fact because neither is individually mutation-proven; the two routes
    /// that a wrong fix actually breaks (absent and unparseable) are separate facts above, so a
    /// short-circuiting first failure here cannot hide one of those.
    /// </summary>
    [Fact]
    public async Task ObservedButEmptyFileContentsYieldDefaultsAndStillPersist()
    {
        foreach (var (caseName, contents) in new[]
        {
            ("blank", "   \r\n  "),
            ("json-null-literal", "null"),
        })
        {
            var rootDirectory = CreateTempDirectory();
            try
            {
                await File.WriteAllTextAsync(Path.Combine(rootDirectory, SettingsFileName), contents);

                var store = new JsonSettingsStore(rootDirectory);

                Assert.Equal(ThemeMode.System, await store.LoadThemeAsync());
                Assert.Equal(ReadingPreferences.Default, await store.LoadPreferencesAsync());

                await store.SaveThemeAsync(ThemeMode.Dark);

                var persistedTheme = await new JsonSettingsStore(rootDirectory).LoadThemeAsync();
                Assert.True(
                    persistedTheme == ThemeMode.Dark,
                    $"{caseName}: expected Dark to reach disk, found {persistedTheme}");
            }
            finally
            {
                DeleteDirectory(rootDirectory);
            }
        }
    }

    /// <summary>
    /// G8 -- CHARACTERIZATION, not a defect guard: it is green before and after this fix. It pins
    /// the one deliberate exemption from the refusal rule, so that a later "consistency" cleanup
    /// that gates Reset on observation too has to break a named test rather than silently remove
    /// the only in-app way back from an unreadable settings file.
    /// <para>
    /// Reset is exempt because it reads NOTHING from the baseline -- every value it writes is a
    /// constant, so it cannot fabricate a field out of one it never observed, and the file it
    /// produces is identical whether or not the old contents were legible. The route below is the
    /// one where that matters: the file cannot be read but can still be replaced.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResetStillRepairsASettingsFileThisProcessCannotRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootDirectory = CreateTempDirectory();
        var settingsFilePath = Path.Combine(rootDirectory, SettingsFileName);
        try
        {
            await WriteRealSettingsAsync(rootDirectory);
            var store = new JsonSettingsStore(rootDirectory);

            DenyFileRead(settingsFilePath);
            try
            {
                await store.ResetAsync();
            }
            finally
            {
                RestoreFileAccess(settingsFilePath);
            }

            var reloaded = new JsonSettingsStore(rootDirectory);
            Assert.Equal(ThemeMode.Light, await reloaded.LoadThemeAsync());
            Assert.Equal(AppLanguage.System, await reloaded.LoadLanguageAsync());
            Assert.Equal(ReadingPreferences.Default, await reloaded.LoadPreferencesAsync());
            Assert.Null(await reloaded.LoadWindowPlacementAsync());
        }
        finally
        {
            DeleteDirectory(rootDirectory);
        }
    }

    private const string SettingsFileName = "settings.json";

    /// <summary>
    /// A settings state in which every one of the four persisted values is distinguishable from its
    /// default, so a forged write shows up whichever field it forges.
    /// </summary>
    private static ReadingPreferences DistinctivePreferences() => new(
        FontFamilyMode.Mono,
        19,
        1.8,
        ReadingPreferences.WideContentWidth,
        DocumentMinimapMode.On,
        MarkdownRendererBackend.WebView,
        WidthResizerVisibility.Always,
        LightPaletteMode.White,
        ModeSwitchSmoothEnabled: false,
        ModeSwitchSmoothDurationMs: 260,
        TocColumnWidth: 333);

    private static async Task WriteRealSettingsAsync(string rootDirectory)
    {
        var seed = new JsonSettingsStore(rootDirectory);
        await seed.SavePreferencesAsync(DistinctivePreferences());
        await seed.SaveThemeAsync(ThemeMode.Dark);
        await seed.SaveLanguageAsync(AppLanguage.Russian);
        await seed.SaveWindowPlacementAsync(new WindowPlacement(120, 80, 900, 700, IsMaximized: true));
    }

    /// <summary>
    /// Denies the current user every right on <paramref name="directory"/>, with inheritance broken
    /// so no inherited Allow survives. Deny FullControl is required: a partial deny (list + read
    /// attributes + traverse) leaves File.Exists returning true and would not reproduce the defect.
    /// The owner can always rewrite its own DACL, which is what lets the restore run unelevated.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void DenyAllAccess(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("no user SID on the current identity");
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreAllAccess(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        RemoveDenyRules(security);
        info.SetAccessControl(security);
    }

    /// <summary>
    /// Denies ReadData on the file ALONE, leaving the containing directory writable. Measured this
    /// session: File.Exists stays true, File.ReadAllText throws UnauthorizedAccessException, and a
    /// File.Move over the file with overwrite still succeeds.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void DenyFileRead(string file)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("no user SID on the current identity");
        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    /// <summary>
    /// Tolerates the file having been REPLACED while denied -- which is precisely what happens when
    /// the store under test forges a write, since File.Move drops the old file and its explicit ACE
    /// along with it. Finding no deny rule is therefore a legitimate outcome here, not a failure.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RestoreFileAccess(string file)
    {
        if (!File.Exists(file))
        {
            return;
        }

        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        RemoveDenyRules(security);
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveDenyRules(FileSystemSecurity security)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny)
            {
                security.RemoveAccessRule(rule);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkMello.Presentation.Tests;

/// <summary>
/// Guard: every brush resource key looked up anywhere in the shipped UI code must be
/// DEFINED in all three theme dictionaries (Light, ClassicWhite, Dark) in Colors.axaml.
///
/// <para>This is the build-time guard for the commit-466fb39 class of defect: a lookup
/// naming a resource key that exists in NO theme. That class is silent by construction —
/// <c>TryFindResource</c>, <c>GetResourceObservable</c> and <c>{DynamicResource}</c> all
/// miss quietly (null / no emission / keep-default) and a <c>?? fallback</c> chain moves
/// on, so nothing ever renders wrong to draw attention.</para>
///
/// <para>It also catches the reverse (the classic-white purple-cell class): a key defined
/// in some themes but not another. Because <c>ClassicWhiteThemeVariant</c> is declared with
/// a <c>ThemeVariant.Light</c> inherit fallback, the Avalonia runtime would silently resolve
/// a ClassicWhite-missing key through the Light dictionary — masking the gap. So this guard
/// asserts EXPLICIT per-dictionary presence, not inherited runtime resolution, which is
/// strictly stricter and matches the file's own convention (all three dictionaries define
/// the identical key set).</para>
///
/// <para>Enumeration is a source-text scan — the repo's established convention where no
/// headless harness exists (see <c>ApplicateSiblingMountTests</c>, which asserts against
/// file text the same way). Two angles converge and make the scan mechanism-agnostic: every
/// in-repo brush lookup passes its key either as a <c>"Mm...Brush"</c> string literal (C#)
/// or as <c>{DynamicResource Mm...Brush}</c> (XAML), whatever helper wraps it — verified
/// helpers at HEAD are <c>TryFindResource</c>, <c>LookupBrush</c>, <c>ResolveOptionalBrush</c>,
/// <c>ResolveBrush</c> and Avalonia's <c>GetResourceObservable</c>. A key-pattern scan is a
/// superset of any helper-name scan, so an aliased or future helper cannot smuggle an
/// unresolved key past this guard.</para>
///
/// <para>Scope: brush keys only (suffix "Brush"). Font-family lookups (<c>Mm*FontFamily</c>,
/// defined in Typography.axaml, not theme-scoped) are a different dictionary and out of
/// scope. The reverse "token DEFINED with no consumer" question is deliberately out of scope
/// per the queue spec (2026-07-20-theme-token-resolution-audit).</para>
/// </summary>
public sealed class ThemeTokenResolutionTests
{
    // AppContext.BaseDirectory = <repo>/tests/MarkMello.Presentation.Tests/bin/<cfg>/net10.0/.
    // Five levels up reaches the repo root — the same anchoring ApplicateSiblingMountTests uses.
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string ColorsAxamlPath = Path.Combine(
        RepoRoot, "src", "MarkMello.Presentation", "Themes", "Colors.axaml");

    // Both trees that consume the palette: the Presentation views that own it and the
    // Applicate fork chrome that reuses it. The scan is file I/O, not a compile dependency.
    private static readonly string[] SourceRoots =
    {
        Path.Combine(RepoRoot, "src", "MarkMello.Presentation"),
        Path.Combine(RepoRoot, "src", "MarkMello.Applicate.Desktop"),
    };

    private static readonly string[] ExpectedThemes = { "Light", "ClassicWhite", "Dark" };

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // A brush resource key in this repo is PascalCase, "Mm"-prefixed, "Brush"-suffixed.
    private static readonly Regex CSharpBrushLiteral =
        new("\"(Mm[A-Za-z0-9]+Brush)\"", RegexOptions.Compiled);

    private static readonly Regex XamlBrushResource =
        new(@"\{(?:Dynamic|Static)Resource\s+(Mm[A-Za-z0-9]+Brush)\}", RegexOptions.Compiled);

    [Fact]
    public void EveryBrushLookupResolvesInAllThreeThemeVariants()
    {
        var dictionaries = LoadThemeBrushKeys();

        // Resolver self-check: the three real dictionaries loaded, each non-empty. Guards
        // against a parser regression that would let the guard pass by resolving nothing.
        Assert.Equal(
            ExpectedThemes.OrderBy(t => t, StringComparer.Ordinal),
            dictionaries.Keys.OrderBy(t => t, StringComparer.Ordinal));
        foreach (var (theme, keys) in dictionaries)
        {
            Assert.True(keys.Count > 0, $"Theme '{theme}' resolved zero brush keys — Colors.axaml parse broke.");
        }

        var lookups = EnumerateBrushLookups();

        // Scanner self-check: real source was read (an empty scan must never pass silently)
        // and a known key from a known mechanism was captured.
        Assert.NotEmpty(lookups);
        Assert.Contains(lookups, l => l.Key == "MmBackgroundBrush");

        var failures = new List<string>();
        foreach (var lookup in lookups.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            foreach (var theme in ExpectedThemes)
            {
                if (!dictionaries[theme].Contains(lookup.Key))
                {
                    failures.Add($"{lookup.Key} — not defined in the '{theme}' theme dictionary " +
                        $"(looked up at {lookup.Location})");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Brush lookups that do not resolve in all three theme variants (Colors.axaml):\n  " +
            string.Join("\n  ", failures));
    }

    /// <summary>
    /// Parses Colors.axaml — the single palette source — into { themeName -> set of brush keys
    /// explicitly defined in that theme's dictionary }. A faithful load of the real dictionaries,
    /// not a hardcoded allow-list.
    /// </summary>
    private static Dictionary<string, IReadOnlySet<string>> LoadThemeBrushKeys()
    {
        Assert.True(File.Exists(ColorsAxamlPath), $"Colors.axaml not found at {ColorsAxamlPath}");

        var doc = XDocument.Load(ColorsAxamlPath);

        // The three theme dictionaries are the ResourceDictionary elements that carry an
        // x:Key inside ResourceDictionary.ThemeDictionaries. Match on x:Key text so a
        // reorder in the file cannot mislabel them.
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        var themeDictionaries = doc.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary"
                        && e.Attribute(Xaml + "Key") is not null);

        foreach (var themeDict in themeDictionaries)
        {
            var rawKey = themeDict.Attribute(Xaml + "Key")!.Value;
            var theme = ClassifyTheme(rawKey);

            var brushKeys = themeDict.Descendants()
                .Select(e => e.Attribute(Xaml + "Key")?.Value)
                .Where(k => k is not null && k.EndsWith("Brush", StringComparison.Ordinal))
                .Select(k => k!)
                .ToHashSet(StringComparer.Ordinal);

            // Last-writer-wins is impossible here (each theme key is unique); a duplicate
            // label would indicate a malformed file, so fail loudly rather than merge.
            Assert.False(result.ContainsKey(theme),
                $"Duplicate theme dictionary '{theme}' (raw key '{rawKey}') in Colors.axaml.");
            result[theme] = brushKeys;
        }

        return result;
    }

    /// <summary>
    /// Maps a theme dictionary's raw x:Key to a canonical theme name. ClassicWhite is keyed
    /// via the <c>{x:Static AvaloniaThemeService.ClassicWhiteThemeVariant}</c> markup extension;
    /// Light and Dark are plain variant names. An unrecognized key is returned verbatim so the
    /// caller's expected-set assertion fails closed on a new/renamed theme.
    /// </summary>
    private static string ClassifyTheme(string rawKey)
    {
        if (rawKey.Contains("ClassicWhite", StringComparison.Ordinal))
        {
            return "ClassicWhite";
        }

        return rawKey.Trim();
    }

    /// <summary>
    /// Scans both source trees for brush-lookup keys across every lookup mechanism, returning
    /// each DISTINCT key once with its first-seen location for a readable failure message.
    /// </summary>
    private static List<(string Key, string Location)> EnumerateBrushLookups()
    {
        var firstSeen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var root in SourceRoots)
        {
            Assert.True(Directory.Exists(root), $"Source root not found: {root}");

            foreach (var file in EnumerateSourceFiles(root))
            {
                var regex = file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                    ? XamlBrushResource
                    : CSharpBrushLiteral;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (Match match in regex.Matches(lines[i]))
                    {
                        var key = match.Groups[1].Value;
                        if (!firstSeen.ContainsKey(key))
                        {
                            var relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
                            firstSeen[key] = $"{relative}:{i + 1}";
                        }
                    }
                }
            }
        }

        return firstSeen.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip build output — generated code under obj/ and bin/ is not a source lookup.
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }
}

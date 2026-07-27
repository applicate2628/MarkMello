using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkMello.Applicate.Tests;

// Turns an EMERGENT property into an ENFORCED one.
//
// EarlyDocumentCache's doc comment states that it is the only type in
// MarkMello.Application that reaches the disk, and its placement argument
// rests on that being true. It IS true today -- and nothing kept it true.
// There are no architecture or fitness tests in this repository, no analyzer
// package in any csproj, no BannedSymbols.txt, no dotnet_diagnostic rules in
// .editorconfig, no layering step in CI, and every git hook is still a
// .sample. The only structural enforcement is the csproj reference graph, and
// that graph is blind to System.IO: MarkMello.Application already has
// `global using System.IO;` via ImplicitUsings, so File.ReadAllText compiles
// there today with no using directive and no reference change to notice in
// review. This scan is the missing enforcement, mirroring the statement-aware
// source-text idiom of DispatchAwaitDisciplineTests.
//
// THE LINE IS "NO DISK ACCESS", NOT "NO System.IO". Path-string manipulation
// is already de-facto allowed in this layer and is NOT a violation:
// Path.GetFullPath ships in SaveDocumentUseCase (twice) and in
// EarlyDocumentCache itself, plus Path.GetFileName and Path.GetExtension --
// eight call sites, all pre-existing, none of which touch a disk. A scan that
// banned `Path.` outright would go red on untouched code and would be simply
// wrong about where the convention actually sits. The one exception is
// Path.GetTempFileName, which despite living on Path DOES create a file on
// disk, so it is banned by name.
//
// WHAT THIS CATCHES:
//  - bare `File.` / `Directory.` (the static filesystem classes)
//  - the instantiable filesystem types by bare name (FileInfo, DirectoryInfo,
//    FileSystemInfo, FileStream, StreamReader, StreamWriter,
//    FileSystemWatcher, DriveInfo, RandomAccess)
//  - ANY qualified `System.IO.<something>` reference. This one pattern covers
//    the evasions a bare-name scan would otherwise miss: a fully-qualified
//    `System.IO.File.ReadAllText(p)`, a `using static System.IO.File;`, an
//    alias `using IOFile = System.IO.File;`, and a sub-namespace import such
//    as `using System.IO.Compression;` or `using System.IO.IsolatedStorage;`.
//    (Plain `using System.IO;` has no trailing dot and is NOT flagged -- it is
//    already implicit repo-wide and is not itself disk access.)
//  - Path.GetTempFileName
//
// WHAT THIS KNOWINGLY DOES NOT CATCH:
//  - disk access that never names a System.IO symbol: a P/Invoke to
//    CreateFileW, a third-party library that reads files internally, or a
//    future System.IO type not on the bare-name list reached through the
//    implicit `using System.IO;`. The qualified-reference rule catches the
//    import-based routes; a raw syscall is out of a text scanner's reach.
//  - a local or member coincidentally named `File` or `Directory` would be a
//    FALSE POSITIVE. None exists today, and this direction fails loudly
//    rather than silently, which is the correct direction for a guard.
//  - a banned name inside an INTERPOLATED string's literal text. Interpolated
//    strings are deliberately left intact by the blanking pass below, because
//    their `{...}` holes contain real code that must stay scannable --
//    accepting a loud false positive there rather than a silent false
//    negative.
//
// Comments and plain string literals ARE blanked before scanning. That is not
// optional here: this codebase discusses these type names in prose (
// EarlyDocumentCache's own FileIdentity.TryCapture carries the comment "One
// FileInfo is one metadata snapshot ..."), so a raw text scan would flag
// documentation as a layering violation. Blanking preserves newlines, so
// reported line numbers still point at the real source line.
public static class ApplicationLayerDiskAccessScanner
{
    // The single deliberate exception, kept as a one-file allowlist. Stored
    // with a forward slash; the enumerator normalizes separators before
    // comparing so this is stable regardless of platform.
    public const string AllowlistedRelativePath = "Diagnostics/EarlyDocumentCache.cs";

    private sealed record BannedPattern(Regex Pattern, string Description);

    private static readonly BannedPattern[] BannedPatterns =
    [
        new BannedPattern(
            new Regex(@"(?<![\w.])(?:File|Directory)\s*\.", RegexOptions.Compiled),
            "the System.IO static filesystem classes (File. / Directory.)"),
        new BannedPattern(
            new Regex(
                @"(?<![\w.])(?:FileInfo|DirectoryInfo|FileSystemInfo|FileStream|StreamReader|StreamWriter|FileSystemWatcher|DriveInfo|RandomAccess)\b",
                RegexOptions.Compiled),
            "an instantiable System.IO filesystem type"),
        new BannedPattern(
            new Regex(@"System\s*\.\s*IO\s*\.", RegexOptions.Compiled),
            "a qualified System.IO reference (fully-qualified call, using static, alias, or sub-namespace import)"),
        new BannedPattern(
            new Regex(@"(?<![\w.])Path\s*\.\s*GetTempFileName\b", RegexOptions.Compiled),
            "Path.GetTempFileName, the one Path member that creates a file on disk"),
    ];

    public static IReadOnlyList<string> FindDiskAccessSites(string source, string fileLabel)
    {
        var scannable = BlankCommentsAndPlainLiterals(source);
        var violations = new List<string>();

        foreach (var banned in BannedPatterns)
        {
            foreach (Match match in banned.Pattern.Matches(scannable))
            {
                var line = scannable[..match.Index].Count(c => c == '\n') + 1;
                violations.Add(
                    $"{fileLabel}:{line}: '{match.Value.Trim()}' is {banned.Description}. " +
                    "MarkMello.Application must not reach the disk outside " +
                    $"'{AllowlistedRelativePath}' -- see the Layering section of that file's doc comment, " +
                    "whose placement argument depends on this staying a single-file exception. " +
                    "Path-string manipulation (Path.GetFullPath / GetFileName / GetExtension) is allowed; " +
                    "actual disk access belongs in MarkMello.Infrastructure behind an Application-owned " +
                    "abstraction.");
            }
        }

        return violations;
    }

    // Replaces comments, plain string literals and char literals with spaces
    // (newlines preserved so line numbers survive). Interpolated strings are
    // left intact -- see the header note on that tradeoff.
    public static string BlankCommentsAndPlainLiterals(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var buffer = new StringBuilder(source);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                var end = source.IndexOf('\n', i);
                end = end < 0 ? source.Length : end;
                Blank(buffer, i, end);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = close < 0 ? source.Length : close + 2;
                Blank(buffer, i, end);
                i = end;
                continue;
            }

            if (c == '\'')
            {
                var end = FindCharLiteralEnd(source, i);
                Blank(buffer, i, end);
                i = end;
                continue;
            }

            if (c is '"' or '@' or '$')
            {
                var prefixStart = i;
                var interpolated = false;
                var verbatim = false;
                var j = i;
                while (j < source.Length && source[j] is '@' or '$')
                {
                    interpolated |= source[j] == '$';
                    verbatim |= source[j] == '@';
                    j++;
                }

                if (j < source.Length && source[j] == '"')
                {
                    var end = FindStringLiteralEnd(source, j, verbatim);
                    if (!interpolated)
                    {
                        Blank(buffer, prefixStart, end);
                    }

                    i = end;
                    continue;
                }

                // A lone '@' (verbatim identifier such as @class) or '$'.
                i++;
                continue;
            }

            i++;
        }

        return buffer.ToString();
    }

    private static int FindStringLiteralEnd(string source, int quoteIndex, bool verbatim)
    {
        // Raw string literal: an opening run of three or more quotes closes on
        // a run of at least the same length.
        var openRun = 0;
        while (quoteIndex + openRun < source.Length && source[quoteIndex + openRun] == '"')
        {
            openRun++;
        }

        if (openRun >= 3)
        {
            var k = quoteIndex + openRun;
            while (k < source.Length)
            {
                if (source[k] != '"')
                {
                    k++;
                    continue;
                }

                var closeRun = 0;
                while (k + closeRun < source.Length && source[k + closeRun] == '"')
                {
                    closeRun++;
                }

                if (closeRun >= openRun)
                {
                    return k + closeRun;
                }

                k += closeRun;
            }

            return source.Length;
        }

        if (verbatim)
        {
            // @"..." -- a doubled "" is an escaped quote, anything else closes.
            var k = quoteIndex + 1;
            while (k < source.Length)
            {
                if (source[k] == '"')
                {
                    if (k + 1 < source.Length && source[k + 1] == '"')
                    {
                        k += 2;
                        continue;
                    }

                    return k + 1;
                }

                k++;
            }

            return source.Length;
        }

        // Regular "..." -- backslash escapes, and a newline means the literal
        // was never closed (malformed source); stop there rather than run on.
        for (var k = quoteIndex + 1; k < source.Length; k++)
        {
            if (source[k] == '\\')
            {
                k++;
                continue;
            }

            if (source[k] == '"')
            {
                return k + 1;
            }

            if (source[k] == '\n')
            {
                return k;
            }
        }

        return source.Length;
    }

    private static int FindCharLiteralEnd(string source, int quoteIndex)
    {
        for (var k = quoteIndex + 1; k < source.Length; k++)
        {
            if (source[k] == '\\')
            {
                k++;
                continue;
            }

            if (source[k] == '\'')
            {
                return k + 1;
            }

            if (source[k] == '\n')
            {
                return k;
            }
        }

        return source.Length;
    }

    private static void Blank(StringBuilder buffer, int start, int end)
    {
        for (var k = start; k < end && k < buffer.Length; k++)
        {
            if (buffer[k] is not ('\n' or '\r'))
            {
                buffer[k] = ' ';
            }
        }
    }
}

public sealed class ApplicationLayerDiskAccessDisciplineTests
{
    // ---- the real guard ---------------------------------------------------

    [Fact]
    public void OnlyEarlyDocumentCacheReachesTheDiskInTheApplicationLayer()
    {
        var violations = new List<string>();

        foreach (var (source, relativePath) in EnumerateApplicationSources())
        {
            if (string.Equals(
                    relativePath,
                    ApplicationLayerDiskAccessScanner.AllowlistedRelativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            violations.AddRange(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(source, relativePath));
        }

        Assert.True(
            violations.Count == 0,
            "A file in MarkMello.Application crossed the layer's disk-access line. That line is not a style "
            + "preference: EarlyDocumentCache's Layering doc comment argues its own placement from being the "
            + "SINGLE type in this layer that reaches the disk, and this is the only thing enforcing it (the "
            + "csproj graph cannot see System.IO, and `global using System.IO;` is implicit here). Either move "
            + "the disk access to MarkMello.Infrastructure behind an Application-owned abstraction, or -- if a "
            + "second exception is genuinely warranted -- widen the allowlist AND update that doc comment, "
            + "because its argument stops holding the moment this list has two entries.\n\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void TheAllowlistedFileStillExists()
    {
        // A renamed or moved EarlyDocumentCache.cs would leave the allowlist
        // pointing at nothing. The scan above would still fail (the moved file
        // trips it), but with a confusing message; this asserts the allowlist
        // itself has not silently rotted into a permanent no-op.
        var applicationDirectory = FindApplicationSourceDirectory();
        var allowlisted = Path.Combine(
            applicationDirectory,
            ApplicationLayerDiskAccessScanner.AllowlistedRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(
            File.Exists(allowlisted),
            $"The disk-access allowlist points at '{ApplicationLayerDiskAccessScanner.AllowlistedRelativePath}', "
            + "which no longer exists. If the file moved, update AllowlistedRelativePath; if the exception is "
            + "gone entirely, delete the allowlist so the layer is guarded with no exceptions at all.");
    }

    [Fact]
    public void TheAllowlistedFileIsTheOneThatActuallyNeedsTheException()
    {
        // Positive proof the guard is pointed at a real thing: the allowlisted
        // file must itself contain disk access. If it stops doing I/O, the
        // exception -- and the placement argument built on it -- should be
        // retired rather than left standing as a permanent carve-out.
        var applicationDirectory = FindApplicationSourceDirectory();
        var allowlisted = Path.Combine(
            applicationDirectory,
            ApplicationLayerDiskAccessScanner.AllowlistedRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var violations = ApplicationLayerDiskAccessScanner.FindDiskAccessSites(
            File.ReadAllText(allowlisted),
            ApplicationLayerDiskAccessScanner.AllowlistedRelativePath);

        Assert.True(
            violations.Count > 0,
            $"'{ApplicationLayerDiskAccessScanner.AllowlistedRelativePath}' is allowlisted as the layer's one "
            + "disk-access exception but no longer reaches the disk. Remove the allowlist entry and the "
            + "Layering carve-out in its doc comment.");
    }

    // ---- scanner-logic fixtures ------------------------------------------
    // Same rationale as DispatchAwaitDisciplineTests: prove the detector
    // discriminates bad from good BEFORE trusting it to gate the real scan. A
    // detector that always returned "no violations" would make the guard above
    // vacuously green forever.

    [Fact]
    public void ScannerFlagsAPlainFileRead()
    {
        const string badFixture = """
            internal sealed class SomeUseCase
            {
                public string Load(string path) => File.ReadAllText(path);
            }
            """;

        var violations = ApplicationLayerDiskAccessScanner.FindDiskAccessSites(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("fixture.cs:3", violations[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ScannerFlagsAFullyQualifiedFileReadThatEvadesABareNameScan()
    {
        const string badFixture = """
            internal sealed class SomeUseCase
            {
                public string Load(string path) => System.IO.File.ReadAllText(path);
            }
            """;

        Assert.NotEmpty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(badFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerFlagsAnAliasedOrUsingStaticImport()
    {
        const string aliased = "using IOFile = System.IO.File;";
        const string usingStatic = "using static System.IO.File;";
        const string subNamespace = "using System.IO.Compression;";

        Assert.NotEmpty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(aliased, "fixture.cs"));
        Assert.NotEmpty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(usingStatic, "fixture.cs"));
        Assert.NotEmpty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(subNamespace, "fixture.cs"));
    }

    [Fact]
    public void ScannerFlagsFilesystemTypeConstruction()
    {
        const string badFixture = """
            internal sealed class SomeUseCase
            {
                public long Size(string path) => new FileInfo(path).Length;
                public void Watch(string dir) => _ = new FileSystemWatcher(dir);
            }
            """;

        var violations = ApplicationLayerDiskAccessScanner.FindDiskAccessSites(badFixture, "fixture.cs");

        Assert.Collection(
            violations,
            first => Assert.Contains("FileInfo", first, StringComparison.Ordinal),
            second => Assert.Contains("FileSystemWatcher", second, StringComparison.Ordinal));
    }

    [Fact]
    public void ScannerAllowsPathStringManipulation()
    {
        // The exact pre-existing shape in SaveDocumentUseCase and
        // EarlyDocumentCache. Banning `Path.` would go red on untouched code.
        const string goodFixture = """
            internal sealed class SomeUseCase
            {
                public string Normalize(string path)
                {
                    var normalized = Path.GetFullPath(path.Trim());
                    var name = Path.GetFileName(normalized);
                    return string.IsNullOrWhiteSpace(Path.GetExtension(normalized)) ? name + ".md" : name;
                }
            }
            """;

        Assert.Empty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerFlagsGetTempFileNameEvenThoughItLivesOnPath()
    {
        const string badFixture = "var scratch = Path.GetTempFileName();";

        Assert.NotEmpty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(badFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerDoesNotFlagTypeNamesMentionedInComments()
    {
        // EarlyDocumentCache.FileIdentity.TryCapture carries a comment reading
        // "One FileInfo is one metadata snapshot ...". Documentation prose
        // discussing these types must never register as a layering violation.
        const string goodFixture = """
            internal sealed class SomeUseCase
            {
                /// <summary>Deliberately does NOT call File.ReadAllText.</summary>
                // One FileInfo is one metadata snapshot; we do not take one here.
                /* Directory.EnumerateFiles would also be wrong in this layer. */
                public int Count => 0;
            }
            """;

        Assert.Empty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerDoesNotFlagTypeNamesInsidePlainStringLiterals()
    {
        const string goodFixture = """
            internal sealed class SomeUseCase
            {
                public string Explain() => "File.ReadAllText belongs in Infrastructure";
                public string Verbatim() => @"Directory.CreateDirectory too";
            }
            """;

        Assert.Empty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerStillSeesCodeInsideAnInterpolationHole()
    {
        // Interpolated strings are left intact precisely so their `{...}`
        // holes stay scannable -- blanking them would be a silent false
        // negative, the one failure direction a guard must not have.
        const string badFixture = """
            internal sealed class SomeUseCase
            {
                public string Load(string p) => $"content={File.ReadAllText(p)}";
            }
            """;

        Assert.NotEmpty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(badFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerDoesNotFlagAMemberAccessThatMerelyEndsInFile()
    {
        // `document.File.Name` is a property chain, not System.IO.File.
        const string goodFixture = """
            internal sealed class SomeUseCase
            {
                public string Name(Doc document) => document.File.Name;
                public string Other(Doc d) => d.OpenDirectory.Path;
            }
            """;

        Assert.Empty(ApplicationLayerDiskAccessScanner.FindDiskAccessSites(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void BlankingPreservesLineNumbers()
    {
        const string source = """
            // File.ReadAllText in a comment on line 1
            var a = 1;
            var b = File.ReadAllText(p);
            """;

        var violations = ApplicationLayerDiskAccessScanner.FindDiskAccessSites(source, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("fixture.cs:3", violations[0], StringComparison.Ordinal);
    }

    // ---- source enumeration ----------------------------------------------

    private static IEnumerable<(string Source, string RelativePath)> EnumerateApplicationSources()
    {
        var applicationDirectory = FindApplicationSourceDirectory();

        foreach (var path in Directory.EnumerateFiles(applicationDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(applicationDirectory, path);
            var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains("bin", StringComparer.Ordinal) || segments.Contains("obj", StringComparer.Ordinal))
            {
                continue;
            }

            yield return (File.ReadAllText(path), string.Join('/', segments));
        }
    }

    private static string FindApplicationSourceDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (!File.Exists(Path.Combine(directory.FullName, "MarkMello.sln")))
                {
                    continue;
                }

                var application = Path.Combine(directory.FullName, "src", "MarkMello.Application");
                if (Directory.Exists(application))
                {
                    return application;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/MarkMello.Application from the repository root (anchored on MarkMello.sln).");
    }
}

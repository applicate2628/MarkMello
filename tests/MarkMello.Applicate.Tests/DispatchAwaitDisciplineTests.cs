using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkMello.Applicate.Tests;

// Guard against reintroducing the defect fixed by
// work-items/bugs/2026-07-26-headless-dispatch-swallows-assertions.md:
// a non-async test method that calls session.Dispatch(() => { ... },
// CancellationToken.None) and discards the returned Task sends every
// exception thrown inside the lambda -- including an xUnit assertion
// failure -- to a faulted Task nobody ever observes. The test then reports
// Passed regardless of what the lambda actually asserted.
//
// The safe idioms already in this repo are (a) `await session.Dispatch(...)`
// and (b) `session.Dispatch(...).GetAwaiter().GetResult();` (the latter
// often spanning multiple lines, e.g. ApplicateSharedWebViewHostRealHostTests
// .RunOnHost and IpcContractTests.RunOnView). A NAIVE same-line grep for
// `Dispatch(` without `await` over-counts, because the `.GetAwaiter()` can
// sit on a later line -- this scan is STATEMENT-aware: it walks from each
// `.Dispatch(` call site to the statement-terminating `;` at bracket depth
// zero (so a `;` inside the dispatched lambda body does not end the scan
// early), then checks the whole statement for `await` immediately preceding
// the call or `GetAwaiter` anywhere within it.
public static class DispatchAwaitScanner
{
    // Matches `<identifier>.Dispatch(`, optionally preceded by `await `.
    // HeadlessUnitTestSession is dispatched through variables named
    // differently across this project (`session`, `headless`), so the
    // receiver identifier is intentionally NOT hardcoded to "session".
    private static readonly Regex DispatchCallSite = new(
        @"(?<awaited>\bawait\s+)?\b[A-Za-z_][A-Za-z0-9_]*\.Dispatch\(",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> FindUnobservedDispatchStatements(string source, string fileLabel)
    {
        var violations = new List<string>();

        foreach (Match match in DispatchCallSite.Matches(source))
        {
            if (match.Groups["awaited"].Success)
            {
                continue;
            }

            var statementEnd = FindStatementEnd(source, match.Index);
            var statement = source[match.Index..statementEnd];
            if (statement.Contains("GetAwaiter", StringComparison.Ordinal))
            {
                continue;
            }

            var line = source[..match.Index].Count(c => c == '\n') + 1;
            violations.Add(
                $"{fileLabel}:{line}: '{match.Value.TrimEnd('(')}(' is neither awaited nor terminated with " +
                ".GetAwaiter().GetResult() -- any exception thrown inside the dispatched lambda (including an " +
                "xUnit assertion failure) will land on an unobserved faulted Task and the test will report Passed.");
        }

        return violations;
    }

    // Walks from the start of the `.Dispatch(` call to the statement-
    // terminating `;` at bracket depth zero, so a `;` inside the dispatched
    // lambda body (or inside any nested call) never ends the scan early.
    private static int FindStatementEnd(string source, int startIndex)
    {
        var depth = 0;
        for (var i = startIndex; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' or '}' or ']':
                    depth--;
                    break;
                case ';' when depth == 0:
                    return i + 1;
            }
        }

        return source.Length;
    }

    // ---- second guard: catches the async-lambda-with-no-return shape that the
    // await-discipline scan above CANNOT see, because those call sites
    // textually DO have `await` -- see
    // work-items/bugs/2026-07-26-async-lambda-dispatch-overload-swallows-exceptions.md.
    // HeadlessUnitTestSession.Dispatch has three overloads: Dispatch(Action, ...),
    // Dispatch<TResult>(Func<TResult>, ...), and
    // Dispatch<TResult>(Func<Task<TResult>>, ...). An `async () => { ... }`
    // lambda with NO return statement is a valid Func<TResult> with
    // TResult=Task -- the compiler resolves it to that overload, producing
    // Task<Task>. A single `await` on the call unwraps only the OUTER task;
    // the INNER task, carrying the lambda's real completion and every
    // exception raised inside it, is never observed (proven empirically: an
    // unconditional throw inside such a lambda does not fail the test). Giving
    // the lambda an explicit `return <value>;` forces the
    // Dispatch<TResult>(Func<Task<TResult>>, ...) overload instead, which
    // awaits and unwraps the inner task correctly.
    //
    // This is a TEXT scan, not a real parse: `ReturnKeyword` matches the bare
    // word `return` anywhere in the lambda body, including inside a string
    // literal or comment. That is a deliberate, documented simplification (the
    // await-discipline scanner above makes the same tradeoff for `;` and
    // `GetAwaiter`) -- a `return` inside a comment could theoretically produce
    // a false negative, but no such case exists in this project today, and a
    // false negative here is strictly safer than the false positives a
    // stricter-but-wrong parse could introduce.
    private static readonly Regex AsyncLambdaDispatchCallSite = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*\.Dispatch\(\s*async\s*\([^)]*\)\s*=>\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex ReturnKeyword = new(@"\breturn\b", RegexOptions.Compiled);

    public static IReadOnlyList<string> FindAsyncLambdaMissingReturnStatements(string source, string fileLabel)
    {
        var violations = new List<string>();

        foreach (Match match in AsyncLambdaDispatchCallSite.Matches(source))
        {
            // match.Length includes everything up to and including the lambda
            // body's opening '{'; walk from there to the matching '}' (brace-
            // depth aware, so nested object initializers, try/catch blocks,
            // and local braces never end the scan early).
            var bodyStart = match.Index + match.Length - 1;
            var bodyEnd = FindMatchingBrace(source, bodyStart);
            var body = source[bodyStart..bodyEnd];

            if (ReturnKeyword.IsMatch(body))
            {
                continue;
            }

            var line = source[..match.Index].Count(c => c == '\n') + 1;
            violations.Add(
                $"{fileLabel}:{line}: an async lambda passed to '.Dispatch(' has no return statement in its " +
                "body, so it resolves to Dispatch<TResult>(Func<TResult>) with TResult=Task (an unwrapped " +
                "Task<Task>) instead of Dispatch<TResult>(Func<Task<TResult>>). A single await on the call only " +
                "awaits the OUTER task -- the lambda's internal awaits, and any exception raised inside it " +
                "(including an xUnit assertion failure), are never observed. Add an explicit `return <value>;` " +
                "to force the correct overload.");
        }

        return violations;
    }

    // Walks from an opening '{' to its matching closing '}' at brace depth
    // zero (so nested braces of any kind never end the scan early).
    // internal (not private) so AsyncPostGuardScanner below reuses this one
    // brace walker instead of re-typing a second copy of the same logic.
    internal static int FindMatchingBrace(string source, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i + 1;
                    }
                    break;
            }
        }

        return source.Length;
    }
}

// ---- third guard: PRODUCTION `Dispatcher.UIThread.Post(async () => ...)` ----
// work-items/bugs/2026-08-03-production-async-post-lambdas-are-unguarded.md.
//
// Dispatcher.Post takes an Action, so `Post(async () => { ... })` compiles to
// an async void continuation. RUNTIME-VERIFIED against the pinned
// Avalonia.Headless 12.1.0 (not inferred): the exception is NOT swallowed --
// AsyncVoidMethodBuilder hands it to the ambient AvaloniaSynchronizationContext,
// which re-posts it as a plain dispatcher job (Task.ThrowAsync ->
// SendOrPostCallbackDispatcherOperation.InvokeCore), where nothing catches it.
// It escapes the dispatcher loop and terminates the process. In a WinExe with
// no console the user sees only the window vanishing, with the stack trace
// written to a stderr nobody is reading.
//
// So this class is the OPPOSITE of the two guarded above: those swallow, this
// one kills. The remedy is the shape Program.StartActiveDocumentPreRead and
// ApplicateOpenTabPrefetchDocumentSource.OnUiThreadAsync already use -- the
// whole lambda body inside a try with a CATCH-ALL that reports through
// ApplicateTrace -- factored into the single owner
// ApplicateDispatch.PostGuarded so the invariant has one implementation
// instead of one copy per call site.
//
// This scanner therefore enforces a STRUCTURAL invariant rather than an
// owner allowlist (which would rot as call sites move): every async lambda
// handed to any `.Post(` must have its entire body wrapped in a try whose
// catch list contains an UNFILTERED catch-all. A typed `catch (IOException)`
// does not qualify -- that was exactly the state of all five defective sites,
// each of which caught IOException/UnauthorizedAccessException and let every
// other exception type through to the crash path.
public static class AsyncPostGuardScanner
{
    // Receiver-agnostic on purpose: this repo posts through
    // `Dispatcher.UIThread.Post(`, the fully-qualified
    // `Avalonia.Threading.Dispatcher.UIThread.Post(`, AND an injected
    // `_scheduler.Post(` seam in ApplicateAirspaceCompositor. All three bind
    // Action and so share the defect; keying on `.Post(` covers each.
    private static readonly Regex AsyncPostCallSite = new(
        @"\.Post\(\s*async\b",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> FindUnguardedAsyncPostCallSites(string source, string fileLabel)
    {
        var violations = new List<string>();

        // Unlike the two scanners above -- which accept comment/string-literal
        // false NEGATIVES as a documented simplification -- this guard blanks
        // comments and string literals first. It has to: OpenDocumentsService.cs
        // contains the literal text "Dispatcher.UIThread.Post(async) lambdas"
        // inside a prose comment, and a raw regex would report that comment as
        // a violation. A false POSITIVE fails the build on correct code, which
        // is strictly worse than a false negative, so the extra lexing earns
        // its keep here.
        var scannable = BlankOutCommentsAndStringLiterals(source);

        foreach (Match match in AsyncPostCallSite.Matches(scannable))
        {
            var line = scannable[..match.Index].Count(c => c == '\n') + 1;

            var arrow = scannable.IndexOf("=>", match.Index, StringComparison.Ordinal);
            if (arrow < 0)
            {
                violations.Add(Describe(fileLabel, line, "its lambda arrow could not be located"));
                continue;
            }

            var bodyStart = SkipWhitespace(scannable, arrow + 2);
            if (bodyStart >= scannable.Length || scannable[bodyStart] != '{')
            {
                // Expression-bodied: `Post(async () => await Foo())`. There is
                // no body to wrap, so it can never be guarded in place. This is
                // the same "one spelling short" hole the sibling async-lambda
                // guard documented for Dispatch; covered here from the start.
                violations.Add(Describe(
                    fileLabel,
                    line,
                    "it is an EXPRESSION-bodied async lambda, which has no block to guard at all"));
                continue;
            }

            var bodyEnd = DispatchAwaitScanner.FindMatchingBrace(scannable, bodyStart);
            if (!BodyIsWrappedInACatchAllTry(scannable, bodyStart, bodyEnd))
            {
                violations.Add(Describe(
                    fileLabel,
                    line,
                    "its body is not wrapped in a try with an unfiltered catch-all"));
            }
        }

        return violations;
    }

    private static string Describe(string fileLabel, int line, string reason)
        => $"{fileLabel}:{line}: an async lambda is passed to '.Post(' but {reason}. Dispatcher.Post binds " +
           "Action, so the lambda is an async void continuation: any exception it raises is re-posted to the " +
           "dispatcher by AvaloniaSynchronizationContext, escapes the loop unhandled and TERMINATES THE " +
           "PROCESS (runtime-verified on Avalonia.Headless 12.1.0). Post it through " +
           "ApplicateDispatch.PostGuarded, which owns the try/catch-all and reports the failure via " +
           "ApplicateTrace instead of killing the window.";

    // True only when the lambda body's FIRST statement is a try whose catch
    // list contains an unfiltered catch-all (`catch { }` or
    // `catch (Exception ...)` / `catch (System.Exception ...)` with no `when`).
    private static bool BodyIsWrappedInACatchAllTry(string source, int bodyStart, int bodyEnd)
    {
        var first = SkipWhitespace(source, bodyStart + 1);
        if (!IsKeywordAt(source, first, "try"))
        {
            return false;
        }

        var tryBrace = SkipWhitespace(source, first + "try".Length);
        if (tryBrace >= source.Length || source[tryBrace] != '{')
        {
            return false;
        }

        // Walk EVERY catch clause attached to this try: a site may legitimately
        // handle IOException specifically and still end with a catch-all, and
        // only the catch-all decides whether the process can still die here.
        var cursor = DispatchAwaitScanner.FindMatchingBrace(source, tryBrace);
        while (cursor < bodyEnd)
        {
            var clause = SkipWhitespace(source, cursor);
            if (!IsKeywordAt(source, clause, "catch"))
            {
                return false;
            }

            var afterCatch = SkipWhitespace(source, clause + "catch".Length);
            if (afterCatch < source.Length && source[afterCatch] == '{')
            {
                return true;
            }

            if (afterCatch >= source.Length || source[afterCatch] != '(')
            {
                return false;
            }

            var closeParen = FindMatchingParen(source, afterCatch);
            var declaration = source[(afterCatch + 1)..(closeParen - 1)].Trim();
            var afterDeclaration = SkipWhitespace(source, closeParen);

            // `catch (Exception ex) when (IsUnusablePersistedPath(ex))` is a
            // FILTERED catch: the exceptions its filter rejects still escape,
            // so it is not a catch-all. Site 3930 shipped exactly that shape.
            var filtered = IsKeywordAt(source, afterDeclaration, "when");
            if (!filtered && IsCatchAllDeclaration(declaration))
            {
                return true;
            }

            var clauseBrace = source.IndexOf('{', afterDeclaration);
            if (clauseBrace < 0)
            {
                return false;
            }

            cursor = DispatchAwaitScanner.FindMatchingBrace(source, clauseBrace);
        }

        return false;
    }

    private static bool IsCatchAllDeclaration(string declaration)
    {
        var typeName = declaration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return typeName is "Exception" or "System.Exception" or "global::System.Exception";
    }

    private static int FindMatchingParen(string source, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return i + 1;
                    }

                    break;
            }
        }

        return source.Length;
    }

    private static int SkipWhitespace(string source, int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsKeywordAt(string source, int index, string keyword)
    {
        if (index + keyword.Length > source.Length
            || !source.AsSpan(index, keyword.Length).SequenceEqual(keyword))
        {
            return false;
        }

        var after = index + keyword.Length;
        return after >= source.Length || (!char.IsLetterOrDigit(source[after]) && source[after] != '_');
    }

    // Replaces every comment and string/char literal with spaces, PRESERVING
    // both total length and newline positions so offsets and reported line
    // numbers stay valid against the original text. Handles //, /* */,
    // "...", @"...", """raw""" and '...'.
    //
    // Documented simplification: an interpolated string whose holes contain
    // further quoted strings (`$"{a ?? "x"}"`, legal since C# 11) is lexed as
    // if the inner quote closed the outer literal. No such construct exists in
    // this repo's production source today, and the guard is run against that
    // real tree -- a desync would surface as a spurious violation rather than
    // hiding one.
    internal static string BlankOutCommentsAndStringLiterals(string source)
    {
        var buffer = source.ToCharArray();
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    buffer[i++] = ' ';
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                buffer[i++] = ' ';
                buffer[i++] = ' ';
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    Blank(buffer, source, i++);
                }

                for (var k = 0; k < 2 && i < source.Length; k++)
                {
                    buffer[i++] = ' ';
                }
            }
            else if (c == '"' && CountQuoteRun(source, i) >= 3)
            {
                var fence = CountQuoteRun(source, i);
                for (var k = 0; k < fence; k++)
                {
                    buffer[i++] = ' ';
                }

                while (i < source.Length && CountQuoteRun(source, i) < fence)
                {
                    Blank(buffer, source, i++);
                }

                for (var k = 0; k < fence && i < source.Length; k++)
                {
                    buffer[i++] = ' ';
                }
            }
            else if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                buffer[i++] = ' ';
                buffer[i++] = ' ';
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        // "" is an escaped quote inside a verbatim literal.
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            buffer[i++] = ' ';
                            buffer[i++] = ' ';
                            continue;
                        }

                        buffer[i++] = ' ';
                        break;
                    }

                    Blank(buffer, source, i++);
                }
            }
            else if (c is '"' or '\'')
            {
                var quote = c;
                buffer[i++] = ' ';
                while (i < source.Length && source[i] != quote)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        buffer[i++] = ' ';
                    }

                    if (i < source.Length)
                    {
                        Blank(buffer, source, i++);
                    }
                }

                if (i < source.Length)
                {
                    buffer[i++] = ' ';
                }
            }
            else
            {
                i++;
            }
        }

        return new string(buffer);
    }

    // Newlines survive blanking so reported line numbers keep matching the
    // original file; everything else becomes a space.
    private static void Blank(char[] buffer, string source, int index)
    {
        if (source[index] != '\n')
        {
            buffer[index] = ' ';
        }
    }

    private static int CountQuoteRun(string source, int index)
    {
        var count = 0;
        while (index + count < source.Length && source[index + count] == '"')
        {
            count++;
        }

        return count;
    }
}

public sealed class DispatchAwaitDisciplineTests
{
    // ---- the real guard: scans every .cs file this test project owns -----

    [Fact]
    public void EveryDispatchCallSiteInTheTestProjectIsAwaitedOrSynchronouslyUnwrapped()
    {
        var violations = new List<string>();

        foreach (var (source, relativePath) in EnumerateHandAuthoredTestSources())
        {
            violations.AddRange(DispatchAwaitScanner.FindUnobservedDispatchStatements(source, relativePath));
        }

        Assert.Empty(violations);
    }

    // Guard against reintroducing the defect fixed by
    // work-items/bugs/2026-07-26-async-lambda-dispatch-overload-swallows-exceptions.md:
    // an `async () => { ... }` lambda passed to Dispatch with no return
    // statement resolves to the Func<TResult> (TResult=Task) overload instead
    // of Func<Task<TResult>>, so `await session.Dispatch(async () => { ... },
    // CancellationToken.None)` only awaits the OUTER Task<Task> -- every
    // exception (and every internal await) inside the lambda is silently
    // discarded, even an unconditional synchronous throw. This is invisible to
    // the await-discipline guard above because these call sites DO have
    // `await` textually; the gap is which overload that await resolves to.
    [Fact]
    public void EveryAsyncLambdaDispatchCallSiteHasAnExplicitReturnStatement()
    {
        var violations = new List<string>();

        foreach (var (source, relativePath) in EnumerateHandAuthoredTestSources())
        {
            violations.AddRange(DispatchAwaitScanner.FindAsyncLambdaMissingReturnStatements(source, relativePath));
        }

        Assert.Empty(violations);
    }

    // Guard against reintroducing the defect filed as
    // work-items/bugs/2026-08-03-production-async-post-lambdas-are-unguarded.md.
    //
    // NOTE the scope difference from the two guards above: those scan the TEST
    // project, because the swallowed-assertion class they cover is a test-only
    // hazard. This one scans PRODUCTION source, because Dispatcher.Post's async
    // void continuation kills the shipped application. That scope gap is
    // precisely why the earlier guards reported zero violations project-wide
    // while five defective production sites sat in ApplicateMainWindow.cs: they
    // never looked at src/ at all, and they match `.Dispatch(`, not `.Post(`.
    [Fact]
    public void EveryAsyncLambdaPostedToTheDispatcherInProductionSourceIsGuardedByACatchAll()
    {
        var violations = new List<string>();

        foreach (var (source, relativePath) in EnumerateProductionSources())
        {
            violations.AddRange(AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(source, relativePath));
        }

        // Assert.Empty truncates each element, and these violation strings
        // carry the file:line plus the remedy -- the only actionable part.
        // Same strictness, readable output for whoever trips this later.
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // Walks the whole shipped source tree, not just the one file the known
    // defects lived in -- the sixth site in the filing arrived in a DIFFERENT
    // file (Rendering/ApplicateOpenTabPrefetchDocumentSource.cs) one commit
    // before it was filed, so a file-scoped guard would already be stale.
    private static IEnumerable<(string Source, string RelativePath)> EnumerateProductionSources()
    {
        var sourceRoot = FindProductionSourceDirectory();

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, path);
            var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains("bin", StringComparer.Ordinal)
                || segments.Contains("obj", StringComparer.Ordinal)
                || segments.Contains("node_modules", StringComparer.Ordinal))
            {
                continue;
            }

            yield return (File.ReadAllText(path), relativePath);
        }
    }

    private static string FindProductionSourceDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "src");
                if (File.Exists(Path.Combine(directory.FullName, "MarkMello.sln")) && Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository's src/ directory.");
    }

    // Shared by both real-scan guards above: enumerates every hand-authored
    // .cs file in the test project (excluding build output and this file's
    // own fixture literals, which are deliberately-bad strings the
    // scanner-logic tests assert against directly, not live Dispatch call
    // sites).
    private static IEnumerable<(string Source, string RelativePath)> EnumerateHandAuthoredTestSources()
    {
        var testProjectDirectory = FindTestProjectDirectory();

        foreach (var path in Directory.EnumerateFiles(testProjectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(testProjectDirectory, path);
            var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains("bin", StringComparer.Ordinal) || segments.Contains("obj", StringComparer.Ordinal))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(path), "DispatchAwaitDisciplineTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (File.ReadAllText(path), relativePath);
        }
    }

    // ---- scanner-logic fixtures: proves the detector actually discriminates
    // bad from good BEFORE trusting it to gate the real project scan above.
    // A detector that always reports "no violations" would make the guard
    // above vacuously green forever -- these fixtures are the mutation proof
    // that it does not.

    [Fact]
    public void ScannerFlagsTheExactFireAndForgetPatternThatSwallowedAssertions()
    {
        // The exact shape this whole fix addresses: a non-async test body
        // that calls session.Dispatch(...) and discards the Task. An
        // Assert.Fail inside this lambda was empirically proven (the bug
        // report) to still report the test as Passed.
        const string badFixture = """
            public void SomeTest()
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                session.Dispatch(() =>
                {
                    Assert.Fail("this assertion would be silently swallowed");
                }, CancellationToken.None);
            }
            """;

        var violations = DispatchAwaitScanner.FindUnobservedDispatchStatements(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("session.Dispatch(", violations[0]);
    }

    [Fact]
    public void ScannerAllowsTheAwaitedIdiom()
    {
        const string goodFixture = """
            public async Task SomeTest()
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                await session.Dispatch(() =>
                {
                    Assert.Fail("this assertion now genuinely fails the test");
                }, CancellationToken.None);
            }
            """;

        Assert.Empty(DispatchAwaitScanner.FindUnobservedDispatchStatements(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerAllowsTheMultilineGetAwaiterGetResultIdiom()
    {
        // Mirrors ApplicateSharedWebViewHostRealHostTests.RunOnHost and
        // IpcContractTests.RunOnView: the safe idiom for a helper whose own
        // signature cannot be made async.
        const string goodFixture = """
            private static void RunOnHost(Action<Host> body)
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                session.Dispatch(() => body(NewHost()), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            """;

        Assert.Empty(DispatchAwaitScanner.FindUnobservedDispatchStatements(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerDoesNotFlagADifferentlyNamedReceiverVariable()
    {
        // BulkCloseSessionLossReproTests.cs / EditWorkspace*Tests.cs dispatch
        // through a variable named `headless`, not `session` -- the scanner
        // must not be hardcoded to one receiver name.
        const string badFixture = """
            public void SomeTest()
            {
                var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                headless.Dispatch(() =>
                {
                    Assert.Fail("swallowed regardless of the receiver's name");
                }, CancellationToken.None);
            }
            """;

        var violations = DispatchAwaitScanner.FindUnobservedDispatchStatements(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("headless.Dispatch(", violations[0]);
    }

    // ---- scanner-logic fixtures for the second guard (async-lambda-with-
    // no-return). Same rationale as above: prove the detector discriminates
    // bad from good before trusting it to gate the real project scan.

    [Fact]
    public void ScannerFlagsAnAsyncLambdaDispatchCallWithNoReturnStatement()
    {
        // The exact shape from
        // work-items/bugs/2026-07-26-async-lambda-dispatch-overload-swallows-exceptions.md:
        // an async lambda with no return statement resolves to
        // Dispatch<TResult>(Func<TResult>) with TResult=Task, so the single
        // `await` never observes the inner task or anything thrown inside it.
        const string badFixture = """
            public async Task SomeTest()
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                await session.Dispatch(async () =>
                {
                    Assert.Fail("this assertion would be silently swallowed");
                }, CancellationToken.None);
            }
            """;

        var violations = DispatchAwaitScanner.FindAsyncLambdaMissingReturnStatements(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("no return statement", violations[0]);
    }

    [Fact]
    public void ScannerAllowsAnAsyncLambdaDispatchCallWithAnExplicitReturnStatement()
    {
        // The documented fix: an explicit `return <value>;` forces the
        // Dispatch<TResult>(Func<Task<TResult>>) overload, which correctly
        // awaits and unwraps the inner task.
        const string goodFixture = """
            public async Task SomeTest()
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                await session.Dispatch(async () =>
                {
                    Assert.Fail("this assertion now genuinely fails the test");
                    return 0;
                }, CancellationToken.None);
            }
            """;

        Assert.Empty(DispatchAwaitScanner.FindAsyncLambdaMissingReturnStatements(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerDoesNotFlagASynchronousLambdaDispatchCall()
    {
        // The common, unambiguous idiom (no `async` keyword at all) must never
        // be flagged by this guard -- it is out of scope for this detector.
        const string goodFixture = """
            public async Task SomeTest()
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                await session.Dispatch(() =>
                {
                    Assert.Fail("not an async lambda, not this guard's concern");
                }, CancellationToken.None);
            }
            """;

        Assert.Empty(DispatchAwaitScanner.FindAsyncLambdaMissingReturnStatements(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerReturnDetectionIsNotFooledByNestedBracesBeforeTheReturnStatement()
    {
        // Proves the brace-depth walk in FindMatchingBrace correctly spans
        // nested object initializers and try/catch blocks (all of which
        // contain their own '{'/'}' pairs) to find the return statement that
        // sits after all of them, rather than stopping at the first inner '}'.
        const string goodFixture = """
            public async Task SomeTest()
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                await session.Dispatch(async () =>
                {
                    var vm = new FakeMainWindowVm { IsViewer = true, Document = new object() };
                    try
                    {
                        DoSomething();
                    }
                    catch (Exception ex)
                    {
                        capturedException = ex;
                    }
                    return 0;
                }, CancellationToken.None);
            }
            """;

        Assert.Empty(DispatchAwaitScanner.FindAsyncLambdaMissingReturnStatements(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void ScannerFlagsAnAsyncLambdaWithNestedBracesAndStillNoReturnStatement()
    {
        // Same nested-brace shape as above, but with no return statement
        // anywhere -- must still be flagged, proving the brace walk finds the
        // TRUE end of the lambda body (not a false-negative from an inner '}'
        // being mistaken for the end).
        const string badFixture = """
            public async Task SomeTest()
            {
                var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
                await session.Dispatch(async () =>
                {
                    var vm = new FakeMainWindowVm { IsViewer = true, Document = new object() };
                    try
                    {
                        DoSomething();
                    }
                    catch (Exception ex)
                    {
                        capturedException = ex;
                    }
                }, CancellationToken.None);
            }
            """;

        var violations = DispatchAwaitScanner.FindAsyncLambdaMissingReturnStatements(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("no return statement", violations[0]);
    }

    // ---- scanner-logic fixtures for the third guard (unguarded async Post).
    // Same rationale as the two fixture blocks above, and the reason they
    // exist: the production scan is green once the five real sites are fixed,
    // and a detector that silently degenerates to "never reports anything"
    // would stay green forever without these. Each fixture pins ONE
    // discrimination the guard has to keep making.

    [Fact]
    public void AsyncPostScannerFlagsAnAsyncLambdaWithNoTryAtAll()
    {
        // The bare shape of all five production defects before the fix.
        const string badFixture = """
            private void Install()
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await viewModel.OpenPathAsync(path).ConfigureAwait(true);
                });
            }
            """;

        var violations = AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("not wrapped in a try with an unfiltered catch-all", violations[0]);
    }

    [Fact]
    public void AsyncPostScannerAllowsABodyFullyWrappedInACatchAllTry()
    {
        // The remedy shape, as implemented by ApplicateDispatch.PostGuarded.
        const string goodFixture = """
            private void Install()
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await viewModel.OpenPathAsync(path).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        ApplicateTrace.Diag("async-post", "site", ex.Message);
                    }
                });
            }
            """;

        Assert.Empty(AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void AsyncPostScannerFlagsATryWhoseOnlyCatchIsNarrowlyTyped()
    {
        // THE discriminating case. Every one of the five production sites had
        // try/catch blocks -- but only for IOException and
        // UnauthorizedAccessException, so every other exception type still
        // reached the async void crash path. A guard that accepted "has a try"
        // would have called all five compliant and caught nothing.
        const string badFixture = """
            private void Install()
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await openDocs.OpenAsync(path).ConfigureAwait(true);
                    }
                    catch (System.IO.IOException)
                    {
                    }
                });
            }
            """;

        var violations = AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("unfiltered catch-all", violations[0]);
    }

    [Fact]
    public void AsyncPostScannerFlagsACatchAllNeutralisedByAWhenFilter()
    {
        // `catch (Exception ex) when (...)` reads like a catch-all but is not:
        // whatever the filter rejects keeps propagating. Site 3930 shipped
        // exactly this shape.
        const string badFixture = """
            private void Install()
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await ReplayOpenAsync(path).ConfigureAwait(true);
                    }
                    catch (System.Exception ex) when (IsUnusablePersistedPath(ex))
                    {
                    }
                });
            }
            """;

        var violations = AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("unfiltered catch-all", violations[0]);
    }

    [Fact]
    public void AsyncPostScannerAllowsATypedCatchFollowedByACatchAll()
    {
        // Handling one case specifically is fine so long as the clause list
        // still ends in a real catch-all -- proves the scanner walks EVERY
        // clause instead of judging only the first.
        const string goodFixture = """
            private void Install()
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await openDocs.OpenAsync(path).ConfigureAwait(true);
                    }
                    catch (System.IO.IOException)
                    {
                    }
                    catch (Exception ex)
                    {
                        ApplicateTrace.Diag("async-post", "site", ex.Message);
                    }
                });
            }
            """;

        Assert.Empty(AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void AsyncPostScannerFlagsAnExpressionBodiedAsyncLambda()
    {
        // The "one spelling short" hole the sibling Dispatch guard documented
        // but did not close. There is no block here to hold a try at all, so
        // this form can never be guarded in place.
        const string badFixture = """
            private void Install()
            {
                Dispatcher.UIThread.Post(async () => await ReloadAsync().ConfigureAwait(true));
            }
            """;

        var violations = AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("EXPRESSION-bodied", violations[0]);
    }

    [Fact]
    public void AsyncPostScannerDoesNotFlagASynchronousPostedLambda()
    {
        // A non-async lambda posted to the dispatcher is an ordinary Action:
        // it throws on the dispatcher thread synchronously and is out of scope
        // for this guard. There are 30-plus of these in production source, so
        // flagging them would make the guard unusable.
        const string goodFixture = """
            private void Install()
            {
                Dispatcher.UIThread.Post(() => InstallStatusHintAboveWebView());
            }
            """;

        Assert.Empty(AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void AsyncPostScannerDoesNotFlagTheCallShapeMentionedInsideAComment()
    {
        // Verbatim from Editing/OpenDocumentsService.cs, which discusses this
        // exact call shape in prose. A regex-only scan reports it as a live
        // violation and fails the build on correct code -- this is why the
        // scanner blanks comments before matching.
        const string goodFixture = """
            private void Explain()
            {
                // Dispatcher.UIThread.Post(async) lambdas) cannot both pass the
                // cancellation token and stay on the UI thread.
                var reason = "none";
            }
            """;

        Assert.Empty(AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void AsyncPostScannerFindsTheTryEvenBehindALeadingComment()
    {
        // Three of the five real bodies open with a comment block before their
        // first statement. If comment-blanking left the text in place, the
        // "first token must be try" check would read the comment and report a
        // correctly-guarded site as a violation.
        const string goodFixture = """
            private void Install()
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    // CreateNewDocument (Ctrl+N) also clears VM.Document before it
                    // installs the untitled EditorSession.
                    try
                    {
                        await openDocs.OpenAsync(path).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        ApplicateTrace.Diag("async-post", "site", ex.Message);
                    }
                });
            }
            """;

        Assert.Empty(AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(goodFixture, "fixture.cs"));
    }

    [Fact]
    public void AsyncPostScannerFlagsAGuardedTryThatDoesNotOpenTheBody()
    {
        // A try that guards only the TAIL of the body leaves everything before
        // it -- including the first await's continuation -- on the crash path.
        // The invariant is "the whole body", not "a try exists somewhere".
        const string badFixture = """
            private void Install()
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await PrepareAsync().ConfigureAwait(true);
                    try
                    {
                        await openDocs.OpenAsync(path).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        ApplicateTrace.Diag("async-post", "site", ex.Message);
                    }
                });
            }
            """;

        var violations = AsyncPostGuardScanner.FindUnguardedAsyncPostCallSites(badFixture, "fixture.cs");

        Assert.Single(violations);
        Assert.Contains("not wrapped in a try", violations[0]);
    }

    private static string FindTestProjectDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "MarkMello.Applicate.Tests.csproj");
                if (File.Exists(candidate))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the MarkMello.Applicate.Tests project directory.");
    }
}

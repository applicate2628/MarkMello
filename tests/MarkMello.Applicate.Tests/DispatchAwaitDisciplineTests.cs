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
    private static int FindMatchingBrace(string source, int openBraceIndex)
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

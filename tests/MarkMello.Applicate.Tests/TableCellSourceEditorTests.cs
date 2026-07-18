using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using MarkMello.Infrastructure;
using MarkMello.Infrastructure.Diagnostics;
using MarkMello.Infrastructure.Markdown;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class TableCellSourceEditorTests
{
    [Fact]
    public void SourceEditorContractUsesDomainSpanWithoutMarkdigDependency()
    {
        var applicationAssembly = typeof(IMarkdownDocumentRenderer).Assembly;
        var contractType = applicationAssembly.GetType(
            "MarkMello.Application.Abstractions.ITableCellSourceEditor");

        Assert.NotNull(contractType);
        Assert.DoesNotContain(
            applicationAssembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Markdig", StringComparison.Ordinal));

        var locateMethod = contractType.GetMethod("Locate");
        Assert.NotNull(locateMethod);
        Assert.Equal(typeof(TableCellSpan?), locateMethod.ReturnType);

        var parseMethod = contractType.GetMethod("ParsePlainCell");
        Assert.NotNull(parseMethod);
        Assert.Equal(
            "MarkMello.Application.Abstractions.TableCellSourceSnapshot",
            Nullable.GetUnderlyingType(parseMethod.ReturnType)?.FullName);

        var rawLocateMethod = typeof(RawTableCellLocator).GetMethod(nameof(RawTableCellLocator.Locate));
        Assert.NotNull(rawLocateMethod);
        Assert.Equal(typeof(TableCellSpan?), rawLocateMethod.ReturnType);
    }

    [Fact]
    public void InfrastructureRegistersMarkdigSourceEditorForApplicationContract()
    {
        var implementationType = typeof(RawTableCellLocator).Assembly.GetType(
            "MarkMello.Infrastructure.Markdown.MarkdigTableCellSourceEditor");

        Assert.NotNull(implementationType);
        Assert.Contains(typeof(ITableCellSourceEditor), implementationType.GetInterfaces());

        var services = new ServiceCollection();
        services.AddInfrastructure(new StopwatchStartupMetrics(), []);

        var descriptor = Assert.Single(
            services,
            service => service.ServiceType == typeof(ITableCellSourceEditor));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(implementationType, descriptor.ImplementationType);
    }

    [Fact]
    public void LocateReturnsDomainSpanAndFailsClosedForMissingCoordinates()
    {
        const string source = "| A | B |\n|---|---|\n| left | right |\n";
        var editor = new MarkdigTableCellSourceEditor();

        var span = editor.Locate(source, line: 2, cellIndex: 1);

        Assert.NotNull(span);
        Assert.Equal(" right ", source.Substring(span.Value.Start, span.Value.Length));
        Assert.Null(editor.Locate(source, line: 99, cellIndex: 0));
        Assert.Null(editor.Locate(source, line: 2, cellIndex: 99));
        Assert.Null(editor.Locate(source, line: -1, cellIndex: 0));
    }

    [Fact]
    public void ParsePlainCellReturnsDecodedTextAndStableTableShape()
    {
        const string source =
            "# Heading\n\n"
            + "| A | B |\n"
            + "|---|---|\n"
            + "| a\\|b | plain |\n\n"
            + "paragraph\n\n"
            + "| C |\n"
            + "|---|\n"
            + "| final |\n";
        var editor = new MarkdigTableCellSourceEditor();

        var first = editor.ParsePlainCell(source, line: 4, cellIndex: 0);
        var second = editor.ParsePlainCell(source, line: 10, cellIndex: 0);

        Assert.NotNull(first);
        Assert.Equal("a|b", first.Value.Text);
        Assert.Equal(" a\\|b ", source.Substring(first.Value.Span.Start, first.Value.Span.Length));
        Assert.Equal(0, first.Value.TableIndex);
        Assert.Equal(2, first.Value.TableStartLine);
        Assert.Equal(4, first.Value.TableEndLine);
        Assert.Equal(1, first.Value.RowIndex);
        Assert.Equal(0, first.Value.ColumnIndex);
        Assert.Equal(2, first.Value.RowCount);
        Assert.Equal(2, first.Value.ColumnCount);

        Assert.NotNull(second);
        Assert.Equal("final", second.Value.Text);
        Assert.Equal(1, second.Value.TableIndex);
        Assert.Equal(8, second.Value.TableStartLine);
        Assert.Equal(10, second.Value.TableEndLine);
        Assert.Equal(1, second.Value.RowIndex);
        Assert.Equal(0, second.Value.ColumnIndex);
        Assert.Equal(2, second.Value.RowCount);
        Assert.Equal(1, second.Value.ColumnCount);
    }

    [Fact]
    public void ParsePlainCellReturnsNullForRichOrMissingCells()
    {
        const string source =
            "| Plain | Bold | Code | Entity | Math |\n"
            + "|---|---|---|---|---|\n"
            + "| text | **bold** | `code` | &amp; | $x$ |\n";
        var editor = new MarkdigTableCellSourceEditor();

        Assert.NotNull(editor.ParsePlainCell(source, line: 2, cellIndex: 0));
        Assert.Null(editor.ParsePlainCell(source, line: 2, cellIndex: 1));
        Assert.Null(editor.ParsePlainCell(source, line: 2, cellIndex: 2));
        Assert.Null(editor.ParsePlainCell(source, line: 2, cellIndex: 3));
        Assert.Null(editor.ParsePlainCell(source, line: 2, cellIndex: 4));
        Assert.Null(editor.ParsePlainCell(source, line: 99, cellIndex: 0));
        Assert.Null(editor.ParsePlainCell(source, line: 2, cellIndex: 99));
    }
}

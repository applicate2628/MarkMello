namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// One document heading captured by the renderer and surfaced to the host
/// for the Avalonia-side Table of Contents panel.
///
/// <para>The Applicate renderer scans <c>main.mm-document h1..h6</c> after
/// each chrome rebuild (right after the <c>main</c> innerHTML swap inside
/// <c>ensureChromeNodes</c>) and posts the resulting list via the
/// <c>headings-updated</c> IPC message. Heading <see cref="Id"/> values are
/// stable slugs produced by <see cref="MarkMello.Domain.MarkdownHeadingAnchorSlugger"/>
/// during HTML generation, so the host can send a <c>scroll-to-heading</c>
/// IPC back with the same id to seek the document to that heading.</para>
///
/// <para>The type lives in the Presentation project so the
/// <see cref="MainWindowViewModel"/> can carry a shell-wide
/// <c>ObservableCollection&lt;DocumentHeading&gt;</c> for the TOC binding;
/// the Applicate-side fork-only consumer adapter constructs values from the
/// renderer payload and assigns the collection.</para>
/// </summary>
/// <param name="Id">Stable anchor slug, matches the heading element's
/// <c>id</c> attribute in the generated HTML.</param>
/// <param name="Level">Heading depth: 1 for h1 .. 6 for h6.</param>
/// <param name="Text">Plain-text content of the heading element.</param>
/// <param name="Indent">Pre-computed horizontal indent in device-independent
/// pixels, derived as <c>(Level - 1) * 12</c>. Stored on the record so the
/// XAML/code-built TOC row can bind to it directly without a value
/// converter.</param>
public sealed record DocumentHeading(string Id, int Level, string Text, double Indent);

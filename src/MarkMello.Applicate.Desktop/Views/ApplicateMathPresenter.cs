using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CSharpMath.Avalonia;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Builds a CSharpMath <see cref="MathView"/> for already-normalized TeX, with a
/// graceful <see cref="TextBlock"/> fallback when CSharpMath cannot parse the
/// input. CSharpMath 0.5.1 has narrower coverage than the document body's KaTeX;
/// most input-shape gaps are pre-fixed in
/// <see cref="Math.ApplicateMarkdownDocumentRenderer.NormalizeTexForRenderer"/>
/// (e.g. the "\command=" tokenizer glue), but rather than ever render CSharpMath's
/// red inline "Invalid command" error into the TOC / host math surfaces, we detect
/// the parse failure via <see cref="MathView"/>'s <c>ErrorMessage</c> (set
/// synchronously by the <c>LaTeX</c> setter) and substitute a readable text
/// approximation. Callers pass TeX ALREADY run through
/// <c>NormalizeTexForRenderer</c>. Single owner for the three direct MathView call
/// sites (TOC panel, wrapping math view, host markdown inline math).
/// </summary>
internal static class ApplicateMathPresenter
{
    public static Control CreateOrFallback(
        string normalizedTex,
        float fontSize,
        Color textColor,
        Thickness margin,
        HorizontalAlignment horizontalAlignment = HorizontalAlignment.Stretch)
    {
        var view = new MathView
        {
            LaTeX = normalizedTex,
            FontSize = fontSize,
            TextColor = textColor,
            ErrorColor = Colors.OrangeRed,
            // Errors are handled by the fallback below — never paint CSharpMath's
            // inline error string into the UI.
            DisplayErrorInline = false,
            Margin = margin,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The LaTeX setter parses synchronously and copies the painter's error,
        // so ErrorMessage is populated by now.
        if (string.IsNullOrEmpty(view.ErrorMessage))
        {
            return view;
        }

        return new TextBlock
        {
            Text = ToReadableApproximation(normalizedTex),
            FontSize = fontSize,
            Foreground = new SolidColorBrush(textColor),
            Margin = margin,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
    }

    // Safety-net TeX→Unicode map, only reached when CSharpMath still fails after
    // normalization. Unknown commands degrade to their name (no backslash); braces
    // are dropped. This is a readability approximation, NOT a second math parser.
    private static readonly IReadOnlyDictionary<string, string> SymbolMap = new Dictionary<string, string>
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
        ["epsilon"] = "ε", ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
        ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π",
        ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ",
        ["phi"] = "φ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ",
        ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ", ["Psi"] = "Ψ",
        ["Omega"] = "Ω",
        ["times"] = "×", ["cdot"] = "·", ["pm"] = "±", ["mp"] = "∓", ["leq"] = "≤",
        ["geq"] = "≥", ["neq"] = "≠", ["approx"] = "≈", ["equiv"] = "≡",
        ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇", ["in"] = "∈",
        ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["Rightarrow"] = "⇒",
        ["sum"] = "∑", ["int"] = "∫", ["sqrt"] = "√",
    };

    private static string ToReadableApproximation(string tex)
    {
        var sb = new StringBuilder(tex.Length);
        var i = 0;
        while (i < tex.Length)
        {
            var c = tex[i];
            if (c == '\\' && i + 1 < tex.Length && char.IsLetter(tex[i + 1]))
            {
                var start = ++i;
                while (i < tex.Length && char.IsLetter(tex[i]))
                {
                    i++;
                }
                var name = tex.Substring(start, i - start);
                sb.Append(SymbolMap.TryGetValue(name, out var sym) ? sym : name);
            }
            else if (c == '{' || c == '}' || c == '\\')
            {
                i++; // drop braces and stray backslashes
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString().Trim();
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace DwgParaPdf;

/// <summary>
/// Remove códigos de formatação do AutoCAD (MTEXT inline codes e sequências %%) deixando só o texto legível.
/// </summary>
public static partial class LimpadorTextoCad
{
    private const string BarraLiteral = "\u0001";
    private const string AbreLiteral = "\u0002";
    private const string FechaLiteral = "\u0003";

    [GeneratedRegex(@"\\[PXN]")] private static partial Regex QuebraRx();
    [GeneratedRegex(@"\\U\+([0-9A-Fa-f]{4})")] private static partial Regex UnicodeRx();
    [GeneratedRegex(@"\\M\+[0-9A-Fa-f]{5}")] private static partial Regex MultibyteRx();
    [GeneratedRegex(@"\\S([^;]*?)[\^/#]([^;]*?);")] private static partial Regex EmpilhadoRx();
    [GeneratedRegex(@"\\[ACcFfHhWwQqTp][^;]*;")] private static partial Regex CodigoComParametroRx();
    [GeneratedRegex(@"\\[A-Za-z]")] private static partial Regex CodigoSimplesRx();
    [GeneratedRegex(@"%%(\d{3}|[A-Za-z%])")] private static partial Regex PorcentoRx();
    [GeneratedRegex(@"[ \t\u00A0]{2,}")] private static partial Regex EspacosRx();

    /// <summary>Limpa conteúdo de MTEXT (e de qualquer campo que aceite marcação MTEXT: MLEADER, cota, célula de tabela).</summary>
    public static string LimparMText(string? bruto)
    {
        if (string.IsNullOrEmpty(bruto)) return string.Empty;

        // protege escapes literais antes de qualquer outra coisa
        var s = bruto.Replace(@"\\", BarraLiteral).Replace(@"\{", AbreLiteral).Replace(@"\}", FechaLiteral);

        s = QuebraRx().Replace(s, "\n");                         // \P parágrafo, \X separador de cota, \N coluna
        s = s.Replace(@"\~", " ");                               // espaço não separável
        s = UnicodeRx().Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        s = MultibyteRx().Replace(s, string.Empty);
        s = EmpilhadoRx().Replace(s, m => $"{m.Groups[1].Value.Trim()}/{m.Groups[2].Value.Trim()}"); // \S1^2; frações e tolerâncias
        s = CodigoComParametroRx().Replace(s, string.Empty);      // \A1; \C3; \fArial|b1; \H2.5x; \W0.8; \Q15; \T1.2; \pxi-3;
        s = CodigoSimplesRx().Replace(s, string.Empty);           // \L \l \O \o \K \k e códigos desconhecidos
        s = s.Replace("{", string.Empty).Replace("}", string.Empty);

        s = s.Replace(BarraLiteral, "\\").Replace(AbreLiteral, "{").Replace(FechaLiteral, "}");
        s = SubstituirPorcento(s);
        return NormalizarEspacos(s);
    }

    /// <summary>Limpa conteúdo de TEXT / ATTRIB (linha única, só sequências %% e \U+).</summary>
    public static string LimparTexto(string? bruto)
    {
        if (string.IsNullOrEmpty(bruto)) return string.Empty;
        var s = UnicodeRx().Replace(bruto, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        s = SubstituirPorcento(s);
        return NormalizarEspacos(s);
    }

    private static string SubstituirPorcento(string s) => PorcentoRx().Replace(s, m =>
    {
        var codigo = m.Groups[1].Value;
        switch (codigo)
        {
            case "d": case "D": return "°";
            case "c": case "C": return "Ø";
            case "p": case "P": return "±";
            case "%": return "%";
            case "u": case "U": case "o": case "O": return string.Empty; // sublinhado / sobrelinha (só formatação)
        }
        if (codigo.Length == 3 && int.TryParse(codigo, out var n))
            return n is >= 32 and < 0xFFFF ? ((char)n).ToString() : string.Empty;
        return m.Value;
    });

    private static string NormalizarEspacos(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var linhaBruta in s.Split('\n'))
        {
            var limpa = new StringBuilder(linhaBruta.Length);
            foreach (var c in linhaBruta)
            {
                if (c == '\t' || c == '\u00A0') limpa.Append(' ');
                else if (c < ' ' || char.IsSurrogate(c)) continue;
                else limpa.Append(c);
            }
            var linha = EspacosRx().Replace(limpa.ToString(), " ").Trim();
            if (linha.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(linha);
        }
        return sb.ToString();
    }
}

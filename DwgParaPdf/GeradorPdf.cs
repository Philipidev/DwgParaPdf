using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace DwgParaPdf;

/// <summary>
/// Gera um PDF só de texto (sem imagens) a partir do <see cref="DocumentoExtraido"/>, com MigraDoc.
/// Texto real no content stream: qualquer extrator de PDF (RAG) recupera o conteúdo.
/// </summary>
public static class GeradorPdf
{
    public const string FamiliaFonte = "Arial";
    public const string VariavelAmbienteFonte = "DWGPARAPDF_FONTE";

    private static readonly object Trava = new();
    private static bool _fontesConfiguradas;

    public static byte[] Gerar(DocumentoExtraido extraido)
    {
        ArgumentNullException.ThrowIfNull(extraido);
        ConfigurarFontes();

        var documento = MontarDocumento(extraido);

        var renderizador = new PdfDocumentRenderer { Document = documento };
        renderizador.RenderDocument();

        var pdf = renderizador.PdfDocument;
        pdf.Info.Title = Sanear(extraido.NomeOrigem);
        pdf.Info.Subject = "Texto extraído de desenho CAD para indexação";
        pdf.Info.Creator = "DwgParaPdf";
        pdf.Info.Keywords = Sanear(string.Join(", ", extraido.Camadas.Take(50)));

        using var ms = new MemoryStream();
        pdf.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static Document MontarDocumento(DocumentoExtraido extraido)
    {
        var doc = new Document();
        doc.Info.Title = Sanear(extraido.NomeOrigem);
        doc.Info.Subject = "Texto extraído de desenho CAD para indexação";
        doc.Info.Author = "DwgParaPdf";

        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = FamiliaFonte;
        normal.Font.Size = Unit.FromPoint(9);
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var titulo = doc.Styles[StyleNames.Heading1]!;
        titulo.Font.Size = Unit.FromPoint(15);
        titulo.Font.Bold = true;
        titulo.ParagraphFormat.SpaceAfter = Unit.FromPoint(8);

        var secaoTitulo = doc.Styles[StyleNames.Heading2]!;
        secaoTitulo.Font.Size = Unit.FromPoint(11.5);
        secaoTitulo.Font.Bold = true;
        secaoTitulo.ParagraphFormat.SpaceBefore = Unit.FromPoint(10);
        secaoTitulo.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        secaoTitulo.ParagraphFormat.KeepWithNext = true;

        var secao = doc.AddSection();
        secao.PageSetup.PageFormat = PageFormat.A4;
        secao.PageSetup.TopMargin = Unit.FromCentimeter(2);
        secao.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        secao.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        secao.PageSetup.RightMargin = Unit.FromCentimeter(2);

        var rodape = secao.Footers.Primary.AddParagraph();
        rodape.Format.Alignment = ParagraphAlignment.Center;
        rodape.Format.Font.Size = Unit.FromPoint(8);
        rodape.AddText(Sanear(extraido.NomeOrigem) + " - página ");
        rodape.AddPageField();
        rodape.AddText(" de ");
        rodape.AddNumPagesField();

        secao.AddParagraph(Sanear(extraido.NomeOrigem), StyleNames.Heading1);

        foreach (var (tituloSecao, paragrafos) in extraido.Secoes())
        {
            secao.AddParagraph(Sanear(tituloSecao), StyleNames.Heading2);
            foreach (var texto in paragrafos)
                AdicionarParagrafo(secao, texto);
        }

        return doc;
    }

    private static void AdicionarParagrafo(Section secao, string texto)
    {
        var paragrafo = secao.AddParagraph();
        var linhas = Sanear(texto).Split('\n');
        for (var i = 0; i < linhas.Length; i++)
        {
            if (i > 0) paragrafo.AddLineBreak();
            paragrafo.AddText(linhas[i]);
        }
    }

    /// <summary>Remove o que o motor de fontes não trata: controles, surrogates e não-caracteres.</summary>
    private static string Sanear(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return string.Empty;
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto)
        {
            if (c == '\n') { sb.Append(c); continue; }
            if (c < ' ' || char.IsSurrogate(c) || c is '￾' or '￿') continue;
            sb.Append(c == ' ' ? ' ' : c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ fontes

    /// <summary>
    /// PDFsharp (build Core) não resolve fonte sozinho. Ordem: variável de ambiente, pasta 'fontes' ao lado do
    /// executável, caminhos comuns de Linux/macOS; por fim, fontes do Windows quando rodando em Windows.
    /// </summary>
    internal static void ConfigurarFontes()
    {
        if (_fontesConfiguradas) return;
        lock (Trava)
        {
            if (_fontesConfiguradas) return;

            if (GlobalFontSettings.FontResolver is null)
            {
                var ttf = LocalizarFonteTrueType();
                if (ttf is not null)
                    GlobalFontSettings.FontResolver = new ResolvedorFonteUnica(ttf);
                else if (OperatingSystem.IsWindows())
                {
                    var windows = new ResolvedorFontesWindows(new[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
                    });
                    if (windows.TemFamiliaPadrao) GlobalFontSettings.FontResolver = windows;
                    else GlobalFontSettings.UseWindowsFontsUnderWindows = true;
                }
                else
                    throw new InvalidOperationException(
                        $"Nenhuma fonte TrueType encontrada. Defina {VariavelAmbienteFonte}=/caminho/fonte.ttf " +
                        "ou coloque um .ttf na pasta 'fontes' ao lado do executável.");
            }

            _fontesConfiguradas = true;
        }
    }

    private static string? LocalizarFonteTrueType()
    {
        var porAmbiente = Environment.GetEnvironmentVariable(VariavelAmbienteFonte);
        if (!string.IsNullOrWhiteSpace(porAmbiente) && File.Exists(porAmbiente))
            return porAmbiente;

        var pasta = Path.Combine(AppContext.BaseDirectory, "fontes");
        if (Directory.Exists(pasta))
        {
            var primeira = Directory.EnumerateFiles(pasta, "*.ttf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (primeira is not null) return primeira;
        }

        if (OperatingSystem.IsWindows()) return null;

        string[] candidatas =
        {
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf",
            "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/Library/Fonts/Arial.ttf",
        };
        return candidatas.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Resolve fontes pelos arquivos .ttf instalados no Windows: aceita o nome da família ("Calibri", "Times New Roman")
    /// ou o nome do arquivo sem extensão ("calibri", "arialbd"), que é o que o AutoCAD guarda no estilo de texto.
    /// Variantes negrito/itálico seguem a convenção de sufixos dos arquivos (bd, b, i, it, bi, z).
    /// </summary>
    private sealed class ResolvedorFontesWindows : IFontResolver
    {
        private static readonly Dictionary<string, string> Familias = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Arial"] = "arial", ["Arial Narrow"] = "arialn", ["Times New Roman"] = "times", ["Courier New"] = "cour",
            ["Trebuchet MS"] = "trebuc", ["Microsoft Sans Serif"] = "micross", ["Segoe UI"] = "segoeui", ["Comic Sans MS"] = "comic",
            ["Lucida Console"] = "lucon", ["Verdana"] = "verdana", ["Tahoma"] = "tahoma", ["Calibri"] = "calibri", ["Georgia"] = "georgia",
            ["Consolas"] = "consola", ["Impact"] = "impact", ["Symbol"] = "symbol", ["Wingdings"] = "wingding", ["Candara"] = "candara",
            ["Corbel"] = "corbel", ["Constantia"] = "constan", ["Palatino Linotype"] = "pala", ["Franklin Gothic Medium"] = "framd",
        };

        private readonly Dictionary<string, string> _arquivos = new(StringComparer.OrdinalIgnoreCase); // nome sem extensão → caminho
        private readonly Dictionary<string, byte[]> _dados = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _trava = new();

        public ResolvedorFontesWindows(IEnumerable<string> pastas)
        {
            foreach (var pasta in pastas)
            {
                if (string.IsNullOrWhiteSpace(pasta) || !Directory.Exists(pasta)) continue;
                foreach (var arquivo in Directory.EnumerateFiles(pasta, "*.ttf"))
                    _arquivos.TryAdd(Path.GetFileNameWithoutExtension(arquivo), arquivo);
            }
        }

        public bool TemFamiliaPadrao => _arquivos.ContainsKey("arial");

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            var raiz = Familias.TryGetValue(familyName, out var mapeada) ? mapeada : familyName.Trim();

            var candidatos = new List<(string Nome, bool CobreNegrito, bool CobreItalico)>();
            if (bold && italic) candidatos.AddRange(new[] { (raiz + "bi", true, true), (raiz + "z", true, true) });
            if (bold) candidatos.AddRange(new[] { (raiz + "bd", true, false), (raiz + "b", true, false) });
            if (italic) candidatos.AddRange(new[] { (raiz + "i", false, true), (raiz + "it", false, true) });
            candidatos.Add((raiz, false, false));

            foreach (var (nome, cobreNegrito, cobreItalico) in candidatos)
                if (_arquivos.TryGetValue(nome, out var caminho))
                    return new FontResolverInfo(caminho, bold && !cobreNegrito, italic && !cobreItalico);

            return null;
        }

        public byte[]? GetFont(string faceName)
        {
            lock (_trava)
            {
                if (!_dados.TryGetValue(faceName, out var dados))
                {
                    if (!File.Exists(faceName)) return null;
                    _dados[faceName] = dados = File.ReadAllBytes(faceName);
                }
                return dados;
            }
        }
    }

    /// <summary>Mapeia qualquer família pedida para um único arquivo TrueType (negrito/itálico simulados).</summary>
    private sealed class ResolvedorFonteUnica : IFontResolver
    {
        private const string Face = "DwgParaPdf#FonteUnica";
        private readonly byte[] _dados;

        public ResolvedorFonteUnica(string caminho) => _dados = File.ReadAllBytes(caminho);

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
            => new FontResolverInfo(Face, bold, italic);

        public byte[]? GetFont(string faceName) => faceName == Face ? _dados : null;
    }
}

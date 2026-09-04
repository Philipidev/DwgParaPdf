using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using CSMath;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DwgParaPdf.Desenho;

/// <summary>Nível de detalhe da geometria. O texto é sempre preservado integralmente.</summary>
public enum NivelDetalhe
{
    /// <summary>Fiel: só remove vértices coincidentes (padrão).</summary>
    Alto,

    /// <summary>Simplifica polilinhas a 0,3 pt, arcos com metade dos segmentos, hachuras de padrão com até 8 mil linhas, coordenadas com 1 decimal.</summary>
    Medio,

    /// <summary>Simplifica a 0,8 pt, arcos grosseiros, hachuras de padrão viram preenchimento translúcido, entidades menores que 0,8 pt somem.</summary>
    Baixo,
}

/// <summary>Parâmetros concretos de cada nível de detalhe (em pontos da página).</summary>
internal sealed record ParametrosLod(double ToleranciaPt, double PassoArcoPt, int LimiteLinhasHachura, double ArredondamentoPt, double MinimoEntidadePt)
{
    public ModoCores Cores { get; init; } = ModoCores.Texto;

    public static ParametrosLod De(NivelDetalhe nivel, ModoCores cores = ModoCores.Texto) => (nivel switch
    {
        NivelDetalhe.Baixo => new ParametrosLod(0.8, 6.0, 0, 0.1, 0.8),
        NivelDetalhe.Medio => new ParametrosLod(0.3, 3.0, 8_000, 0.1, 0.3),
        _ => new ParametrosLod(0.02, 1.5, 40_000, 0, 0),
    }) with { Cores = cores };
}

/// <summary>Tratamento das cores para papel branco (o AutoCAD desenha em fundo escuro; amarelo e ciano somem no branco).</summary>
public enum ModoCores
{
    /// <summary>Cores do arquivo (só branco/índice 7 vira preto).</summary>
    Original,

    /// <summary>Textos claros demais são escurecidos mantendo o matiz; geometria intacta (padrão).</summary>
    Texto,

    /// <summary>Todo texto preto; geometria intacta.</summary>
    TextoPreto,

    /// <summary>Textos e geometria claros demais são escurecidos mantendo o matiz.</summary>
    Tudo,

    /// <summary>Tudo preto, como o monochrome.ctb do AutoCAD.</summary>
    Mono,
}

public sealed class OpcoesDesenho
{
    /// <summary>Nível de detalhe da geometria (o texto nunca é degradado). Reduz o tamanho do PDF em desenhos densos.</summary>
    public NivelDetalhe Lod { get; init; } = NivelDetalhe.Alto;

    /// <summary>Como tratar cores claras sobre o papel branco.</summary>
    public ModoCores Cores { get; init; } = ModoCores.Texto;

    /// <summary>Gera uma página com o espaço do modelo ajustado ao papel.</summary>
    public bool IncluirModelo { get; init; } = true;

    /// <summary>Gera uma página por layout de papel com conteúdo (viewports renderizadas e recortadas).</summary>
    public bool IncluirLayouts { get; init; } = true;

    /// <summary>Altura mínima desejada, no papel, para o texto mediano do modelo; define o tamanho da página (A4 até 4A0).</summary>
    public double AlturaTextoMinimaMm { get; init; } = 1.8;

    public double MargemMm { get; init; } = 10;
}

public sealed class EstatisticasDesenho
{
    public int Paginas { get; internal set; }
    public int EntidadesDesenhadas { get; internal set; }
    public int EntidadesComErro { get; internal set; }
    public int Viewports { get; internal set; }
}

/// <summary>
/// Monta o PDF vetorial do desenho: uma página para o espaço do modelo (extensão ajustada ao papel) e uma
/// página por layout de papel, com as viewports renderizando o modelo recortado, como o AutoCAD plota.
/// </summary>
internal static class GeradorPdfDesenho
{
    private const double PtPorMm = 72.0 / 25.4;

    // do menor para o maior; para além de A0 o PDF fica pesado demais para os visualizadores sem ganho real (o PDF é vetorial)
    private static readonly (string Nome, double LarguraMm, double AlturaMm)[] Papeis =
    {
        ("A4", 210, 297), ("A3", 297, 420), ("A2", 420, 594), ("A1", 594, 841), ("A0", 841, 1189),
    };

    public static PdfDocument Gerar(CadDocument doc, string nomeOrigem, OpcoesDesenho opcoes, List<string> avisos, EstatisticasDesenho stats)
    {
        GeradorPdf.ConfigurarFontes();

        var pdf = new PdfDocument();
        pdf.Options.CompressContentStreams = true;
        pdf.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        var lod = ParametrosLod.De(opcoes.Lod, opcoes.Cores);
        pdf.Info.Title = nomeOrigem;
        pdf.Info.Subject = "Desenho CAD convertido em PDF vetorial";
        pdf.Info.Creator = "DwgParaPdf";

        var tiposNaoSuportados = new Dictionary<string, int>();

        if (opcoes.IncluirModelo)
            PaginaModelo(pdf, doc, opcoes, lod, avisos, stats, tiposNaoSuportados);

        if (opcoes.IncluirLayouts)
        {
            foreach (var layout in doc.Layouts.Where(l => l.IsPaperSpace).OrderBy(l => l.TabOrder).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    PaginaLayout(pdf, doc, layout, opcoes, lod, avisos, stats, tiposNaoSuportados);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    avisos.Add($"Layout '{layout.Name}' não renderizado: {ex.Message}");
                }
            }
        }

        if (pdf.PageCount == 0)
        {
            var pagina = pdf.AddPage();
            pagina.Width = XUnit.FromMillimeter(210);
            pagina.Height = XUnit.FromMillimeter(297);
            using var gfx = XGraphics.FromPdfPage(pagina);
            gfx.DrawString("(desenho sem conteúdo gráfico)", new XFont(GeradorPdf.FamiliaFonte, 12), XBrushes.Black,
                new XRect(0, 0, pagina.Width.Point, pagina.Height.Point), XStringFormats.Center);
        }

        foreach (var (tipo, quantidade) in tiposNaoSuportados.OrderByDescending(p => p.Value))
            avisos.Add($"Tipo de entidade não desenhado: {tipo} (x{quantidade})");

        stats.Paginas = pdf.PageCount;
        return pdf;
    }

    // ------------------------------------------------------------------ modelo

    private static void PaginaModelo(PdfDocument pdf, CadDocument doc, OpcoesDesenho opcoes, ParametrosLod lod, List<string> avisos, EstatisticasDesenho stats, Dictionary<string, int> naoSuportados)
    {
        var entidades = doc.ModelSpace.Entities.Where(e => e is not Viewport).ToList();
        if (entidades.Count == 0) return;

        // renderizador provisório só para as regras de visibilidade das camadas
        using var sonda = XGraphics.CreateMeasureContext(new XSize(100, 100), XGraphicsUnit.Point, XPageDirection.Downwards);
        var regras = new RenderizadorEntidades(sonda, doc, avisos, lod);
        var visiveis = entidades.Where(e => !e.IsInvisible && regras.CamadaVisivel(e.Layer, null)).ToList();
        if (visiveis.Count == 0) return;

        var extensao = Extensao(visiveis, doc.Header?.ModelSpaceExtMin, doc.Header?.ModelSpaceExtMax);
        if (extensao is null) { avisos.Add("Espaço do modelo sem extensão calculável; página do modelo omitida."); return; }
        var (min, max) = extensao.Value;

        var largura = Math.Max(max.X - min.X, 1e-9);
        var altura = Math.Max(max.Y - min.Y, 1e-9);
        var paisagem = largura >= altura;
        var medianaTexto = MedianaAlturaTexto(visiveis);

        // escala: a menor folha ISO (A4..A0) em que o texto mediano fique legível; a página final segue a proporção
        // do conteúdo dentro dessa folha (sem faixas em branco quando o desenho é muito alongado)
        double escalaMm = 0;
        foreach (var papel in Papeis)
        {
            var folhaW = paisagem ? papel.AlturaMm : papel.LarguraMm;
            var folhaH = paisagem ? papel.LarguraMm : papel.AlturaMm;
            escalaMm = Math.Min((folhaW - 2 * opcoes.MargemMm) / largura, (folhaH - 2 * opcoes.MargemMm) / altura);
            if (medianaTexto <= 0 && papel.Nome == "A3") break;
            if (medianaTexto > 0 && medianaTexto * escalaMm >= opcoes.AlturaTextoMinimaMm) break;
        }
        var paginaW = Math.Max(largura * escalaMm + 2 * opcoes.MargemMm, 105);
        var paginaH = Math.Max(altura * escalaMm + 2 * opcoes.MargemMm, 105);

        var pagina = pdf.AddPage();
        pagina.Width = XUnit.FromMillimeter(paginaW);
        pagina.Height = XUnit.FromMillimeter(paginaH);

        var escalaPt = escalaMm * PtPorMm;
        var deslocX = (paginaW - largura * escalaMm) / 2 * PtPorMm;
        var deslocY = (paginaH - altura * escalaMm) / 2 * PtPorMm;
        var m = Afim2D.Pagina(escalaPt, deslocX - min.X * escalaPt, pagina.Height.Point - deslocY + min.Y * escalaPt);

        using var gfx = XGraphics.FromPdfPage(pagina);
        var renderizador = new RenderizadorEntidades(gfx, doc, avisos, lod);
        renderizador.Desenhar(visiveis, new ContextoRender { M = m });
        Acumular(stats, renderizador, naoSuportados);
    }

    internal static (XYZ Min, XYZ Max)? Extensao(List<Entity> entidades, XYZ? cabecalhoMin, XYZ? cabecalhoMax)
    {
        var caixas = new List<(double MinX, double MinY, double MaxX, double MaxY)>(entidades.Count);
        foreach (var e in entidades)
        {
            // a caixa que a biblioteca dá para texto é só o ponto de inserção: estima a área ocupada pelas letras
            var texto = EstimarCaixaTexto(e);

            (double, double, double, double)? caixa = null;
            try
            {
                var bb = e.GetBoundingBox();
                if (Valida(bb.Min) && Valida(bb.Max) && bb.Max.X >= bb.Min.X && bb.Max.Y >= bb.Min.Y)
                    caixa = (bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y);
            }
            catch
            {
                // entidade sem caixa calculável na biblioteca
            }

            if (texto is { } t && double.IsFinite(t.MinX) && double.IsFinite(t.MaxX) && double.IsFinite(t.MinY) && double.IsFinite(t.MaxY))
                caixa = caixa is { } c
                    ? (Math.Min(c.Item1, t.MinX), Math.Min(c.Item2, t.MinY), Math.Max(c.Item3, t.MaxX), Math.Max(c.Item4, t.MaxY))
                    : (t.MinX, t.MinY, t.MaxX, t.MaxY);

            if (caixa is { } pronta) caixas.Add(pronta);
        }

        (XYZ, XYZ)? doCabecalho = null;
        if (cabecalhoMin is { } cmin && cabecalhoMax is { } cmax && Valida(cmin) && Valida(cmax) && cmax.X > cmin.X && cmax.Y > cmin.Y)
            doCabecalho = (cmin, cmax);

        if (caixas.Count == 0) return doCabecalho;

        // Entidade perdida longe do desenho (comum: algo esquecido na origem) faria tudo virar um ponto.
        // Núcleo = percentis 2..98 dos centros; só descarta o que cair a mais de uma largura do núcleo, e só se for minoria.
        if (caixas.Count >= 20)
        {
            var cx = caixas.Select(c => (c.MinX + c.MaxX) / 2).OrderBy(v => v).ToList();
            var cy = caixas.Select(c => (c.MinY + c.MaxY) / 2).OrderBy(v => v).ToList();
            static double Percentil(List<double> v, double p) => v[(int)Math.Clamp(Math.Round(p * (v.Count - 1)), 0, v.Count - 1)];
            var x1 = Percentil(cx, 0.02); var x2 = Percentil(cx, 0.98);
            var y1 = Percentil(cy, 0.02); var y2 = Percentil(cy, 0.98);
            var fx = Math.Max(x2 - x1, 1e-9); var fy = Math.Max(y2 - y1, 1e-9);
            var aceitas = caixas.Where(c =>
            {
                var mx = (c.MinX + c.MaxX) / 2; var my = (c.MinY + c.MaxY) / 2;
                return mx >= x1 - fx && mx <= x2 + fx && my >= y1 - fy && my <= y2 + fy;
            }).ToList();
            if (aceitas.Count >= caixas.Count * 0.9) caixas = aceitas;
        }

        var minX = caixas.Min(c => c.MinX); var minY = caixas.Min(c => c.MinY);
        var maxX = caixas.Max(c => c.MaxX); var maxY = caixas.Max(c => c.MaxY);


        return (new XYZ(minX, minY, 0), new XYZ(maxX, maxY, 0));
    }

    private static bool Valida(XYZ p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X) < 1e15 && Math.Abs(p.Y) < 1e15;

    /// <summary>
    /// Retângulo aproximado ocupado por um TEXT/MTEXT, considerando rotação e alinhamento. Superestima de propósito
    /// (≈0,95 × altura por caractere; ascendentes e descendentes incluídos): cortar texto é pior que sobrar margem.
    /// </summary>
    internal static (double MinX, double MinY, double MaxX, double MaxY)? EstimarCaixaTexto(Entity e)
    {
        const double LarguraPorCaractere = 0.95;
        string conteudo; double altura, rotacao; XYZ ancora; double deslocX; double baseY; double largura;
        switch (e)
        {
            case TextEntity t when t.Height > 0:
                conteudo = LimpadorTextoCad.LimparTexto(t.Value);
                if (conteudo.Length == 0) return null;
                altura = t.Height; rotacao = t.Rotation;
                largura = conteudo.Length * altura * LarguraPorCaractere * (t.WidthFactor > 0 ? t.WidthFactor : 1);
                var usaAlinhamento = !(t.HorizontalAlignment == TextHorizontalAlignment.Left && t.VerticalAlignment == TextVerticalAlignmentType.Baseline);
                ancora = usaAlinhamento && t.HorizontalAlignment is not (TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit) ? t.AlignmentPoint : t.InsertPoint;
                deslocX = t.HorizontalAlignment switch
                {
                    TextHorizontalAlignment.Center or TextHorizontalAlignment.Middle => -largura / 2,
                    TextHorizontalAlignment.Right => -largura,
                    _ => 0,
                };
                baseY = t.VerticalAlignment switch { TextVerticalAlignmentType.Top => -altura, TextVerticalAlignmentType.Middle => -altura / 2, _ => 0 };
                if (t.HorizontalAlignment == TextHorizontalAlignment.Middle) baseY = -altura / 2;
                break;

            case MText m when m.Height > 0:
                conteudo = LimpadorTextoCad.LimparMText(m.Value);
                if (conteudo.Length == 0) return null;
                altura = m.Height; ancora = m.InsertPoint;
                rotacao = m.Rotation;
                var direcao = m.AlignmentPoint;
                if ((Math.Abs(direcao.X) > 1e-9 || Math.Abs(direcao.Y) > 1e-9) && (Math.Abs(direcao.X - 1) > 1e-9 || Math.Abs(direcao.Y) > 1e-9))
                    rotacao = Math.Atan2(direcao.Y, direcao.X);
                var linhas = conteudo.Split('\n');
                // a largura da caixa do MTEXT é só o limite de quebra: o texto ocupa o menor entre ela e a largura natural
                var larguraNatural = linhas.Max(l => l.Length) * altura * LarguraPorCaractere;
                var totalLinhas = linhas.Length;
                // largura de quebra menor que um caractere é lixo (arquivos trazem 2e-10): trata como "sem quebra"
                if (m.RectangleWidth >= altura * LarguraPorCaractere && m.RectangleWidth < larguraNatural)
                {
                    largura = m.RectangleWidth;
                    totalLinhas = Math.Min(200, linhas.Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length * altura * LarguraPorCaractere / m.RectangleWidth))));
                }
                else largura = larguraNatural;
                var total = altura + (totalLinhas - 1) * altura * 5.0 / 3.0;
                deslocX = m.AttachmentPoint switch
                {
                    AttachmentPointType.TopCenter or AttachmentPointType.MiddleCenter or AttachmentPointType.BottomCenter => -largura / 2,
                    AttachmentPointType.TopRight or AttachmentPointType.MiddleRight or AttachmentPointType.BottomRight => -largura,
                    _ => 0,
                };
                baseY = m.AttachmentPoint switch
                {
                    AttachmentPointType.TopLeft or AttachmentPointType.TopCenter or AttachmentPointType.TopRight => -total,
                    AttachmentPointType.MiddleLeft or AttachmentPointType.MiddleCenter or AttachmentPointType.MiddleRight => -total / 2,
                    _ => 0,
                };
                altura = total;
                break;

            default:
                return null;
        }

        // ascendentes/descendentes: a fonte ocupa ~1,4 × a altura das maiúsculas
        var descida = e is TextEntity ? altura * 0.3 : 0;
        var subida = e is TextEntity ? altura * 0.3 : 0;
        var c = Math.Cos(rotacao); var s = Math.Sin(rotacao);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (lx, ly) in new[] { (deslocX, baseY - descida), (deslocX + largura, baseY - descida), (deslocX, baseY + altura + subida), (deslocX + largura, baseY + altura + subida) })
        {
            var x = ancora.X + lx * c - ly * s;
            var y = ancora.Y + lx * s + ly * c;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return (minX, minY, maxX, maxY);
    }

    private static double MedianaAlturaTexto(List<Entity> entidades)
    {
        var alturas = new List<double>();
        foreach (var e in entidades)
        {
            switch (e)
            {
                case TextEntity t when t.Height > 0: alturas.Add(t.Height); break;
                case MText m when m.Height > 0: alturas.Add(m.Height); break;
                case Insert ins:
                    foreach (var a in ins.Attributes) if (a.Height > 0) alturas.Add(a.Height);
                    break;
            }
        }
        if (alturas.Count == 0) return 0;
        alturas.Sort();
        return alturas[alturas.Count / 2];
    }

    // ------------------------------------------------------------------ layouts

    private static void PaginaLayout(PdfDocument pdf, CadDocument doc, Layout layout, OpcoesDesenho opcoes, ParametrosLod lod, List<string> avisos, EstatisticasDesenho stats, Dictionary<string, int> naoSuportados)
    {
        var bloco = layout.AssociatedBlock;
        if (bloco is null) return;

        var entidades = bloco.Entities.ToList();
        var viewports = entidades.OfType<Viewport>().ToList();
        // a viewport id=1 é a "janela" do espaço do papel na tela, não a folha: nunca é desenhada nem conta como conteúdo
        var papelVp = layout.PaperViewport ?? viewports.FirstOrDefault(v => v.RepresentsPaper || v.Id == 1);
        var viewportsAtivas = viewports.Where(v => !ReferenceEquals(v, papelVp) && ViewportAtiva(v)).ToList();
        var conteudo = entidades.Where(e => !ReferenceEquals(e, papelVp) && (e is not Viewport vp || viewportsAtivas.Contains(vp))).ToList();
        if (conteudo.Count == 0) return; // layout vazio

        // unidades do espaço do papel → mm; folha declarada nas configurações de plotagem (sempre em mm)
        var unidadeMm = layout.PaperUnits == PlotPaperUnits.Inches ? 25.4 : 1.0;
        var girado = layout.PaperRotation is PlotRotation.Degrees90 or PlotRotation.Degrees270;
        var paperWmm = girado ? layout.PaperHeight : layout.PaperWidth;
        var paperHmm = girado ? layout.PaperWidth : layout.PaperHeight;
        var folhaDeclarada = paperWmm > 0 && paperHmm > 0 && paperWmm <= 6000 && paperHmm <= 6000;

        // região a enquadrar, na ordem de confiança: janela de plotagem (o que o usuário plota), extensão salva
        // do layout (calculada pelo AutoCAD), e por fim as caixas das entidades visíveis (a menos confiável:
        // MULTILEADER/WIPEOUT ilegíveis trazem caixas erradas)
        using var sonda = XGraphics.CreateMeasureContext(new XSize(100, 100), XGraphicsUnit.Point, XPageDirection.Downwards);
        var regras = new RenderizadorEntidades(sonda, doc, avisos, lod);
        var visiveis = conteudo.Where(e => e is Viewport || (!e.IsInvisible && regras.CamadaVisivel(e.Layer, null))).ToList();
        var limiteUnidades = (folhaDeclarada ? Math.Max(paperWmm, paperHmm) : 6000) / unidadeMm * 10; // região maior que 10 folhas é lixo

        (XYZ Min, XYZ Max)? regiao = null;
        var regiaoExata = false;
        if (layout.PlotType == PlotType.Window && RegiaoValida(layout.WindowLowerLeftX, layout.WindowLowerLeftY, layout.WindowUpperLeftX, layout.WindowUpperLeftY, limiteUnidades, out var janela))
        {
            regiao = janela; regiaoExata = true;
        }
        else if (RegiaoValida(layout.MinExtents.X, layout.MinExtents.Y, layout.MaxExtents.X, layout.MaxExtents.Y, limiteUnidades, out var salva))
        {
            regiao = salva; regiaoExata = true;
        }
        regiao ??= ExtensaoLayout(visiveis) ?? ExtensaoLayout(conteudo);
        if (regiao is null) return;

        var (min, max) = regiao.Value;
        var larguraConteudo = Math.Max(max.X - min.X, 1e-9);
        var alturaConteudo = Math.Max(max.Y - min.Y, 1e-9);

        if (!folhaDeclarada)
        {
            // sem folha utilizável: A1 na orientação do conteúdo
            var paisagem = larguraConteudo >= alturaConteudo;
            paperWmm = paisagem ? 841 : 594;
            paperHmm = paisagem ? 594 : 841;
            avisos.Add($"Layout '{layout.Name}': folha não declarada; usando A1.");
        }
        var larguraPt = paperWmm * PtPorMm;
        var alturaPt = paperHmm * PtPorMm;

        // "Plotar layout" em 1:1 quando o conteúdo cabe na folha; senão (extensão, janela, tela) ajusta à folha, como o AutoCAD faz com "ajustar ao papel"
        Afim2D m;
        var margem = layout.UnprintableMargin;
        var umParaUmCabe = layout.PlotType == PlotType.LayoutInformation &&
                           larguraConteudo * unidadeMm <= paperWmm * 1.01 && alturaConteudo * unidadeMm <= paperHmm * 1.01;
        if (umParaUmCabe)
        {
            var escala = unidadeMm * PtPorMm;
            var dxMm = margem.Left; var dyMm = margem.Bottom;
            // posição natural: origem do layout no canto da área imprimível; se sair da folha, centraliza
            if (min.X * unidadeMm + dxMm < -0.5 || max.X * unidadeMm + dxMm > paperWmm + 0.5 ||
                min.Y * unidadeMm + dyMm < -0.5 || max.Y * unidadeMm + dyMm > paperHmm + 0.5)
            {
                dxMm = (paperWmm - larguraConteudo * unidadeMm) / 2 - min.X * unidadeMm;
                dyMm = (paperHmm - alturaConteudo * unidadeMm) / 2 - min.Y * unidadeMm;
            }
            m = Afim2D.Pagina(escala, dxMm * PtPorMm, alturaPt - dyMm * PtPorMm);
        }
        else
        {
            // região exata (janela/extensão salva) preenche a folha; região estimada ganha uma margem
            var margemPt = regiaoExata ? 0 : Math.Max(5, Math.Min(Math.Min(margem.Left, margem.Bottom), 20)) * PtPorMm;
            var escala = Math.Min((larguraPt - 2 * margemPt) / larguraConteudo, (alturaPt - 2 * margemPt) / alturaConteudo);
            var dx = (larguraPt - larguraConteudo * escala) / 2;
            var dy = (alturaPt - alturaConteudo * escala) / 2;
            m = Afim2D.Pagina(escala, dx - min.X * escala, alturaPt - dy + min.Y * escala);
        }

        var pagina = pdf.AddPage();
        pagina.Width = XUnit.FromPoint(larguraPt);
        pagina.Height = XUnit.FromPoint(alturaPt);

        using var gfx = XGraphics.FromPdfPage(pagina);
        var renderizador = new RenderizadorEntidades(gfx, doc, avisos, lod);
        var ctxPapel = new ContextoRender { M = m };
        var modelo = doc.ModelSpace.Entities.Where(e => e is not Viewport).ToList();

        foreach (var vp in viewportsAtivas)
        {
            try
            {
                DesenharViewport(gfx, renderizador, vp, modelo, ctxPapel, avisos);
                stats.Viewports++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                avisos.Add($"Viewport do layout '{layout.Name}' não renderizada: {ex.Message}");
            }
        }

        renderizador.Desenhar(conteudo.Where(e => e is not Viewport), ctxPapel);
        Acumular(stats, renderizador, naoSuportados);
    }

    private static bool ViewportAtiva(Viewport vp)
        => vp.Width > 0 && vp.Height > 0 && vp.ViewHeight > 0 && !vp.Status.HasFlag(ViewportStatusFlags.ViewportOff);

    /// <summary>Retângulo finito, com área e não absurdo (lado ≤ <paramref name="limite"/>), em qualquer ordem de cantos.</summary>
    private static bool RegiaoValida(double x1, double y1, double x2, double y2, double limite, out (XYZ Min, XYZ Max) regiao)
    {
        regiao = default;
        if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2)) return false;
        var minX = Math.Min(x1, x2); var maxX = Math.Max(x1, x2);
        var minY = Math.Min(y1, y2); var maxY = Math.Max(y1, y2);
        var w = maxX - minX; var h = maxY - minY;
        if (w <= 1e-6 || h <= 1e-6 || w > limite || h > limite) return false;
        regiao = (new XYZ(minX, minY, 0), new XYZ(maxX, maxY, 0));
        return true;
    }

    /// <summary>Extensão do conteúdo de um layout: entidades do papel mais os retângulos das viewports ativas.</summary>
    private static (XYZ Min, XYZ Max)? ExtensaoLayout(List<Entity> conteudo)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var achou = false;

        var semViewports = conteudo.Where(e => e is not Viewport).ToList();
        if (semViewports.Count > 0 && Extensao(semViewports, null, null) is { } ext)
        {
            minX = ext.Min.X; minY = ext.Min.Y; maxX = ext.Max.X; maxY = ext.Max.Y; achou = true;
        }
        foreach (var vp in conteudo.OfType<Viewport>())
        {
            minX = Math.Min(minX, vp.Center.X - vp.Width / 2); maxX = Math.Max(maxX, vp.Center.X + vp.Width / 2);
            minY = Math.Min(minY, vp.Center.Y - vp.Height / 2); maxY = Math.Max(maxY, vp.Center.Y + vp.Height / 2);
            achou = true;
        }
        return achou ? (new XYZ(minX, minY, 0), new XYZ(maxX, maxY, 0)) : null;
    }

    private static void DesenharViewport(XGraphics gfx, RenderizadorEntidades renderizador, Viewport vp, List<Entity> modelo, ContextoRender ctxPapel, List<string> avisos)
    {
        var escala = vp.Height / vp.ViewHeight; // unidades de papel por unidade de modelo

        // modelo → papel: o sistema de exibição (DCS) tem origem no alvo da vista (ViewTarget, em WCS) e pode estar
        // girado (twist); ViewCenter é dado nesse sistema e cai no centro da viewport, com a escala altura/altura-da-vista
        var mVp = Afim2D.Translacao(vp.Center.X, vp.Center.Y)
            .Compor(Afim2D.Escala(escala, escala))
            .Compor(Afim2D.Translacao(-vp.ViewCenter.X, -vp.ViewCenter.Y))
            .Compor(Afim2D.Rotacao(vp.TwistAngle))
            .Compor(Afim2D.Translacao(-vp.ViewTarget.X, -vp.ViewTarget.Y));
        var m = ctxPapel.M.Compor(mVp);

        if (Math.Abs(vp.ViewDirection.Z) < 0.99 && (vp.ViewDirection.X != 0 || vp.ViewDirection.Y != 0 || vp.ViewDirection.Z != 0))
            avisos.Add("Viewport com vista não plana renderizada em planta.");

        // recorte: retângulo da viewport ou contorno não retangular
        List<XYZ> contorno;
        if (vp.Boundary is IPolyline poligono && vp.Status.HasFlag(ViewportStatusFlags.NonRectangularClipping))
            contorno = poligono.Vertices.Select(v => v.Location switch { XYZ p => p, XY q => new XYZ(q.X, q.Y, 0), var o => new XYZ(o[0], o[1], 0) }).ToList();
        else
            contorno = new List<XYZ>
            {
                new(vp.Center.X - vp.Width / 2, vp.Center.Y - vp.Height / 2, 0),
                new(vp.Center.X + vp.Width / 2, vp.Center.Y - vp.Height / 2, 0),
                new(vp.Center.X + vp.Width / 2, vp.Center.Y + vp.Height / 2, 0),
                new(vp.Center.X - vp.Width / 2, vp.Center.Y + vp.Height / 2, 0),
            };
        if (contorno.Count < 3) return;

        var caminho = new XGraphicsPath();
        caminho.AddPolygon(contorno.Select(p => ctxPapel.M.Aplicar(p)).ToArray());

        var congeladas = new HashSet<string>(vp.FrozenLayers.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);

        var estado = gfx.Save();
        try
        {
            gfx.IntersectClip(caminho);
            renderizador.Desenhar(modelo, new ContextoRender { M = m, CamadasCongeladasViewport = congeladas });
        }
        finally
        {
            gfx.Restore(estado);
        }

        // borda: só se a camada da viewport for visível e plotável
        if (renderizador.CamadaVisivel(vp.Layer, ctxPapel))
            renderizador.Contorno(contorno, vp, ctxPapel);
    }

    private static void Acumular(EstatisticasDesenho stats, RenderizadorEntidades r, Dictionary<string, int> naoSuportados)
    {
        stats.EntidadesDesenhadas += r.EntidadesDesenhadas;
        stats.EntidadesComErro += r.EntidadesComErro;
        foreach (var (tipo, n) in r.TiposNaoSuportados)
            naoSuportados[tipo] = naoSuportados.GetValueOrDefault(tipo) + n;
    }
}

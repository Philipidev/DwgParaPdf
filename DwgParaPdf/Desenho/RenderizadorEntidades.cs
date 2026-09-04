using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DwgParaPdf.Desenho;

/// <summary>Estado herdado enquanto se desce por INSERTs, cotas e viewports.</summary>
internal sealed class ContextoRender
{
    public required Afim2D M { get; init; }

    /// <summary>Cor, espessura e tipo de linha resolvidos do INSERT pai (para ByBlock e para entidades na camada 0).</summary>
    public XColor? CorPai { get; init; }
    public double? EspessuraPaiMm { get; init; }
    public LineType? TipoLinhaPai { get; init; }
    public double EscalaTipoLinhaPai { get; init; } = 1;

    public IReadOnlySet<string>? CamadasCongeladasViewport { get; init; }
    public int Profundidade { get; init; }
    public HashSet<string> BlocosAbertos { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public ContextoRender Filho(Afim2D m, XColor? corPai, double? espessuraPai, LineType? tipoLinhaPai, double escalaTipoLinha) => new()
    {
        M = m,
        CorPai = corPai,
        EspessuraPaiMm = espessuraPai,
        TipoLinhaPai = tipoLinhaPai,
        EscalaTipoLinhaPai = escalaTipoLinha,
        CamadasCongeladasViewport = CamadasCongeladasViewport,
        Profundidade = Profundidade + 1,
        BlocosAbertos = BlocosAbertos,
    };
}

/// <summary>
/// Desenha entidades do ACadSharp em um <see cref="XGraphics"/> (PDFsharp). Geometria vira caminhos vetoriais;
/// texto vira texto real da página (selecionável e extraível).
/// </summary>
internal sealed class RenderizadorEntidades
{
    private const double PtPorMm = 72.0 / 25.4;
    private const double EspessuraPadraoMm = 0.25;
    private const double EspessuraMinimaPt = 0.12;
    private const double AlturaCapitalArial = 0.716; // altura das maiúsculas / tamanho da fonte
    private const int LimiteLinhasHachura = 40_000;
    private const int ProfundidadeMaxima = 12;

    private readonly XGraphics _gfx;
    private readonly List<string> _avisos;
    private readonly double _ltscale;
    private readonly double _alturaTextoPadrao;
    private readonly bool _respeitarPlotFlag;

    private readonly Dictionary<(uint Cor, double Largura, string Tracos), XPen> _canetas = new();
    private readonly Dictionary<uint, XSolidBrush> _pinceis = new();
    private readonly Dictionary<(string Familia, double Tamanho), XFont> _fontes = new();
    private readonly HashSet<string> _familiasIndisponiveis = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _tiposNaoSuportados = new();

    public int EntidadesDesenhadas { get; private set; }
    public int EntidadesComErro { get; private set; }
    public IReadOnlyDictionary<string, int> TiposNaoSuportados => _tiposNaoSuportados;

    public RenderizadorEntidades(XGraphics gfx, CadDocument doc, List<string> avisos)
    {
        _gfx = gfx;
        _avisos = avisos;
        _ltscale = doc.Header?.LineTypeScale > 0 ? doc.Header.LineTypeScale : 1;
        _alturaTextoPadrao = doc.Header?.TextHeightDefault > 0 ? doc.Header.TextHeightDefault : 2.5;
        // se nenhuma camada está marcada como plotável o flag não é confiável; ignora
        _respeitarPlotFlag = doc.Layers.Any(l => l.PlotFlag);
    }

    // ------------------------------------------------------------------ percurso

    public void Desenhar(IEnumerable<Entity> entidades, ContextoRender ctx)
    {
        foreach (var entidade in entidades)
        {
            try
            {
                DesenharEntidade(entidade, ctx);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                EntidadesComErro++;
                if (_avisos.Count < 200)
                    _avisos.Add($"Entidade {entidade.ObjectName} não desenhada: {ex.Message}");
            }
        }
    }

    private void DesenharEntidade(Entity e, ContextoRender ctx)
    {
        if (e.IsInvisible) return;
        if (!CamadaVisivel(e.Layer, ctx)) return;

        switch (e)
        {
            case Viewport:
            case AttributeDefinition:
                return; // viewports são tratadas pelo paginador; ATTDEF é só definição

            case Line l:
                Tracar(new[] { l.StartPoint, l.EndPoint }, false, l, ctx);
                return;

            case LwPolyline lw:
                Polilinha(lw, lw.ConstantWidth, lw, ctx);
                return;

            case IPolyline p when e is Polyline2D or Polyline3D:
                Polilinha(p, 0, e, ctx);
                return;

            case Arc a:
                Tracar(Xyz(a.PolygonalVertexes(Segmentos(a.Radius, Math.Abs(a.Sweep), ctx))), false, a, ctx);
                return;

            case Circle c:
                Tracar(Xyz(c.PolygonalVertexes(Segmentos(c.Radius, Math.Tau, ctx))), true, c, ctx);
                return;

            case Ellipse el:
                {
                    var varredura = el.IsFullEllipse ? Math.Tau : Math.Abs(el.EndParameter - el.StartParameter);
                    var raio = Math.Sqrt(el.MajorAxisEndPoint.X * el.MajorAxisEndPoint.X + el.MajorAxisEndPoint.Y * el.MajorAxisEndPoint.Y);
                    Tracar(Xyz(el.PolygonalVertexes(Segmentos(raio, varredura, ctx))), el.IsFullEllipse, el, ctx);
                    return;
                }

            case Spline s:
                Curva(s, ctx);
                return;

            case Hatch h:
                Hachura(h, ctx);
                return;

            case Solid so:
                Preencher(new[] { so.FirstCorner, so.SecondCorner, so.FourthCorner, so.ThirdCorner }, so, ctx);
                return;

            case Face3D f:
                Tracar(new[] { f.FirstCorner, f.SecondCorner, f.ThirdCorner, f.FourthCorner }, true, f, ctx);
                return;

            case Point pt:
                {
                    var q = ctx.M.Aplicar(pt.Location);
                    if (Finito(q)) _gfx.DrawEllipse(Pincel(Cor(pt, ctx)), q.X - 0.35, q.Y - 0.35, 0.7, 0.7);
                    EntidadesDesenhadas++;
                    return;
                }

            case AttributeEntity atrib:
                Texto(atrib, ctx);
                return;

            case TextEntity t:
                Texto(t, ctx);
                return;

            case MText m:
                MTexto(m, ctx);
                return;

            case TableEntity tabela:
                Insercao(tabela, ctx); // tabela = INSERT do seu bloco gráfico
                return;

            case Insert ins:
                Insercao(ins, ctx);
                return;

            case Dimension d:
                if (d.Block is not null && ctx.Profundidade < ProfundidadeMaxima)
                    Desenhar(d.Block.Entities, ctx.Filho(ctx.M, Cor(d, ctx), EspessuraMm(d, ctx), TipoLinhaEfetivo(d, ctx), ctx.EscalaTipoLinhaPai));
                return;

            case Leader ld:
                if (ld.Vertices.Count >= 2) Tracar(ld.Vertices, false, ld, ctx);
                return;

            case MultiLeader ml:
                MultiLider(ml, ctx);
                return;

            default:
                _tiposNaoSuportados[e.ObjectName] = _tiposNaoSuportados.GetValueOrDefault(e.ObjectName) + 1;
                return;
        }
    }

    // ------------------------------------------------------------------ geometria

    private void Polilinha(IPolyline p, double larguraConstante, Entity e, ContextoRender ctx)
    {
        var vertices = p.Vertices.ToList();
        if (vertices.Count < 2) return;

        var pontos = new List<XYZ>(vertices.Count * 2);
        var n = vertices.Count;
        var arestas = p.IsClosed ? n : n - 1;
        double larguraMax = larguraConstante;

        for (var i = 0; i < arestas; i++)
        {
            var a = Ponto(vertices[i]);
            var b = Ponto(vertices[(i + 1) % n]);
            pontos.Add(a);

            if (vertices[i] is LwPolyline.Vertex vl)
                larguraMax = Math.Max(larguraMax, Math.Max(vl.StartWidth, vl.EndWidth));
            else if (vertices[i] is Vertex v3)
                larguraMax = Math.Max(larguraMax, Math.Max(v3.StartWidth, v3.EndWidth));

            var bulge = vertices[i].Bulge;
            if (Math.Abs(bulge) < 1e-9 || !(Distancia(a, b) > 1e-9)) continue; // sem arco, ou vértices coincidentes/NaN: segmento reto

            List<XYZ> seg;
            try
            {
                var arco = Arc.CreateFromBulge(new XY(a.X, a.Y), new XY(b.X, b.Y), bulge);
                if (!(arco.Radius > 0) || !double.IsFinite(arco.Radius)) continue;
                seg = Xyz(arco.PolygonalVertexes(Segmentos(arco.Radius, Math.Abs(arco.Sweep), ctx)));
            }
            catch (ArgumentException) { continue; }
            catch (ArithmeticException) { continue; }
            if (seg.Count < 2) continue;
            // o polígono do arco vai no sentido anti-horário; alinha com a→b
            if (Distancia(seg[0], a) > Distancia(seg[0], b)) seg.Reverse();
            for (var k = 1; k < seg.Count - 1; k++) pontos.Add(seg[k]);
        }
        if (!p.IsClosed) pontos.Add(Ponto(vertices[n - 1]));

        Tracar(pontos, p.IsClosed, e, ctx, larguraMax);
    }

    private void Curva(Spline s, ContextoRender ctx)
    {
        var n = Math.Clamp(Math.Max(s.ControlPoints.Count, s.FitPoints.Count) * 8, 16, 512);
        if (s.TryPolygonalVertexes(n, out var pontos) && pontos.Count >= 2)
            Tracar(pontos, s.IsClosed, s, ctx);
        else if (s.FitPoints.Count >= 2)
            Tracar(s.FitPoints, s.IsClosed, s, ctx);
        else if (s.ControlPoints.Count >= 2)
            Tracar(s.ControlPoints, s.IsClosed, s, ctx);
    }

    private void Hachura(Hatch h, ContextoRender ctx)
    {
        var cor = Cor(h, ctx);
        var caminho = new XGraphicsPath { FillMode = XFillMode.Alternate };
        var lacos = 0;
        foreach (var trecho in h.Paths)
        {
            var pontos = ParaPagina(Xyz(trecho.GetPoints(48)), ctx);
            if (pontos.Length < 3) continue;
            caminho.AddPolygon(pontos);
            lacos++;
        }
        if (lacos == 0) return;

        if (h.IsSolid)
        {
            _gfx.DrawPath(Pincel(cor), caminho);
            EntidadesDesenhadas++;
            return;
        }

        List<Entity>? linhas = null;
        try
        {
            linhas = h.ExplodePattern()?.ToList();
        }
        catch
        {
            // padrão não suportado: cai no preenchimento leve
        }

        if (linhas is { Count: > 0 } && linhas.Count <= LimiteLinhasHachura)
        {
            var estado = _gfx.Save();
            try
            {
                _gfx.IntersectClip(caminho);
                var caneta = Caneta(cor, Math.Max(EspessuraMinimaPt, EspessuraPt(h, ctx) * 0.6), null);
                foreach (var linha in linhas)
                {
                    if (linha is Line l)
                    {
                        var a = ctx.M.Aplicar(l.StartPoint);
                        var b = ctx.M.Aplicar(l.EndPoint);
                        if (Finito(a) && Finito(b)) _gfx.DrawLine(caneta, a, b);
                    }
                    else if (linha is IPolyline pl)
                    {
                        var pts = ParaPagina(pl.Vertices.Select(Ponto).ToList(), ctx);
                        if (pts.Length >= 2) _gfx.DrawLines(caneta, pts);
                    }
                }
            }
            finally
            {
                _gfx.Restore(estado);
            }
        }
        else
        {
            // muitas linhas (ou padrão desconhecido): preenchimento translúcido na cor da hachura
            _gfx.DrawPath(new XSolidBrush(XColor.FromArgb(48, cor.R, cor.G, cor.B)), caminho);
        }
        EntidadesDesenhadas++;
    }

    private void Preencher(IReadOnlyList<XYZ> mundo, Entity e, ContextoRender ctx)
    {
        var pontos = ParaPagina(mundo, ctx);
        if (pontos.Length < 3) return;
        _gfx.DrawPolygon(Pincel(Cor(e, ctx)), pontos, XFillMode.Winding);
        EntidadesDesenhadas++;
    }

    private void Tracar(IReadOnlyList<XYZ> mundo, bool fechado, Entity e, ContextoRender ctx, double larguraMundo = 0)
    {
        var pontos = ParaPagina(mundo, ctx);
        if (pontos.Length < 2) return;

        var larguraPt = larguraMundo > 0
            ? Math.Max(EspessuraMinimaPt, larguraMundo * ctx.M.EscalaMedia)
            : EspessuraPt(e, ctx);
        var caneta = Caneta(Cor(e, ctx), larguraPt, Tracos(e, ctx, larguraPt));

        if (fechado && pontos.Length >= 3) _gfx.DrawPolygon(caneta, pontos);
        else _gfx.DrawLines(caneta, pontos);
        EntidadesDesenhadas++;
    }

    /// <summary>Traça um contorno já em coordenadas do espaço (usado pelo paginador para bordas de viewport).</summary>
    public void Contorno(IReadOnlyList<XYZ> pontosEspaco, Entity dona, ContextoRender ctx) => Tracar(pontosEspaco, true, dona, ctx);

    private static XPoint[] ParaPagina(IReadOnlyList<XYZ> mundo, ContextoRender ctx)
    {
        var lista = new List<XPoint>(mundo.Count);
        foreach (var p in mundo)
        {
            var q = ctx.M.Aplicar(p);
            if (!Finito(q)) continue;
            if (lista.Count > 0)
            {
                var ult = lista[^1];
                if (Math.Abs(ult.X - q.X) < 0.02 && Math.Abs(ult.Y - q.Y) < 0.02) continue;
            }
            lista.Add(q);
        }
        return lista.ToArray();
    }

    private static int Segmentos(double raioMundo, double varredura, ContextoRender ctx)
    {
        var comprimentoPt = Math.Abs(varredura) * Math.Abs(raioMundo) * ctx.M.EscalaMedia;
        var n = (int)Math.Ceiling(comprimentoPt / 1.5);
        return Math.Clamp(n, varredura > Math.PI ? 12 : 4, 720);
    }

    // ------------------------------------------------------------------ blocos

    private void Insercao(Insert ins, ContextoRender ctx)
    {
        foreach (var atributo in ins.Attributes)
        {
            if (atributo.IsInvisible || atributo.Flags.HasFlag(AttributeFlags.Hidden)) continue;
            if (!CamadaVisivel(atributo.Layer, ctx)) continue;
            try { Texto(atributo, ctx); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { EntidadesComErro++; if (_avisos.Count < 200) _avisos.Add($"Atributo não desenhado: {ex.Message}"); }
        }

        var bloco = ins.Block;
        if (bloco is null || ctx.Profundidade >= ProfundidadeMaxima) return;
        if (!ctx.BlocosAbertos.Add(bloco.Name)) return; // ciclo

        try
        {
            var cor = Cor(ins, ctx);
            var espessura = EspessuraMm(ins, ctx);
            var tipoLinha = TipoLinhaEfetivo(ins, ctx);
            var escalaTipo = ctx.EscalaTipoLinhaPai * (ins.LineTypeScale > 0 ? ins.LineTypeScale : 1);
            var baseM = ctx.M.Compor(Afim2D.DeInsercao(ins.InsertPoint, ins.XScale, ins.YScale, ins.Rotation));

            var colunas = Math.Max(1, (int)ins.ColumnCount);
            var linhas = Math.Max(1, (int)ins.RowCount);
            var entidades = bloco.Entities.Where(e => e is not AttributeDefinition).ToList();

            for (var r = 0; r < linhas; r++)
            for (var c = 0; c < colunas; c++)
            {
                var m = (linhas > 1 || colunas > 1)
                    ? baseM.Compor(Afim2D.Translacao(c * ins.ColumnSpacing, r * ins.RowSpacing))
                    : baseM;
                Desenhar(entidades, ctx.Filho(m, cor, espessura, tipoLinha, escalaTipo));
            }
        }
        finally
        {
            ctx.BlocosAbertos.Remove(bloco.Name);
        }
    }

    private void MultiLider(MultiLeader ml, ContextoRender ctx)
    {
        var dados = ml.ContextData;
        if (dados is null) return;

        foreach (var raiz in dados.LeaderRoots)
        {
            foreach (var linha in raiz.Lines)
            {
                var pontos = new List<XYZ>(linha.Points);
                pontos.Add(raiz.ConnectionPoint);
                if (pontos.Count >= 2) Tracar(pontos, false, ml, ctx);
            }
        }

        if (!dados.HasTextContents || string.IsNullOrWhiteSpace(dados.TextLabel)) return;
        var texto = LimpadorTextoCad.LimparMText(dados.TextLabel);
        if (texto.Length == 0) return;

        var altura = dados.TextHeight > 0 ? dados.TextHeight : _alturaTextoPadrao;
        var quadro = Quadro(dados.TextLocation, dados.TextRotation, ctx);
        LinhasDeTexto(quadro, ml.TextStyle, altura, dados.LineSpacingFactor, texto.Split('\n'), AttachmentPointType.MiddleLeft, 0, Cor(ml, ctx));
    }

    // ------------------------------------------------------------------ texto

    private readonly record struct QuadroTexto(XPoint Origem, double AnguloGraus, double EscalaX, double EscalaY, bool Espelhado);

    /// <summary>Leva a âncora e os eixos do texto para a página; devolve origem, ângulo e escalas por eixo.</summary>
    private static QuadroTexto Quadro(XYZ ancora, double rotacao, ContextoRender ctx)
    {
        var o = ctx.M.Aplicar(ancora);
        var px = ctx.M.Aplicar(ancora.X + Math.Cos(rotacao), ancora.Y + Math.Sin(rotacao));
        var py = ctx.M.Aplicar(ancora.X - Math.Sin(rotacao), ancora.Y + Math.Cos(rotacao));
        var dx = px.X - o.X; var dy = px.Y - o.Y;
        var ux = py.X - o.X; var uy = py.Y - o.Y;
        var escalaX = Math.Sqrt(dx * dx + dy * dy);
        var escalaY = Math.Sqrt(ux * ux + uy * uy);
        var angulo = Math.Atan2(dy, dx) * 180 / Math.PI;
        // na página (Y para baixo) um texto normal tem "cima" apontando para -Y: produto vetorial negativo
        var espelhado = dx * uy - dy * ux > 0;
        return new QuadroTexto(o, angulo, escalaX <= 0 ? 1 : escalaX, escalaY <= 0 ? 1 : escalaY, espelhado);
    }

    private void Texto(TextEntity t, ContextoRender ctx)
    {
        var s = LimpadorTextoCad.LimparTexto(t.Value);
        if (s.Length == 0) return;

        var altura = t.Height > 0 ? t.Height : _alturaTextoPadrao;
        var alinhadoOuFit = t.HorizontalAlignment is TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit;
        var usaAlinhamento = !(t.HorizontalAlignment == TextHorizontalAlignment.Left && t.VerticalAlignment == TextVerticalAlignmentType.Baseline);
        var ancora = alinhadoOuFit || !usaAlinhamento ? t.InsertPoint : t.AlignmentPoint;

        var quadro = Quadro(ancora, t.Rotation, ctx);
        var alturaPt = altura * quadro.EscalaY;
        var fonte = Fonte(t.Style, alturaPt);
        var fatorLargura = t.WidthFactor > 0 ? t.WidthFactor : 1;
        var estiramento = fatorLargura * (quadro.EscalaX / quadro.EscalaY);
        var larguraLocal = _gfx.MeasureString(s, fonte).Width;

        double dx = 0;
        if (alinhadoOuFit)
        {
            var fim = ctx.M.Aplicar(t.AlignmentPoint);
            var alvo = Math.Sqrt(Math.Pow(fim.X - quadro.Origem.X, 2) + Math.Pow(fim.Y - quadro.Origem.Y, 2)) / (quadro.EscalaX / quadro.EscalaY);
            if (larguraLocal > 1e-6 && alvo > 1e-6)
            {
                if (t.HorizontalAlignment == TextHorizontalAlignment.Fit)
                    estiramento *= alvo / (larguraLocal * fatorLargura);
                else
                {
                    fonte = Fonte(t.Style, alturaPt * alvo / (larguraLocal * fatorLargura));
                    alturaPt *= alvo / (larguraLocal * fatorLargura);
                }
            }
        }
        else
        {
            dx = t.HorizontalAlignment switch
            {
                TextHorizontalAlignment.Center or TextHorizontalAlignment.Middle => -larguraLocal / 2,
                TextHorizontalAlignment.Right => -larguraLocal,
                _ => 0,
            };
        }

        var vertical = t.HorizontalAlignment == TextHorizontalAlignment.Middle ? TextVerticalAlignmentType.Middle : t.VerticalAlignment;
        var dy = vertical switch
        {
            TextVerticalAlignmentType.Bottom => -0.2 * alturaPt,
            TextVerticalAlignmentType.Middle => alturaPt / 2,
            TextVerticalAlignmentType.Top => alturaPt,
            _ => 0,
        };

        DesenharTextoLocal(quadro, fonte, Pincel(Cor(t, ctx)), s, dx, dy, estiramento, t.ObliqueAngle);
    }

    private void MTexto(MText m, ContextoRender ctx)
    {
        var limpo = LimpadorTextoCad.LimparMText(m.Value);
        if (limpo.Length == 0) return;

        var altura = m.Height > 0 ? m.Height : _alturaTextoPadrao;
        var rotacao = m.Rotation;
        var direcao = m.AlignmentPoint;
        if ((Math.Abs(direcao.X) > 1e-9 || Math.Abs(direcao.Y) > 1e-9) && (Math.Abs(direcao.X - 1) > 1e-9 || Math.Abs(direcao.Y) > 1e-9))
            rotacao = Math.Atan2(direcao.Y, direcao.X);

        var quadro = Quadro(m.InsertPoint, rotacao, ctx);
        LinhasDeTexto(quadro, m.Style, altura, m.LineSpacing, limpo.Split('\n'), m.AttachmentPoint, m.RectangleWidth, Cor(m, ctx));
    }

    /// <summary>Desenha um bloco de linhas (MTEXT, MLEADER) com quebra por largura e ancoragem pelo ponto de fixação.</summary>
    private void LinhasDeTexto(QuadroTexto quadro, TextStyle? estilo, double alturaMundo, double espacamento, string[] paragrafos,
        AttachmentPointType fixacao, double larguraCaixaMundo, XColor cor)
    {
        var alturaPt = alturaMundo * quadro.EscalaY;
        var fonte = Fonte(estilo, alturaPt);
        var estiramento = quadro.EscalaX / quadro.EscalaY;
        var passoLinha = alturaPt * (5.0 / 3.0) * (espacamento > 0 ? espacamento : 1);
        // largura de quebra menor que um caractere é lixo (arquivos trazem 2e-10): sem quebra
        var larguraMaxLocal = larguraCaixaMundo >= alturaMundo * 0.5 ? larguraCaixaMundo * quadro.EscalaY : 0;

        var linhas = new List<string>();
        foreach (var paragrafo in paragrafos)
        {
            if (larguraMaxLocal <= 0 || _gfx.MeasureString(paragrafo, fonte).Width <= larguraMaxLocal) { linhas.Add(paragrafo); continue; }
            var atual = string.Empty;
            foreach (var palavra in paragrafo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var tentativa = atual.Length == 0 ? palavra : atual + " " + palavra;
                if (atual.Length > 0 && _gfx.MeasureString(tentativa, fonte).Width > larguraMaxLocal)
                {
                    linhas.Add(atual);
                    atual = palavra;
                }
                else atual = tentativa;
            }
            if (atual.Length > 0) linhas.Add(atual);
        }
        if (linhas.Count == 0) return;

        var total = alturaPt + (linhas.Count - 1) * passoLinha;
        var topo = fixacao switch
        {
            AttachmentPointType.MiddleLeft or AttachmentPointType.MiddleCenter or AttachmentPointType.MiddleRight => -total / 2,
            AttachmentPointType.BottomLeft or AttachmentPointType.BottomCenter or AttachmentPointType.BottomRight => -total,
            _ => 0,
        };

        var pincel = Pincel(cor);
        for (var i = 0; i < linhas.Count; i++)
        {
            var largura = _gfx.MeasureString(linhas[i], fonte).Width;
            var dx = fixacao switch
            {
                AttachmentPointType.TopCenter or AttachmentPointType.MiddleCenter or AttachmentPointType.BottomCenter => -largura / 2,
                AttachmentPointType.TopRight or AttachmentPointType.MiddleRight or AttachmentPointType.BottomRight => -largura,
                _ => 0,
            };
            DesenharTextoLocal(quadro, fonte, pincel, linhas[i], dx, topo + alturaPt + i * passoLinha, estiramento, 0);
        }
    }

    private void DesenharTextoLocal(QuadroTexto quadro, XFont fonte, XBrush pincel, string texto, double dx, double dy, double estiramento, double obliquo)
    {
        if (!Finito(quadro.Origem)) return;
        var estado = _gfx.Save();
        try
        {
            _gfx.TranslateTransform(quadro.Origem.X, quadro.Origem.Y);
            if (Math.Abs(quadro.AnguloGraus) > 1e-6) _gfx.RotateTransform(quadro.AnguloGraus);
            if (quadro.Espelhado) _gfx.ScaleTransform(1, -1);
            if (Math.Abs(estiramento - 1) > 1e-3) _gfx.ScaleTransform(estiramento, 1);
            if (Math.Abs(obliquo) > 1e-6) _gfx.MultiplyTransform(new XMatrix(1, 0, -Math.Tan(obliquo), 1, 0, 0));
            _gfx.DrawString(texto, fonte, pincel, new XPoint(dx, dy), XStringFormats.BaseLineLeft);
            EntidadesDesenhadas++;
        }
        finally
        {
            _gfx.Restore(estado);
        }
    }

    private XFont Fonte(TextStyle? estilo, double alturaCapitalPt)
    {
        var tamanho = Math.Round(Math.Max(alturaCapitalPt / AlturaCapitalArial, 0.05), 2);
        var (familia, estiloFonte) = FamiliaDe(estilo);
        if (_familiasIndisponiveis.Contains(familia)) familia = GeradorPdf.FamiliaFonte;

        var chave = (familia + "|" + (int)estiloFonte, tamanho);
        if (_fontes.TryGetValue(chave, out var pronta)) return pronta;

        XFont fonte;
        try
        {
            fonte = new XFont(familia, tamanho, estiloFonte, new XPdfFontOptions(PdfFontEncoding.Unicode));
        }
        catch
        {
            _familiasIndisponiveis.Add(familia);
            if (_avisos.Count < 200) _avisos.Add($"Fonte '{familia}' indisponível; usando {GeradorPdf.FamiliaFonte}.");
            familia = GeradorPdf.FamiliaFonte;
            chave = (familia + "|" + (int)estiloFonte, tamanho);
            if (_fontes.TryGetValue(chave, out pronta)) return pronta;
            fonte = new XFont(familia, tamanho, estiloFonte, new XPdfFontOptions(PdfFontEncoding.Unicode));
        }
        _fontes[chave] = fonte;
        return fonte;
    }

    /// <summary>
    /// O estilo de texto do AutoCAD guarda o nome do arquivo da fonte ("calibri.ttf", "romans.shx"). Para .ttf usa o
    /// nome do arquivo como família (o resolvedor de fontes do Windows acha pelo arquivo); .shx e afins caem em Arial.
    /// </summary>
    private static (string Familia, XFontStyleEx Estilo) FamiliaDe(TextStyle? estilo)
    {
        if (estilo is null) return (GeradorPdf.FamiliaFonte, XFontStyleEx.Regular);

        var flags = estilo.TrueType;
        var negrito = flags.HasFlag(FontFlags.Bold);
        var italico = flags.HasFlag(FontFlags.Italic);
        var xEstilo = (negrito, italico) switch
        {
            (true, true) => XFontStyleEx.BoldItalic,
            (true, false) => XFontStyleEx.Bold,
            (false, true) => XFontStyleEx.Italic,
            _ => XFontStyleEx.Regular,
        };

        var arquivo = estilo.Filename;
        if (!string.IsNullOrWhiteSpace(arquivo) && arquivo.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
            return (Path.GetFileNameWithoutExtension(arquivo), xEstilo);
        return (GeradorPdf.FamiliaFonte, xEstilo);
    }

    // ------------------------------------------------------------------ estilo

    public bool CamadaVisivel(Layer? camada, ContextoRender? ctx)
    {
        if (camada is null) return true;
        if (!camada.IsOn) return false;
        if (camada.Flags.HasFlag(LayerFlags.Frozen)) return false;
        if (_respeitarPlotFlag && !camada.PlotFlag) return false;
        if (camada.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) return false;
        if (ctx?.CamadasCongeladasViewport?.Contains(camada.Name) == true) return false;
        return true;
    }

    private static XColor Cor(Entity e, ContextoRender ctx)
    {
        var c = e.Color;
        if (c.IsByBlock) return ctx.CorPai ?? XColors.Black;
        if (c.IsByLayer)
        {
            var camada = e.Layer;
            if (camada is null) return XColors.Black;
            if (ctx.Profundidade > 0 && camada.Name == "0" && ctx.CorPai.HasValue) return ctx.CorPai.Value;
            return CorDe(camada.Color);
        }
        return CorDe(c);
    }

    private static XColor CorDe(Color c)
    {
        if (c.IsByLayer || c.IsByBlock) return XColors.Black;
        if (!c.IsTrueColor && c.Index == 7) return XColors.Black; // branco/preto do AutoCAD plota preto
        byte r = c.R, g = c.G, b = c.B;
        if (r >= 250 && g >= 250 && b >= 250) return XColors.Black; // branco sobre papel branco
        return XColor.FromArgb(r, g, b);
    }

    private static double EspessuraMm(Entity e, ContextoRender ctx)
    {
        var lw = e.LineWeight;
        if (lw == LineWeightType.ByBlock) return ctx.EspessuraPaiMm ?? EspessuraPadraoMm;
        if (lw == LineWeightType.ByLayer)
        {
            var camada = e.Layer;
            if (ctx.Profundidade > 0 && camada?.Name == "0" && ctx.EspessuraPaiMm.HasValue) return ctx.EspessuraPaiMm.Value;
            var daCamada = camada?.LineWeight ?? LineWeightType.Default;
            return daCamada is LineWeightType.ByLayer or LineWeightType.ByBlock or LineWeightType.Default
                ? EspessuraPadraoMm
                : (int)daCamada / 100.0;
        }
        if (lw == LineWeightType.Default) return EspessuraPadraoMm;
        return Math.Max(0, (int)lw) / 100.0;
    }

    private static double EspessuraPt(Entity e, ContextoRender ctx) => Math.Max(EspessuraMinimaPt, EspessuraMm(e, ctx) * PtPorMm);

    private static LineType? TipoLinhaEfetivo(Entity e, ContextoRender ctx)
    {
        var lt = e.LineType;
        if (lt is null || lt.Name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
        {
            var camada = e.Layer;
            if (ctx.Profundidade > 0 && camada?.Name == "0" && ctx.TipoLinhaPai is not null) return ctx.TipoLinhaPai;
            return camada?.LineType;
        }
        if (lt.Name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) return ctx.TipoLinhaPai;
        return lt;
    }

    /// <summary>Padrão de traços (em múltiplos da largura da caneta) ou null para linha contínua.</summary>
    private double[]? Tracos(Entity e, ContextoRender ctx, double larguraPt)
    {
        var lt = TipoLinhaEfetivo(e, ctx);
        if (lt is null || !lt.Segments.Any() || lt.Name.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return null;

        var fator = _ltscale * (e.LineTypeScale > 0 ? e.LineTypeScale : 1) * ctx.EscalaTipoLinhaPai * ctx.M.EscalaMedia;
        var padrao = new List<double>();
        var ultimoEraTraco = false;
        foreach (var seg in lt.Segments)
        {
            var eTraco = seg.Length >= 0; // positivo traço, zero ponto, negativo espaço
            var comprimento = seg.Length == 0 ? larguraPt : Math.Abs(seg.Length) * fator;
            if (padrao.Count == 0 && !eTraco) padrao.Add(0.01); // PDF exige começar por traço
            if (padrao.Count > 0 && eTraco == ultimoEraTraco) padrao[^1] += comprimento;
            else padrao.Add(comprimento);
            ultimoEraTraco = eTraco;
        }
        if (padrao.Count % 2 == 1) padrao.Add(padrao[0] * 0.5);
        if (padrao.Sum() < 2.0) return null; // denso demais para ver e caro para o PDF: contínuo

        return padrao.Select(v => Math.Max(v / larguraPt, 0.01)).ToArray();
    }

    private XPen Caneta(XColor cor, double larguraPt, double[]? tracos)
    {
        var chaveTracos = tracos is null ? string.Empty : string.Join(",", tracos.Select(v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        var chave = (Rgb(cor), Math.Round(larguraPt, 3), chaveTracos);
        if (_canetas.TryGetValue(chave, out var pronta)) return pronta;

        var caneta = new XPen(cor, larguraPt) { LineJoin = XLineJoin.Round, LineCap = XLineCap.Round };
        if (tracos is not null)
        {
            caneta.LineCap = XLineCap.Flat;
            caneta.DashPattern = tracos;
        }
        _canetas[chave] = caneta;
        return caneta;
    }

    private XSolidBrush Pincel(XColor cor)
    {
        var chave = Rgb(cor);
        if (!_pinceis.TryGetValue(chave, out var pincel))
            _pinceis[chave] = pincel = new XSolidBrush(cor);
        return pincel;
    }

    // ------------------------------------------------------------------ utilitários

    private static uint Rgb(XColor c) => ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static bool Finito(XPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X) < 1e7 && Math.Abs(p.Y) < 1e7;

    private static double Distancia(XYZ a, XYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static XYZ Ponto(IVertex v) => v.Location switch
    {
        XYZ p => p,
        XY q => new XYZ(q.X, q.Y, 0),
        var outro => new XYZ(outro[0], outro[1], 0),
    };

    private static List<XYZ> Xyz(IEnumerable<XY> pontos) => pontos.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
    private static List<XYZ> Xyz(IEnumerable<XYZ> pontos) => pontos as List<XYZ> ?? pontos.ToList();
}

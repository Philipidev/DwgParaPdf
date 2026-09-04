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
    private const int ProfundidadeMaxima = 12;

    private readonly XGraphics _gfx;
    private readonly ParametrosLod _lod;
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

    public RenderizadorEntidades(XGraphics gfx, CadDocument doc, List<string> avisos, ParametrosLod? lod = null)
    {
        _gfx = gfx;
        _lod = lod ?? ParametrosLod.De(NivelDetalhe.Alto);
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

            case Wipeout mascara:
                Mascara(mascara, ctx);
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

        // Largura muito maior que a própria geometria é leitura corrompida (visto: "donut" de 0,4 unidades com largura 75,
        // que virava um disco de 20 cm na prancha). Um donut legítimo tem largura ≈ diâmetro; limita a 2× a extensão.
        if (larguraMax > 0)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var q in pontos) { minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y); }
            var extensao = Math.Max(maxX - minX, maxY - minY);
            if (double.IsFinite(extensao) && larguraMax > 2 * extensao) larguraMax = 2 * extensao;
        }

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

        if (linhas is { Count: > 0 } && linhas.Count <= _lod.LimiteLinhasHachura)
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

    /// <summary>
    /// WIPEOUT: polígono branco que esconde o que foi desenhado antes dele (mesma ordem de desenho do arquivo).
    /// Os vértices vêm em coordenadas de imagem: origem no canto superior esquerdo, Y para baixo, deslocados de −0,5;
    /// o mundo é InsertPoint + px·U + (altura − py)·V, com U/V os vetores de um pixel e a imagem de 1×1 pixel.
    /// </summary>
    private void Mascara(CadWipeoutBase w, ContextoRender ctx)
    {
        var vertices = w.ClipBoundaryVertices;
        if (vertices is null || vertices.Count < 2) return;

        var alturaPx = w.Size.Y > 0 ? w.Size.Y : 1;
        IEnumerable<XY> imagem = vertices;
        if (vertices.Count == 2)
        {
            var a = vertices[0]; var b = vertices[1];
            imagem = new[] { a, new XY(b.X, a.Y), b, new XY(a.X, b.Y) };
        }

        var mundo = imagem.Select(p =>
        {
            var px = p.X + 0.5;
            var py = p.Y + 0.5;
            return new XYZ(
                w.InsertPoint.X + px * w.UVector.X + (alturaPx - py) * w.VVector.X,
                w.InsertPoint.Y + px * w.UVector.Y + (alturaPx - py) * w.VVector.Y,
                0);
        }).ToList();

        var pontos = ParaPagina(mundo, ctx);
        if (pontos.Length < 3) return;
        _gfx.DrawPolygon(Pincel(XColors.White), pontos, XFillMode.Winding);
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
        if (_lod.MinimoEntidadePt > 0 && pontos.Length <= 64 && ExtensaoMenorQue(pontos, _lod.MinimoEntidadePt)) return; // invisível nesse nível

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

    /// <summary>Leva pontos do mundo à página, remove coincidentes, simplifica (Douglas-Peucker) e arredonda conforme o nível de detalhe.</summary>
    private XPoint[] ParaPagina(IReadOnlyList<XYZ> mundo, ContextoRender ctx)
    {
        var lista = new List<XPoint>(mundo.Count);
        var minimo = Math.Max(0.02, _lod.ToleranciaPt * 0.5);
        foreach (var p in mundo)
        {
            var q = ctx.M.Aplicar(p);
            if (!Finito(q)) continue;
            if (lista.Count > 0)
            {
                var ult = lista[^1];
                if (Math.Abs(ult.X - q.X) < minimo && Math.Abs(ult.Y - q.Y) < minimo) continue;
            }
            lista.Add(q);
        }

        if (_lod.ToleranciaPt > 0.05 && lista.Count > 2)
            lista = Simplificar(lista, _lod.ToleranciaPt);

        if (_lod.ArredondamentoPt > 0)
        {
            var f = 1 / _lod.ArredondamentoPt;
            for (var i = 0; i < lista.Count; i++)
                lista[i] = new XPoint(Math.Round(lista[i].X * f) / f, Math.Round(lista[i].Y * f) / f);
        }
        return lista.ToArray();
    }

    /// <summary>Douglas-Peucker iterativo: mantém os vértices que se afastam mais que a tolerância da corda.</summary>
    private static List<XPoint> Simplificar(List<XPoint> pontos, double tolerancia)
    {
        var manter = new bool[pontos.Count];
        manter[0] = manter[^1] = true;
        var pilha = new Stack<(int Ini, int Fim)>();
        pilha.Push((0, pontos.Count - 1));
        var tol2 = tolerancia * tolerancia;

        while (pilha.Count > 0)
        {
            var (ini, fim) = pilha.Pop();
            if (fim - ini < 2) continue;
            var a = pontos[ini]; var b = pontos[fim];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var len2 = dx * dx + dy * dy;
            var maxDist2 = -1.0; var indice = -1;
            for (var i = ini + 1; i < fim; i++)
            {
                var p = pontos[i];
                double d2;
                if (len2 < 1e-12) { var ex = p.X - a.X; var ey = p.Y - a.Y; d2 = ex * ex + ey * ey; }
                else { var cruz = (p.X - a.X) * dy - (p.Y - a.Y) * dx; d2 = cruz * cruz / len2; }
                if (d2 > maxDist2) { maxDist2 = d2; indice = i; }
            }
            if (maxDist2 > tol2 && indice > 0)
            {
                manter[indice] = true;
                pilha.Push((ini, indice));
                pilha.Push((indice, fim));
            }
        }

        var saida = new List<XPoint>(pontos.Count);
        for (var i = 0; i < pontos.Count; i++) if (manter[i]) saida.Add(pontos[i]);
        return saida;
    }

    private static bool ExtensaoMenorQue(XPoint[] pontos, double limite)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pontos) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        return maxX - minX < limite && maxY - minY < limite;
    }

    private int Segmentos(double raioMundo, double varredura, ContextoRender ctx)
    {
        var comprimentoPt = Math.Abs(varredura) * Math.Abs(raioMundo) * ctx.M.EscalaMedia;
        var n = (int)Math.Ceiling(comprimentoPt / _lod.PassoArcoPt);
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
        LinhasDeTexto(quadro, ml.TextStyle, altura, dados.LineSpacingFactor, texto.Split('\n'), AttachmentPointType.MiddleLeft, 0, CorTexto(ml, ctx));
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

        DesenharTextoLocal(quadro, fonte, Pincel(CorTexto(t, ctx)), s, dx, dy, estiramento, t.ObliqueAngle);
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
        LinhasDeTexto(quadro, m.Style, altura, m.LineSpacing, limpo.Split('\n'), m.AttachmentPoint, m.RectangleWidth, CorTexto(m, ctx));
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

        // A fonte substituta nunca tem exatamente a largura da original; uma linha que estoura a caixa em até 15 %
        // era uma linha só no AutoCAD: comprime-a horizontalmente em vez de quebrar (senão cada linha vira duas).
        const double Folga = 1.15;
        var linhas = new List<(string Texto, double Compressao)>();
        foreach (var paragrafo in paragrafos)
        {
            var larguraParagrafo = _gfx.MeasureString(paragrafo, fonte).Width;
            if (larguraMaxLocal <= 0 || larguraParagrafo <= larguraMaxLocal) { linhas.Add((paragrafo, 1)); continue; }
            if (larguraParagrafo <= larguraMaxLocal * Folga) { linhas.Add((paragrafo, larguraMaxLocal / larguraParagrafo)); continue; }

            var atual = string.Empty;
            foreach (var palavra in paragrafo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var tentativa = atual.Length == 0 ? palavra : atual + " " + palavra;
                if (atual.Length > 0 && _gfx.MeasureString(tentativa, fonte).Width > larguraMaxLocal * Folga)
                {
                    var w = _gfx.MeasureString(atual, fonte).Width;
                    linhas.Add((atual, w > larguraMaxLocal ? larguraMaxLocal / w : 1));
                    atual = palavra;
                }
                else atual = tentativa;
            }
            if (atual.Length > 0)
            {
                var w = _gfx.MeasureString(atual, fonte).Width;
                linhas.Add((atual, w > larguraMaxLocal ? larguraMaxLocal / w : 1));
            }
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
            var (texto, compressao) = linhas[i];
            var largura = _gfx.MeasureString(texto, fonte).Width * compressao;
            // dx é medido no espaço local antes do estiramento: divide pela compressão da linha
            var dx = fixacao switch
            {
                AttachmentPointType.TopCenter or AttachmentPointType.MiddleCenter or AttachmentPointType.BottomCenter => -largura / 2 / compressao,
                AttachmentPointType.TopRight or AttachmentPointType.MiddleRight or AttachmentPointType.BottomRight => -largura / compressao,
                _ => 0,
            };
            DesenharTextoLocal(quadro, fonte, pincel, texto, dx, topo + alturaPt + i * passoLinha, estiramento * compressao, 0);
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
            if (_avisos.Count < 200 && familia != FamiliaFonteEstreita) _avisos.Add($"Fonte '{familia}' indisponível; usando {GeradorPdf.FamiliaFonte}.");
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

        // fontes SHX (simplex, romans, txt, isocp...) são de traço simples e estreitas: Arial faria o MTEXT quebrar em
        // mais linhas e invadir o que está abaixo. Arial Narrow tem largura de caractere próxima; cai em Arial se faltar.
        return (FamiliaFonteEstreita, xEstilo);
    }

    private const string FamiliaFonteEstreita = "Arial Narrow";

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

    /// <summary>Cor de geometria (traços e preenchimentos), já tratada pelo modo de cores.</summary>
    private XColor Cor(Entity e, ContextoRender ctx) => _lod.Cores switch
    {
        ModoCores.Mono => XColors.Black,
        ModoCores.Tudo => Contrastar(CorBruta(e, ctx)),
        _ => CorBruta(e, ctx),
    };

    /// <summary>Cor de texto: escurecida quando clara demais para o papel branco (modos texto e tudo), preta em mono.</summary>
    private XColor CorTexto(Entity e, ContextoRender ctx) => _lod.Cores switch
    {
        ModoCores.Mono or ModoCores.TextoPreto => XColors.Black,
        ModoCores.Original => CorBruta(e, ctx),
        _ => Contrastar(CorBruta(e, ctx)),
    };

    private static XColor CorBruta(Entity e, ContextoRender ctx)
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

    /// <summary>
    /// Escurece cores claras demais para papel branco mantendo o matiz: amarelo vira oliva, ciano vira petróleo,
    /// cinza claro vira cinza escuro. Cores já escuras (vermelho, azul, magenta, preto) não mudam.
    /// </summary>
    internal static XColor Contrastar(XColor cor)
    {
        double r = cor.R / 255.0, g = cor.G / 255.0, b = cor.B / 255.0;
        var luminancia = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        if (luminancia <= 0.45) return cor;

        // HSL: baixa a luminosidade para 0,28 preservando matiz e saturação
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        double h = 0, s = 0;
        if (d > 1e-9)
        {
            s = d / (1 - Math.Abs(2 * l - 1));
            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
            else if (max == g) h = ((b - r) / d + 2) / 6;
            else h = ((r - g) / d + 4) / 6;
        }
        const double novoL = 0.28;
        var c2 = (1 - Math.Abs(2 * novoL - 1)) * s;
        var x = c2 * (1 - Math.Abs(h * 6 % 2 - 1));
        var m = novoL - c2 / 2;
        var (r1, g1, b1) = (h * 6) switch
        {
            < 1 => (c2, x, 0.0),
            < 2 => (x, c2, 0.0),
            < 3 => (0.0, c2, x),
            < 4 => (0.0, x, c2),
            < 5 => (x, 0.0, c2),
            _ => (c2, 0.0, x),
        };
        return XColor.FromArgb((int)Math.Round((r1 + m) * 255), (int)Math.Round((g1 + m) * 255), (int)Math.Round((b1 + m) * 255));
    }

    private static XColor CorDe(Color c)
    {
        if (c.IsByLayer || c.IsByBlock) return XColors.Black;
        // Só a cor índice 7 ("branco/preto") plota preto no AutoCAD. Branco true color e índice 255 plotam brancos
        // mesmo: são as máscaras de hachura sólida que escondem o que está atrás e têm de continuar invisíveis.
        if (!c.IsTrueColor && c.Index == 7) return XColors.Black;
        return XColor.FromArgb(c.R, c.G, c.B);
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

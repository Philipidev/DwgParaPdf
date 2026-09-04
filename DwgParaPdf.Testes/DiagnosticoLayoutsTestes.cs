using System.Globalization;
using System.Text;
using ACadSharp.Entities;
using Xunit;

namespace DwgParaPdf.Testes;

/// <summary>
/// Ferramenta de apoio (não é teste de regressão): despeja as configurações de papel dos layouts de um DWG
/// para depurar dimensionamento de página. Ativa com a variável DWGPARAPDF_DIAG=&lt;caminho do .dwg&gt;;
/// escreve &lt;dwg&gt;.layouts.txt ao lado do arquivo indicado em DWGPARAPDF_DIAG_SAIDA (ou do próprio DWG).
/// </summary>
public class DiagnosticoLayoutsTestes
{
    [Fact]
    public void Despeja_configuracao_dos_layouts_quando_solicitado()
    {
        var entrada = Environment.GetEnvironmentVariable("DWGPARAPDF_DIAG");
        if (string.IsNullOrWhiteSpace(entrada)) return;

        // pasta: varredura rápida de todos os DWG, só a contagem de larguras suspeitas por arquivo
        if (Directory.Exists(entrada))
        {
            var resumo = new StringBuilder();
            foreach (var arquivo in Directory.EnumerateFiles(entrada, "*.dwg").OrderBy(a => a))
            {
                try
                {
                    var d = LeitorCad.Ler(File.ReadAllBytes(arquivo), Path.GetFileName(arquivo), new List<string>());
                    resumo.AppendLine($"{Path.GetFileName(arquivo)}\tLARGURAS_SUSPEITAS={ContarLargurasSuspeitas(d)}\tMASCARAS_BRANCAS={ContarMascarasBrancas(d)}");
                }
                catch (Exception ex) { resumo.AppendLine($"{Path.GetFileName(arquivo)}\tERRO {ex.Message}"); }
            }
            var destinoPasta = Environment.GetEnvironmentVariable("DWGPARAPDF_DIAG_SAIDA") ?? Path.Combine(entrada, "_larguras.txt");
            File.WriteAllText(destinoPasta, resumo.ToString(), new UTF8Encoding(false));
            return;
        }

        if (!File.Exists(entrada)) return;

        var avisos = new List<string>();
        var doc = LeitorCad.Ler(File.ReadAllBytes(entrada), Path.GetFileName(entrada), avisos);
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;

        foreach (var layout in doc.Layouts)
        {
            sb.AppendLine($"Layout '{layout.Name}' papel={layout.IsPaperSpace} tab={layout.TabOrder} units={layout.PaperUnits} rot={layout.PaperRotation} " +
                          $"paperW={layout.PaperWidth.ToString(ci)} paperH={layout.PaperHeight.ToString(ci)} margens=({layout.UnprintableMargin.Left.ToString(ci)},{layout.UnprintableMargin.Bottom.ToString(ci)},{layout.UnprintableMargin.Right.ToString(ci)},{layout.UnprintableMargin.Top.ToString(ci)}) " +
                          $"plotOrigem=({layout.PlotOriginX.ToString(ci)},{layout.PlotOriginY.ToString(ci)}) tipo={layout.PlotType} escala={layout.NumeratorScale.ToString(ci)}/{layout.DenominatorScale.ToString(ci)} std={layout.StandardScale} minExt={layout.MinExtents} maxExt={layout.MaxExtents} minLim={layout.MinLimits} maxLim={layout.MaxLimits}");
            var pv = layout.PaperViewport;
            sb.AppendLine($"   PaperViewport: {(pv is null ? "null" : $"id={pv.Id} papel={pv.RepresentsPaper} centro={pv.Center} W={pv.Width.ToString(ci)} H={pv.Height.ToString(ci)} viewCentro={pv.ViewCenter} viewH={pv.ViewHeight.ToString(ci)}")}");
            if (layout.AssociatedBlock is null) continue;
            foreach (var vp in layout.AssociatedBlock.Entities.OfType<Viewport>())
                sb.AppendLine($"   Viewport id={vp.Id} papel={vp.RepresentsPaper} centro={vp.Center} W={vp.Width.ToString(ci)} H={vp.Height.ToString(ci)} viewCentro={vp.ViewCenter} viewH={vp.ViewHeight.ToString(ci)} alvo={vp.ViewTarget} dir={vp.ViewDirection} twist={vp.TwistAngle.ToString(ci)} congeladas={vp.FrozenLayers.Count} status={vp.Status} camada={vp.Layer?.Name} ligada={vp.Layer?.IsOn}");
            sb.AppendLine($"   PlotWindow: ({layout.WindowLowerLeftX.ToString(ci)},{layout.WindowLowerLeftY.ToString(ci)}) - ({layout.WindowUpperLeftX.ToString(ci)},{layout.WindowUpperLeftY.ToString(ci)}) escalaImpressao={layout.PrintScale.ToString(ci)} ajustar={layout.ScaledFit}");
            var outros = layout.AssociatedBlock.Entities.Where(e => e is not Viewport).ToList();
            sb.AppendLine($"   Outras entidades: {outros.Count} ({string.Join(", ", outros.GroupBy(e => e.ObjectName).Select(g => $"{g.Key} x{g.Count()}"))})");
            foreach (var m in outros.OfType<MText>().OrderByDescending(m => m.Value.Length).Take(4))
            {
                var limpo = LimpadorTextoCad.LimparMText(m.Value);
                sb.AppendLine($"   MTEXT h={m.Height.ToString(ci)} larg={m.RectangleWidth.ToString(ci)} altCaixa={m.RectangleHeight.ToString(ci)} espac={m.LineSpacing.ToString(ci)} estiloEspac={m.LineSpacingStyle} fix={m.AttachmentPoint} colunas={m.HasColumns} estilo={m.Style?.Name}/{m.Style?.Filename} chars={m.Value.Length} linhasP={limpo.Count(c => c == '\n') + 1} inicio='{limpo.Replace('\n', '/')[..Math.Min(80, limpo.Length)]}'");
            }
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var e in outros)
            {
                try { var bb = e.GetBoundingBox(); minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y); maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y); } catch { }
            }
            if (outros.Count > 0) sb.AppendLine($"   Extensão das entidades: ({minX.ToString(ci)},{minY.ToString(ci)}) - ({maxX.ToString(ci)},{maxY.ToString(ci)})");
        }

        sb.AppendLine("Estilos de texto: " + string.Join("; ", doc.TextStyles.Select(t => $"{t.Name}={t.Filename}{(t.BigFontFilename is { Length: > 0 } b ? "+" + b : "")} w={t.Width.ToString(ci)} flags={t.TrueType}")));

        // maiores caixas estimadas de texto no modelo (para depurar extensão inflada)
        var estimadas = new List<(double Area, string Descricao)>();
        foreach (var e in doc.ModelSpace.Entities)
        {
            var caixa = DwgParaPdf.Desenho.GeradorPdfDesenho.EstimarCaixaTexto(e);
            if (caixa is not { } c) continue;
            var area = (c.MaxX - c.MinX) * (c.MaxY - c.MinY);
            var detalhe = e switch
            {
                MText m => $"MTEXT h={m.Height.ToString(ci)} larg={m.RectangleWidth.ToString(ci)} rot={m.Rotation.ToString(ci)} fix={m.AttachmentPoint} dir={m.AlignmentPoint} pos={m.InsertPoint} '{LimpadorTextoCad.LimparMText(m.Value).Replace('\n', '/')}'",
                TextEntity t => $"TEXT h={t.Height.ToString(ci)} wf={t.WidthFactor.ToString(ci)} rot={t.Rotation.ToString(ci)} ha={t.HorizontalAlignment} va={t.VerticalAlignment} pos={t.InsertPoint} alin={t.AlignmentPoint} '{LimpadorTextoCad.LimparTexto(t.Value)}'",
                _ => e.ObjectName,
            };
            estimadas.Add((area, $"caixa=({c.MinX.ToString("0.#", ci)},{c.MinY.ToString("0.#", ci)})-({c.MaxX.ToString("0.#", ci)},{c.MaxY.ToString("0.#", ci)}) {detalhe}"));
        }
        sb.AppendLine("Maiores caixas de texto estimadas no modelo:");
        foreach (var (_, d) in estimadas.OrderByDescending(x => x.Area).Take(12)) sb.AppendLine("   " + d);

        var modelo = doc.ModelSpace.Entities.Where(e => e is not Viewport).ToList();
        var ext = DwgParaPdf.Desenho.GeradorPdfDesenho.Extensao(modelo, doc.Header?.ModelSpaceExtMin, doc.Header?.ModelSpaceExtMax);
        sb.AppendLine($"Extensão do modelo calculada: {(ext is { } x ? $"({x.Min.X.ToString(ci)},{x.Min.Y.ToString(ci)}) - ({x.Max.X.ToString(ci)},{x.Max.Y.ToString(ci)})" : "null")}; cabeçalho: {doc.Header?.ModelSpaceExtMin} - {doc.Header?.ModelSpaceExtMax}");
        foreach (var t in modelo.OfType<TextEntity>().Where(t => t.Height > 100))
        {
            string caixaLib;
            try { var bb = t.GetBoundingBox(); caixaLib = $"{bb.Min} - {bb.Max}"; } catch (Exception ex) { caixaLib = "erro: " + ex.GetType().Name; }
            sb.AppendLine($"Texto grande '{LimpadorTextoCad.LimparTexto(t.Value)}' h={t.Height.ToString(ci)} pos={t.InsertPoint} estilo={t.Style?.Name} arquivo={t.Style?.Filename} wf={t.WidthFactor.ToString(ci)} caixaLib={caixaLib} camada={t.Layer?.Name}");
        }

        // hachuras cujo contorno poligonalizado é muito maior que a caixa da própria entidade (arestas de arco/elipse
        // mal interpretadas viram discos gigantes no PDF)
        sb.AppendLine("Hachuras suspeitas (contorno >> caixa da entidade):");
        IEnumerable<(string Espaco, Entity E)> Todas()
        {
            foreach (var e in doc.ModelSpace.Entities) yield return ("Model", e);
            foreach (var l in doc.Layouts.Where(l => l.IsPaperSpace && l.AssociatedBlock is not null))
                foreach (var e in l.AssociatedBlock!.Entities) yield return (l.Name, e);
            foreach (var b in doc.BlockRecords)
                foreach (var e in b.Entities) yield return ("bloco " + b.Name, e);
        }
        foreach (var (espaco, e) in Todas())
        {
            if (e is not Hatch h) continue;
            double exMin = double.MaxValue, eyMin = double.MaxValue, exMax = double.MinValue, eyMax = double.MinValue;
            try { var bb = h.GetBoundingBox(); exMin = bb.Min.X; eyMin = bb.Min.Y; exMax = bb.Max.X; eyMax = bb.Max.Y; } catch { }
            var extEnt = Math.Max(exMax - exMin, eyMax - eyMin);
            var indice = 0;
            foreach (var path in h.Paths)
            {
                indice++;
                List<string> tipos = path.Edges.Select(ed => ed.GetType().Name).ToList();
                double pxMin = double.MaxValue, pyMin = double.MaxValue, pxMax = double.MinValue, pyMax = double.MinValue;
                int n = 0;
                try
                {
                    foreach (var p in path.GetPoints(48)) { n++; pxMin = Math.Min(pxMin, p.X); pyMin = Math.Min(pyMin, p.Y); pxMax = Math.Max(pxMax, p.X); pyMax = Math.Max(pyMax, p.Y); }
                }
                catch (Exception ex) { sb.AppendLine($"   [{espaco}] hatch {h.Handle} caminho {indice}: GetPoints lançou {ex.GetType().Name}"); continue; }
                var extPath = Math.Max(pxMax - pxMin, pyMax - pyMin);
                var curvo = tipos.Any(t => t is "Arc" or "Ellipse" or "Spline");
                if (n > 0 && (double.IsNaN(extPath) || extEnt <= 0 || extPath > extEnt * 3 || (curvo && extPath > 20)))
                {
                    sb.AppendLine($"   [{espaco}] hatch {h.Handle} camada={h.Layer?.Name} solida={h.IsSolid} padrao={h.Pattern?.Name} caminho {indice}: polilinha={path.IsPolyline} arestas=[{string.Join(",", tipos)}] pontos={n} extCaminho={extPath.ToString("0.###", ci)} extEntidade={extEnt.ToString("0.###", ci)} caixaEnt=({exMin.ToString("0.#", ci)},{eyMin.ToString("0.#", ci)})-({exMax.ToString("0.#", ci)},{eyMax.ToString("0.#", ci)}) caixaCaminho=({pxMin.ToString("0.#", ci)},{pyMin.ToString("0.#", ci)})-({pxMax.ToString("0.#", ci)},{pyMax.ToString("0.#", ci)})");
                    foreach (var ed in path.Edges)
                    {
                        switch (ed)
                        {
                            case Hatch.BoundaryPath.Arc a: sb.AppendLine($"        Arc centro={a.Center} raio={a.Radius.ToString(ci)} ini={a.StartAngle.ToString(ci)} fim={a.EndAngle.ToString(ci)} ccw={a.CounterClockWise}"); break;
                            case Hatch.BoundaryPath.Ellipse el: sb.AppendLine($"        Ellipse centro={el.Center} eixoMaior={el.MajorAxisEndPoint} razao={el.MinorAxis.ToString(ci)} ini={el.StartAngle.ToString(ci)} fim={el.EndAngle.ToString(ci)} ccw={el.CounterClockWise}"); break;
                            case Hatch.BoundaryPath.Spline sp: sb.AppendLine($"        Spline grau={sp.Degree} ctrl={sp.ControlPoints.Count} fit={sp.FitPoints.Count} nos={sp.Knots.Count} racional={sp.IsRational}"); break;
                            case Hatch.BoundaryPath.Line ln: sb.AppendLine($"        Line {ln.Start} -> {ln.End}"); break;
                        }
                    }
                }
            }
        }

        // polilinhas com largura: uma largura absurda vira um borrão redondo (caneta grossa com pontas arredondadas)
        sb.AppendLine("Polilinhas mais largas:");
        var largas = new List<(double Largura, string Desc)>();
        foreach (var (espaco, e) in Todas())
        {
            double largura = 0; string tipo = e.ObjectName; int nv = 0;
            switch (e)
            {
                case LwPolyline lw:
                    nv = lw.Vertices.Count;
                    largura = Math.Max(lw.ConstantWidth, lw.Vertices.Count == 0 ? 0 : lw.Vertices.Max(v => Math.Max(v.StartWidth, v.EndWidth)));
                    break;
                case Polyline2D p2:
                    nv = p2.Vertices.Count();
                    largura = p2.Vertices.OfType<Vertex>().Select(v => Math.Max(v.StartWidth, v.EndWidth)).DefaultIfEmpty(0).Max();
                    break;
                case Polyline3D p3:
                    nv = p3.Vertices.Count();
                    largura = p3.Vertices.OfType<Vertex>().Select(v => Math.Max(v.StartWidth, v.EndWidth)).DefaultIfEmpty(0).Max();
                    break;
                default: continue;
            }
            if (largura <= 0) continue;
            string caixa = "?";
            try { var bb = e.GetBoundingBox(); caixa = $"({bb.Min.X.ToString("0.#", ci)},{bb.Min.Y.ToString("0.#", ci)})-({bb.Max.X.ToString("0.#", ci)},{bb.Max.Y.ToString("0.#", ci)})"; } catch { }
            largas.Add((largura, $"   [{espaco}] {tipo} {e.Handle} camada={e.Layer?.Name} cor={e.Color} largura={largura.ToString("0.###", ci)} vertices={nv} caixa={caixa}"));
        }
        foreach (var (_, d) in largas.OrderByDescending(x => x.Largura).Take(12)) sb.AppendLine(d);

        sb.AppendLine($"LARGURAS_SUSPEITAS={ContarLargurasSuspeitas(doc)}");

        var saida = Environment.GetEnvironmentVariable("DWGPARAPDF_DIAG_SAIDA");
        var destino = string.IsNullOrWhiteSpace(saida) ? Path.ChangeExtension(entrada, ".layouts.txt") : saida;
        File.WriteAllText(destino, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Hachuras sólidas e SOLIDs cuja cor efetiva é branco true color ou índice 255 (máscaras que devem ficar invisíveis).</summary>
    private static int ContarMascarasBrancas(ACadSharp.CadDocument doc)
    {
        IEnumerable<Entity> todas = doc.ModelSpace.Entities
            .Concat(doc.Layouts.Where(l => l.IsPaperSpace && l.AssociatedBlock is not null).SelectMany(l => l.AssociatedBlock!.Entities))
            .Concat(doc.BlockRecords.SelectMany(b => b.Entities));
        var n = 0;
        foreach (var e in todas)
        {
            if (e is not (Hatch { IsSolid: true } or Solid)) continue;
            var cor = e.Color.IsByLayer ? e.Layer?.Color ?? e.Color : e.Color;
            var branca = cor.IsTrueColor ? cor.R >= 250 && cor.G >= 250 && cor.B >= 250 : cor.Index == 255;
            if (branca) n++;
        }
        return n;
    }

    /// <summary>Polilinhas com largura maior que 2× a própria extensão (leitura corrompida que vira um borrão no PDF).</summary>
    private static int ContarLargurasSuspeitas(ACadSharp.CadDocument doc)
    {
        IEnumerable<Entity> todas = doc.ModelSpace.Entities
            .Concat(doc.Layouts.Where(l => l.IsPaperSpace && l.AssociatedBlock is not null).SelectMany(l => l.AssociatedBlock!.Entities))
            .Concat(doc.BlockRecords.SelectMany(b => b.Entities));
        var suspeitas = 0;
        foreach (var e in todas)
        {
            if (e is not LwPolyline lw || lw.Vertices.Count == 0) continue;
            var largura = Math.Max(lw.ConstantWidth, lw.Vertices.Max(v => Math.Max(v.StartWidth, v.EndWidth)));
            if (largura <= 0) continue;
            var ext = Math.Max(lw.Vertices.Max(v => v.Location.X) - lw.Vertices.Min(v => v.Location.X), lw.Vertices.Max(v => v.Location.Y) - lw.Vertices.Min(v => v.Location.Y));
            if (largura > 2 * ext) suspeitas++;
        }
        return suspeitas;
    }
}

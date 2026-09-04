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
        if (string.IsNullOrWhiteSpace(entrada) || !File.Exists(entrada)) return;

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
                sb.AppendLine($"   Viewport id={vp.Id} papel={vp.RepresentsPaper} centro={vp.Center} W={vp.Width.ToString(ci)} H={vp.Height.ToString(ci)} viewCentro={vp.ViewCenter} viewH={vp.ViewHeight.ToString(ci)} twist={vp.TwistAngle.ToString(ci)} status={vp.Status} camada={vp.Layer?.Name}");
            var outros = layout.AssociatedBlock.Entities.Where(e => e is not Viewport).ToList();
            sb.AppendLine($"   Outras entidades: {outros.Count} ({string.Join(", ", outros.GroupBy(e => e.ObjectName).Select(g => $"{g.Key} x{g.Count()}"))})");
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var e in outros)
            {
                try { var bb = e.GetBoundingBox(); minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y); maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y); } catch { }
            }
            if (outros.Count > 0) sb.AppendLine($"   Extensão das entidades: ({minX.ToString(ci)},{minY.ToString(ci)}) - ({maxX.ToString(ci)},{maxY.ToString(ci)})");
        }

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

        var saida = Environment.GetEnvironmentVariable("DWGPARAPDF_DIAG_SAIDA");
        var destino = string.IsNullOrWhiteSpace(saida) ? Path.ChangeExtension(entrada, ".layouts.txt") : saida;
        File.WriteAllText(destino, sb.ToString(), new UTF8Encoding(false));
    }
}

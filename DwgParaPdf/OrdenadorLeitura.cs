namespace DwgParaPdf;

/// <summary>
/// Agrupa textos em linhas de leitura: mesma faixa de Y (tolerância ligada à altura típica dos textos),
/// da esquerda para a direita; linhas de cima para baixo. MTEXT multi-linha vira parágrafo próprio.
/// </summary>
public static class OrdenadorLeitura
{
    public const string SeparadorNaLinha = " | ";

    public static void Ordenar(EspacoExtraido espaco)
    {
        espaco.Linhas.Clear();
        espaco.Linhas.AddRange(AgruparEmLinhas(espaco.Textos));
    }

    public static List<string> AgruparEmLinhas(IReadOnlyList<TextoCad> textos)
    {
        if (textos.Count == 0) return new List<string>();

        var alturas = textos.Where(t => t.Altura > 0).Select(t => t.Altura).OrderBy(a => a).ToList();
        var alturaTipica = alturas.Count > 0 ? alturas[alturas.Count / 2] : 2.5;
        var tolerancia = Math.Max(alturaTipica * 0.6, 1e-6);

        var ordenados = textos.OrderByDescending(t => t.Y).ThenBy(t => t.X).ToList();

        var linhas = new List<List<TextoCad>>();
        List<TextoCad>? atual = null;
        double yReferencia = 0;

        foreach (var t in ordenados)
        {
            if (t.Texto.Contains('\n'))
            {
                linhas.Add(new List<TextoCad> { t });
                atual = null;
                continue;
            }

            if (atual is null || Math.Abs(t.Y - yReferencia) > tolerancia)
            {
                atual = new List<TextoCad>();
                linhas.Add(atual);
                yReferencia = t.Y;
            }
            atual.Add(t);
        }

        // dentro da mesma faixa de Y, textos muito afastados em X são coisas diferentes (ex.: rótulo de grade
        // à esquerda e carimbo à direita): quebra a linha quando a lacuna passa de ~30 alturas de texto
        var lacunaMaxima = alturaTipica * 30;
        var resultado = new List<string>();
        foreach (var linha in linhas)
        {
            List<string>? segmento = null;
            var fimAnterior = double.NegativeInfinity;
            foreach (var t in linha.OrderBy(t => t.X))
            {
                if (segmento is null || t.X - fimAnterior > lacunaMaxima)
                {
                    if (segmento is not null) resultado.Add(string.Join(SeparadorNaLinha, segmento));
                    segmento = new List<string>();
                }
                segmento.Add(t.Texto);

                var altura = t.Altura > 0 ? t.Altura : alturaTipica;
                fimAnterior = Math.Max(fimAnterior, t.X + t.Texto.Length * altura * 0.7); // largura estimada
            }
            if (segmento is not null) resultado.Add(string.Join(SeparadorNaLinha, segmento));
        }
        return resultado;
    }
}

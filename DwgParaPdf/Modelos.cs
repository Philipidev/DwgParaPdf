using System.Text;

namespace DwgParaPdf;

/// <summary>Um texto encontrado no desenho, já limpo, com posição em coordenadas do espaço (mundo ou papel).</summary>
public sealed record TextoCad(string Tipo, string Camada, string Texto, double X, double Y, double Altura);

public sealed class EspacoExtraido
{
    public required string Nome { get; init; }
    public bool EhModelo { get; init; }

    /// <summary>Textos posicionados (TEXT, MTEXT, ATTRIB, MLEADER, TABLE). Cotas ficam separadas.</summary>
    public List<TextoCad> Textos { get; } = new();

    /// <summary>Textos de cota (DIMENSION), sem posição relevante para leitura.</summary>
    public List<string> Cotas { get; } = new();

    /// <summary>Linhas em ordem de leitura (cima para baixo, esquerda para direita), preenchidas pelo ordenador.</summary>
    public List<string> Linhas { get; } = new();
}

public sealed class BlocoComAtributos
{
    public required string Bloco { get; init; }
    public required string Espaco { get; init; }
    public List<(string Tag, string Valor)> Atributos { get; } = new();
}

public sealed class DocumentoExtraido
{
    public required string NomeOrigem { get; init; }
    public string? Versao { get; set; }
    public DateTimeOffset ExtraidoEm { get; init; } = DateTimeOffset.Now;

    /// <summary>Propriedades do arquivo (SummaryInfo: título, assunto, autor, palavras-chave, comentários, customizadas).</summary>
    public Dictionary<string, string> Propriedades { get; } = new();
    public List<string> Camadas { get; } = new();
    public List<EspacoExtraido> Espacos { get; } = new();
    public List<BlocoComAtributos> Blocos { get; } = new();
    public List<string> Avisos { get; } = new();

    public int TotalTextos => Espacos.Sum(e => e.Textos.Count);
    public int TotalCotas => Espacos.Sum(e => e.Cotas.Count);

    /// <summary>
    /// Estrutura comum consumida pelo PDF e pelo texto plano: seções com título e parágrafos.
    /// Parágrafos podem conter quebras de linha (MTEXT multi-linha).
    /// </summary>
    public List<(string Titulo, List<string> Paragrafos)> Secoes()
    {
        var secoes = new List<(string, List<string>)>();

        var props = new List<string>
        {
            $"Arquivo: {NomeOrigem}",
            $"Versão do formato: {Versao ?? "desconhecida"}",
            $"Extraído em: {ExtraidoEm:yyyy-MM-dd HH:mm:ss zzz}",
        };
        foreach (var (chave, valor) in Propriedades)
            props.Add($"{chave}: {valor}");
        props.Add($"Espaços: {Espacos.Count}; textos: {TotalTextos}; cotas: {TotalCotas}; camadas: {Camadas.Count}; blocos com atributos: {Blocos.Count}");
        secoes.Add(("Propriedades do desenho", props));

        if (Camadas.Count > 0)
            secoes.Add(("Camadas", new List<string> { string.Join(", ", Camadas) }));

        foreach (var espaco in Espacos)
        {
            var paragrafos = new List<string>(espaco.Linhas);
            if (espaco.Cotas.Count > 0)
            {
                var agrupadas = espaco.Cotas
                    .GroupBy(c => c)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => g.Count() > 1 ? $"{g.Key} (x{g.Count()})" : g.Key);
                paragrafos.Add("Cotas: " + string.Join("; ", agrupadas));
            }
            if (paragrafos.Count == 0)
                paragrafos.Add("(sem texto)");

            var titulo = espaco.EhModelo ? $"Espaço do modelo ({espaco.Nome})" : $"Layout: {espaco.Nome}";
            secoes.Add((titulo, paragrafos));
        }

        if (Blocos.Count > 0)
        {
            var linhas = Blocos
                .Select(b => $"{b.Bloco} [{b.Espaco}]: " + string.Join("; ", b.Atributos.Select(a => $"{a.Tag} = {a.Valor}")))
                .ToList();
            secoes.Add(("Blocos com atributos", linhas));
        }

        if (Avisos.Count > 0)
            secoes.Add(("Avisos de leitura", new List<string>(Avisos)));

        return secoes;
    }

    /// <summary>Tabela (TSV) com cada texto e sua posição: espaço, tipo, camada, X, Y, altura, texto.</summary>
    public string ParaTsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("espaco\ttipo\tcamada\tx\ty\taltura\ttexto");
        var cultura = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var espaco in Espacos)
        {
            foreach (var t in espaco.Textos.OrderByDescending(t => t.Y).ThenBy(t => t.X))
            {
                sb.Append(espaco.Nome).Append('\t')
                  .Append(t.Tipo).Append('\t')
                  .Append(t.Camada).Append('\t')
                  .Append(t.X.ToString("0.###", cultura)).Append('\t')
                  .Append(t.Y.ToString("0.###", cultura)).Append('\t')
                  .Append(t.Altura.ToString("0.###", cultura)).Append('\t')
                  .AppendLine(t.Texto.Replace('\t', ' ').Replace('\n', ' '));
            }
            foreach (var cota in espaco.Cotas)
                sb.Append(espaco.Nome).Append("\tDIM\t\t\t\t\t").AppendLine(cota);
        }
        return sb.ToString();
    }

    /// <summary>Renderização em texto plano (Markdown leve). Mesmo conteúdo do PDF.</summary>
    public string ParaTexto()
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(NomeOrigem);
        foreach (var (titulo, paragrafos) in Secoes())
        {
            sb.AppendLine();
            sb.Append("## ").AppendLine(titulo);
            sb.AppendLine();
            foreach (var p in paragrafos)
                sb.AppendLine(p);
        }
        return sb.ToString();
    }
}

public sealed class ResultadoConversao
{
    public required DocumentoExtraido Documento { get; init; }
    public required byte[] Pdf { get; init; }
    public required string Texto { get; init; }
    public ModoSaida Modo { get; init; }
    public int Paginas { get; init; }
    public int EntidadesDesenhadas { get; init; }
    public int EntidadesComErro { get; init; }

    public string NomeSugeridoPdf => Path.ChangeExtension(Documento.NomeOrigem, ".pdf");
}

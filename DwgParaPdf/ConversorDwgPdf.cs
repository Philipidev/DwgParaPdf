using DwgParaPdf.Desenho;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace DwgParaPdf;

public enum ModoSaida
{
    /// <summary>PDF vetorial fiel ao desenho (geometria + texto real), uma página por espaço/layout.</summary>
    Desenho,

    /// <summary>PDF só com o texto extraído, em ordem de leitura.</summary>
    Texto,

    /// <summary>Páginas do desenho seguidas das páginas de texto.</summary>
    Ambos,
}

public sealed class OpcoesConversao
{
    public ModoSaida Modo { get; init; } = ModoSaida.Desenho;
    public OpcoesDesenho Desenho { get; init; } = new();
}

/// <summary>
/// Fachada: recebe a fonte (caminho, URL, bytes ou stream), lê o desenho uma vez e devolve PDF + texto plano.
/// Thread-safe; pode ser registrado como singleton.
/// </summary>
public sealed class ConversorDwgPdf
{
    private static readonly Lazy<HttpClient> HttpPadrao = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(2) });

    private readonly HttpClient _http;
    private readonly ExtratorTextoDwg _extrator;
    private readonly OpcoesConversao _opcoes;

    public ConversorDwgPdf(HttpClient? http = null, ExtratorTextoDwg? extrator = null, OpcoesConversao? opcoes = null)
    {
        _http = http ?? HttpPadrao.Value;
        _extrator = extrator ?? new ExtratorTextoDwg();
        _opcoes = opcoes ?? new OpcoesConversao();
    }

    public async Task<ResultadoConversao> ConverterAsync(FonteDwg fonte, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fonte);
        var conteudo = await fonte.LerBytesAsync(_http, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Converter(conteudo, fonte.Nome);
    }

    /// <summary>Aceita caminho local ou URL http(s).</summary>
    public Task<ResultadoConversao> ConverterAsync(string caminhoOuUrl, CancellationToken ct = default)
        => ConverterAsync(FonteDwg.Interpretar(caminhoOuUrl), ct);

    /// <summary>Converte o binário já em memória. <paramref name="nome"/> é só rótulo (título do PDF, nome sugerido).</summary>
    public ResultadoConversao Converter(byte[] conteudo, string nome)
    {
        var avisosLeitura = new List<string>();
        var doc = LeitorCad.Ler(conteudo, nome, avisosLeitura);
        var extraido = _extrator.Extrair(doc, nome, avisosLeitura);

        var stats = new EstatisticasDesenho();
        byte[] pdf;
        switch (_opcoes.Modo)
        {
            case ModoSaida.Texto:
                pdf = GeradorPdf.Gerar(extraido);
                stats.Paginas = ContarPaginas(pdf);
                break;

            case ModoSaida.Desenho:
                {
                    var avisosDesenho = new List<string>();
                    using var desenho = GeradorPdfDesenho.Gerar(doc, nome, _opcoes.Desenho, avisosDesenho, stats);
                    extraido.Avisos.AddRange(Agrupar(avisosDesenho));
                    pdf = Salvar(desenho);
                    break;
                }

            default:
                {
                    var avisosDesenho = new List<string>();
                    using var desenho = GeradorPdfDesenho.Gerar(doc, nome, _opcoes.Desenho, avisosDesenho, stats);
                    extraido.Avisos.AddRange(Agrupar(avisosDesenho));
                    using var texto = PdfReader.Open(new MemoryStream(GeradorPdf.Gerar(extraido)), PdfDocumentOpenMode.Import);
                    foreach (var pagina in texto.Pages) desenho.AddPage(pagina);
                    stats.Paginas = desenho.PageCount;
                    pdf = Salvar(desenho);
                    break;
                }
        }

        return new ResultadoConversao
        {
            Documento = extraido,
            Texto = extraido.ParaTexto(),
            Pdf = pdf,
            Modo = _opcoes.Modo,
            Paginas = stats.Paginas,
            EntidadesDesenhadas = stats.EntidadesDesenhadas,
            EntidadesComErro = stats.EntidadesComErro,
        };
    }

    private static byte[] Salvar(PdfDocument pdf)
    {
        using var ms = new MemoryStream();
        pdf.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static int ContarPaginas(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.InformationOnly);
        return doc.PageCount;
    }

    private static IEnumerable<string> Agrupar(List<string> avisos) => avisos
        .GroupBy(a => a, StringComparer.Ordinal)
        .Select(g => g.Count() > 1 ? $"{g.Key} (x{g.Count()})" : g.Key);
}

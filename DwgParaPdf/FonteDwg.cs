namespace DwgParaPdf;

/// <summary>
/// Origem de um desenho CAD: caminho local, URL http(s), bytes em memória ou stream.
/// </summary>
public sealed class FonteDwg
{
    public enum TipoFonte { Caminho, Url, Bytes, Stream }

    public TipoFonte Tipo { get; }

    /// <summary>Nome lógico do arquivo (usado no PDF e para sugerir o nome de saída).</summary>
    public string Nome { get; }

    public string? Caminho { get; }
    public Uri? Url { get; }
    public ReadOnlyMemory<byte> Bytes { get; }
    public Stream? Stream { get; }

    private FonteDwg(TipoFonte tipo, string nome, string? caminho = null, Uri? url = null,
        ReadOnlyMemory<byte> bytes = default, Stream? stream = null)
    {
        Tipo = tipo;
        Nome = string.IsNullOrWhiteSpace(nome) ? "desenho.dwg" : nome;
        Caminho = caminho;
        Url = url;
        Bytes = bytes;
        Stream = stream;
    }

    public static FonteDwg DeCaminho(string caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)) throw new ArgumentException("Caminho vazio.", nameof(caminho));
        var completo = Path.GetFullPath(caminho);
        return new FonteDwg(TipoFonte.Caminho, Path.GetFileName(completo), caminho: completo);
    }

    public static FonteDwg DeUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Esquema de URL não suportado: {url.Scheme}. Use http ou https.", nameof(url));

        var ultimoSegmento = url.Segments.Length > 0 ? Uri.UnescapeDataString(url.Segments[^1].Trim('/')) : string.Empty;
        var nome = string.IsNullOrWhiteSpace(ultimoSegmento) ? "desenho.dwg" : ultimoSegmento;
        return new FonteDwg(TipoFonte.Url, nome, url: url);
    }

    public static FonteDwg DeUrl(string url) => DeUrl(new Uri(url, UriKind.Absolute));

    public static FonteDwg DeBytes(ReadOnlyMemory<byte> bytes, string nome)
    {
        if (bytes.IsEmpty) throw new ArgumentException("Conteúdo vazio.", nameof(bytes));
        return new FonteDwg(TipoFonte.Bytes, nome, bytes: bytes);
    }

    public static FonteDwg DeBytes(byte[] bytes, string nome) => DeBytes(new ReadOnlyMemory<byte>(bytes), nome);

    public static FonteDwg DeStream(Stream stream, string nome)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new FonteDwg(TipoFonte.Stream, nome, stream: stream);
    }

    /// <summary>
    /// Interpreta uma entrada textual: URL http(s) absoluta vira <see cref="TipoFonte.Url"/>; qualquer outra coisa é caminho local.
    /// </summary>
    public static FonteDwg Interpretar(string entrada)
    {
        if (string.IsNullOrWhiteSpace(entrada)) throw new ArgumentException("Entrada vazia.", nameof(entrada));

        if (Uri.TryCreate(entrada, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return DeUrl(uri);

        return DeCaminho(entrada);
    }

    /// <summary>Materializa a fonte em memória. O leitor de DWG exige stream posicionável, então tudo vira byte[].</summary>
    public async Task<byte[]> LerBytesAsync(HttpClient http, CancellationToken ct = default)
    {
        switch (Tipo)
        {
            case TipoFonte.Caminho:
                if (!File.Exists(Caminho)) throw new FileNotFoundException("Arquivo não encontrado.", Caminho);
                return await File.ReadAllBytesAsync(Caminho!, ct);

            case TipoFonte.Url:
                using (var resposta = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resposta.EnsureSuccessStatusCode();
                    return await resposta.Content.ReadAsByteArrayAsync(ct);
                }

            case TipoFonte.Bytes:
                return Bytes.ToArray();

            case TipoFonte.Stream:
                if (Stream is MemoryStream ms && ms.TryGetBuffer(out var buffer))
                    return buffer.ToArray();
                using (var copia = new MemoryStream())
                {
                    await Stream!.CopyToAsync(copia, ct);
                    return copia.ToArray();
                }

            default:
                throw new InvalidOperationException($"Tipo de fonte desconhecido: {Tipo}");
        }
    }
}

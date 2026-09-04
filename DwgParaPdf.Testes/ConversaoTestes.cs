using System.Net;
using System.Net.Sockets;
using System.Text;
using ACadSharp;
using Xunit;

namespace DwgParaPdf.Testes;

public class ConversaoTestes
{
    private static readonly ConversorDwgPdf Conversor = new();

    [Theory]
    [InlineData(ACadVersion.AC1015)] // AutoCAD 2000
    [InlineData(ACadVersion.AC1018)] // 2004
    [InlineData(ACadVersion.AC1024)] // 2010
    [InlineData(ACadVersion.AC1032)] // 2018 em diante
    public void Dwg_extrai_textos_blocos_layouts_e_cotas(ACadVersion versao)
    {
        var bytes = AmostraDwg.SalvarDwg(AmostraDwg.Criar(versao));

        var resultado = Conversor.Converter(bytes, $"amostra_{versao}.dwg");

        ConferirConteudo(resultado);
        Assert.Equal(versao.ToString(), resultado.Documento.Versao);
    }

    [Fact]
    public void Dxf_ascii_e_binario_sao_aceitos()
    {
        var doc = AmostraDwg.Criar();

        var ascii = Conversor.Converter(AmostraDwg.SalvarDxf(doc, binario: false), "amostra.dxf");
        var binario = Conversor.Converter(AmostraDwg.SalvarDxf(doc, binario: true), "amostra_bin.dxf");

        ConferirConteudo(ascii);
        ConferirConteudo(binario);
    }

    [Fact]
    public void Pdf_e_texto_puro_sem_imagens()
    {
        var resultado = Conversor.Converter(AmostraDwg.SalvarDwg(AmostraDwg.Criar()), "amostra.dwg");

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(resultado.Pdf, 0, 5));
        var corpo = Encoding.Latin1.GetString(resultado.Pdf);
        Assert.DoesNotMatch(@"/Subtype\s*/Image", corpo); // nenhum XObject de imagem (o ProcSet /ImageB etc. é padrão e não conta)
        Assert.Contains("/Font", corpo);
        Assert.Equal("amostra.pdf", resultado.NomeSugeridoPdf);
    }

    [Fact]
    public void Grava_amostra_em_disco_para_inspecao_manual()
    {
        // deixa um DWG sintético ao lado da DLL de testes; útil para rodar a CLI à mão
        var caminho = Path.Combine(AppContext.BaseDirectory, "amostra.dwg");
        File.WriteAllBytes(caminho, AmostraDwg.SalvarDwg(AmostraDwg.Criar()));
        Assert.True(new FileInfo(caminho).Length > 0);
    }

    [Fact]
    public async Task Bytes_e_stream_sao_aceitos_como_fonte()
    {
        var bytes = AmostraDwg.SalvarDwg(AmostraDwg.Criar());

        var porBytes = await Conversor.ConverterAsync(FonteDwg.DeBytes(bytes, "bytes.dwg"));
        using var stream = new MemoryStream(bytes);
        var porStream = await Conversor.ConverterAsync(FonteDwg.DeStream(stream, "stream.dwg"));

        ConferirConteudo(porBytes);
        ConferirConteudo(porStream);
        Assert.Equal("bytes.dwg", porBytes.Documento.NomeOrigem);
        Assert.Equal("stream.dwg", porStream.Documento.NomeOrigem);
    }

    [Fact]
    public async Task Url_http_e_baixada_e_convertida()
    {
        var bytes = AmostraDwg.SalvarDwg(AmostraDwg.Criar());
        var porta = PortaLivre();
        using var servidor = new HttpListener();
        servidor.Prefixes.Add($"http://localhost:{porta}/");
        servidor.Start();

        var atendimento = Task.Run(async () =>
        {
            var contexto = await servidor.GetContextAsync();
            contexto.Response.ContentType = "application/acad";
            contexto.Response.ContentLength64 = bytes.Length;
            await contexto.Response.OutputStream.WriteAsync(bytes);
            contexto.Response.Close();
        });

        var resultado = await Conversor.ConverterAsync($"http://localhost:{porta}/desenhos/Planta%20Geral.dwg?sas=abc");
        await atendimento;

        ConferirConteudo(resultado);
        Assert.Equal("Planta Geral.dwg", resultado.Documento.NomeOrigem);
    }

    [Fact]
    public void Conteudo_invalido_lanca_excecao_clara()
    {
        var lixo = Encoding.UTF8.GetBytes("isto não é um desenho");

        var erro = Assert.Throws<InvalidDataException>(() => Conversor.Converter(lixo, "lixo.dwg"));
        Assert.Contains("lixo.dwg", erro.Message);
    }

    [Fact]
    public void Interpretar_distingue_url_de_caminho()
    {
        Assert.Equal(FonteDwg.TipoFonte.Url, FonteDwg.Interpretar("https://x.exemplo.com/a/b.dwg").Tipo);
        Assert.Equal(FonteDwg.TipoFonte.Caminho, FonteDwg.Interpretar(@"C:\temp\b.dwg").Tipo);
        Assert.Equal(FonteDwg.TipoFonte.Caminho, FonteDwg.Interpretar("relativo/b.dwg").Tipo);
        Assert.Throws<ArgumentException>(() => FonteDwg.DeUrl("ftp://x.exemplo.com/b.dwg"));
    }

    [Fact]
    public void Sem_cotas_omite_dimensoes()
    {
        var conversor = new ConversorDwgPdf(extrator: new ExtratorTextoDwg { IncluirCotas = false });
        var resultado = conversor.Converter(AmostraDwg.SalvarDwg(AmostraDwg.Criar()), "amostra.dwg");

        Assert.Equal(0, resultado.Documento.TotalCotas);
        Assert.DoesNotContain("Cotas:", resultado.Texto);
    }

    private static void ConferirConteudo(ResultadoConversao resultado)
    {
        var texto = resultado.Texto;

        // TEXT com %%d e a mesma linha de leitura (Y=100 e Y=100,5)
        Assert.Contains("PLANTA BAIXA - NIVEL 100 ° | ESCALA 1:50", texto);

        // MTEXT limpo, multi-linha, com fração empilhada e %%c
        Assert.Contains("NOTAS GERAIS\nConcreto fck = 30 MPa\nAço CA-50 1/2 Ø 12,5 mm", texto.Replace("\r\n", "\n"));

        // bloco: texto interno transformado pelo INSERT (200,0) + atributos na mesma linha do texto vizinho
        Assert.Contains("CARIMBO-> | PROJETO: | BARRAGEM DO RIO VERDE", texto);
        Assert.Contains("PBV-CIV-001-R2", texto);
        Assert.Contains("CARIMBO [Model]: PROJETO = BARRAGEM DO RIO VERDE; DESENHO = PBV-CIV-001-R2", texto);

        // layout de papel
        Assert.Contains("Layout: PRANCHA 01", texto);
        Assert.Contains("PRANCHA 01 - PLANTA GERAL", texto);

        // cota
        Assert.Contains("Cotas: 15", texto);

        // nada de código bruto
        Assert.DoesNotContain(@"\P", texto);
        Assert.DoesNotContain(@"\f", texto);
        Assert.DoesNotContain("%%", texto);

        Assert.True(resultado.Pdf.Length > 1000, "PDF muito pequeno");
    }

    private static int PortaLivre()
    {
        using var ouvinte = new TcpListener(IPAddress.Loopback, 0);
        ouvinte.Start();
        return ((IPEndPoint)ouvinte.LocalEndpoint).Port;
    }
}

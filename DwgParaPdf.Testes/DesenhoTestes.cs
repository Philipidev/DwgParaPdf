using DwgParaPdf.Desenho;
using PdfSharp.Pdf.IO;
using Xunit;

namespace DwgParaPdf.Testes;

public class DesenhoTestes
{
    private static byte[] Amostra() => AmostraDwg.SalvarDwg(AmostraDwg.Criar());

    [Fact]
    public void Modo_desenho_gera_pagina_do_modelo_e_do_layout_com_conteudo()
    {
        var conversor = new ConversorDwgPdf(opcoes: new OpcoesConversao { Modo = ModoSaida.Desenho });

        var resultado = conversor.Converter(Amostra(), "amostra.dwg");

        using var pdf = PdfReader.Open(new MemoryStream(resultado.Pdf), PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount); // Model + PRANCHA 01 (Layout1 vazio é omitido)
        Assert.Equal(resultado.Paginas, pdf.PageCount);
        Assert.True(pdf.Pages[0].Width.Point > pdf.Pages[0].Height.Point, "extensão mais larga que alta => página paisagem");
        Assert.True(resultado.EntidadesDesenhadas >= 12, $"poucas entidades desenhadas: {resultado.EntidadesDesenhadas}");
        Assert.Equal(0, resultado.EntidadesComErro);
        Assert.DoesNotContain(resultado.Documento.Avisos, a => a.StartsWith("Tipo de entidade não desenhado", StringComparison.Ordinal));
    }

    [Fact]
    public void Modo_desenho_mantem_texto_real_no_pdf()
    {
        var conversor = new ConversorDwgPdf(opcoes: new OpcoesConversao { Modo = ModoSaida.Desenho });

        var resultado = conversor.Converter(Amostra(), "amostra.dwg");

        var corpo = System.Text.Encoding.Latin1.GetString(resultado.Pdf);
        Assert.Contains("/Font", corpo);
        Assert.DoesNotMatch(@"/Subtype\s*/Image", corpo);
        Assert.Contains("PLANTA BAIXA", resultado.Texto); // texto extraído continua disponível
    }

    [Fact]
    public void Modo_ambos_concatena_desenho_e_texto()
    {
        var bytes = Amostra();
        var desenho = new ConversorDwgPdf(opcoes: new OpcoesConversao { Modo = ModoSaida.Desenho }).Converter(bytes, "a.dwg");
        var texto = new ConversorDwgPdf(opcoes: new OpcoesConversao { Modo = ModoSaida.Texto }).Converter(bytes, "a.dwg");
        var ambos = new ConversorDwgPdf(opcoes: new OpcoesConversao { Modo = ModoSaida.Ambos }).Converter(bytes, "a.dwg");

        Assert.Equal(desenho.Paginas + texto.Paginas, ambos.Paginas);
        using var pdf = PdfReader.Open(new MemoryStream(ambos.Pdf), PdfDocumentOpenMode.Import);
        Assert.Equal(ambos.Paginas, pdf.PageCount);
    }

    [Fact]
    public void So_modelo_e_so_layouts_filtram_paginas()
    {
        var bytes = Amostra();
        var soModelo = new ConversorDwgPdf(opcoes: new OpcoesConversao { Desenho = new OpcoesDesenho { IncluirLayouts = false } }).Converter(bytes, "a.dwg");
        var soLayouts = new ConversorDwgPdf(opcoes: new OpcoesConversao { Desenho = new OpcoesDesenho { IncluirModelo = false } }).Converter(bytes, "a.dwg");

        Assert.Equal(1, soModelo.Paginas);
        Assert.Equal(1, soLayouts.Paginas);
    }

    [Fact]
    public void Lod_reduz_geometria_e_preserva_texto()
    {
        var bytes = Amostra();
        var alto = new ConversorDwgPdf(opcoes: new OpcoesConversao { Desenho = new OpcoesDesenho { Lod = NivelDetalhe.Alto } }).Converter(bytes, "a.dwg");
        var medio = new ConversorDwgPdf(opcoes: new OpcoesConversao { Desenho = new OpcoesDesenho { Lod = NivelDetalhe.Medio } }).Converter(bytes, "a.dwg");
        var baixo = new ConversorDwgPdf(opcoes: new OpcoesConversao { Desenho = new OpcoesDesenho { Lod = NivelDetalhe.Baixo } }).Converter(bytes, "a.dwg");

        Assert.Equal(alto.Paginas, baixo.Paginas);
        Assert.True(medio.Pdf.Length <= alto.Pdf.Length, $"medio {medio.Pdf.Length} > alto {alto.Pdf.Length}");
        Assert.True(baixo.Pdf.Length <= medio.Pdf.Length, $"baixo {baixo.Pdf.Length} > medio {medio.Pdf.Length}");
        Assert.Equal(alto.Documento.TotalTextos, baixo.Documento.TotalTextos); // texto nunca é degradado
        Assert.Equal(0, baixo.EntidadesComErro);
    }

    [Theory]
    [InlineData(255, 255, 0, true)]     // amarelo: escurece
    [InlineData(0, 255, 255, true)]     // ciano: escurece
    [InlineData(200, 200, 200, true)]   // cinza claro: escurece
    [InlineData(255, 0, 0, false)]      // vermelho: mantém
    [InlineData(0, 0, 255, false)]      // azul: mantém
    [InlineData(0, 0, 0, false)]        // preto: mantém
    public void Contrastar_escurece_so_cores_claras_mantendo_matiz(int r, int g, int b, bool deveEscurecer)
    {
        var original = PdfSharp.Drawing.XColor.FromArgb(r, g, b);
        var ajustada = RenderizadorEntidades.Contrastar(original);

        var lumOriginal = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var lumAjustada = 0.2126 * ajustada.R + 0.7152 * ajustada.G + 0.0722 * ajustada.B;
        if (deveEscurecer)
        {
            Assert.True(lumAjustada < lumOriginal * 0.7, $"não escureceu: {ajustada.R},{ajustada.G},{ajustada.B}");
            // matiz preservado: o canal dominante continua dominante
            if (r == 255 && g == 255) Assert.True(ajustada.R == ajustada.G && ajustada.B < ajustada.R);
            if (g == 255 && b == 255 && r == 0) Assert.True(ajustada.G == ajustada.B && ajustada.R < ajustada.G);
        }
        else
            Assert.Equal((original.R, original.G, original.B), (ajustada.R, ajustada.G, ajustada.B));
    }

    [Fact]
    public void Modo_mono_gera_pdf_valido()
    {
        var mono = new ConversorDwgPdf(opcoes: new OpcoesConversao { Desenho = new OpcoesDesenho { Cores = ModoCores.Mono } }).Converter(Amostra(), "a.dwg");
        Assert.Equal(2, mono.Paginas);
        Assert.Equal(0, mono.EntidadesComErro);
    }

    [Fact]
    public void Afim2D_compoe_na_ordem_certa()
    {
        // ponto local (1,0) num bloco escalado 2x, girado 90° e inserido em (10,10) => (10, 12)
        var m = Afim2D.DeInsercao(new CSMath.XYZ(10, 10, 0), 2, 2, Math.PI / 2);
        var p = m.Aplicar(1, 0);
        Assert.Equal(10, p.X, 6);
        Assert.Equal(12, p.Y, 6);

        var pagina = Afim2D.Pagina(2, 0, 100).Compor(m); // Y invertido na página
        var q = pagina.Aplicar(1, 0);
        Assert.Equal(20, q.X, 6);
        Assert.Equal(100 - 24, q.Y, 6);
    }
}

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

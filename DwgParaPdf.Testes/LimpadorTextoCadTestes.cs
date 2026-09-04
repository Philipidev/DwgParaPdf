using Xunit;

namespace DwgParaPdf.Testes;

public class LimpadorTextoCadTestes
{
    [Theory]
    [InlineData(@"{\fArial|b1|i0|c0|p34;Título}\PLinha 2", "Título\nLinha 2")]
    [InlineData(@"\A1;Centralizado", "Centralizado")]
    [InlineData(@"Diâmetro %%c25 %%p0,5 a 90%%d", "Diâmetro Ø25 ±0,5 a 90°")]
    [InlineData(@"\S3^4; e \S1/2; e \S+0.1^ -0.1;", "3/4 e 1/2 e +0.1/-0.1")]
    [InlineData(@"caminho C:\\temp \{chaves\}", @"caminho C:\temp {chaves}")]
    [InlineData(@"\H2.5x;Grande \C1;vermelho \L sub\l fim", "Grande vermelho sub fim")]
    [InlineData(@"\U+00B2 e \U+00E7", "² e ç")]
    [InlineData("a\\~b   c\t d", "a b c d")]
    [InlineData(@"\pxi-3,l3,t3;item 1\Pitem 2", "item 1\nitem 2")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void LimparMText_remove_codigos_de_formatacao(string? entrada, string esperado)
        => Assert.Equal(esperado, LimpadorTextoCad.LimparMText(entrada));

    [Theory]
    [InlineData("%%uSUBLINHADO%%u", "SUBLINHADO")]
    [InlineData("100%%%", "100%")]
    [InlineData("%%065BC", "ABC")]
    [InlineData("  espaços   extras  ", "espaços extras")]
    [InlineData("50%% (sem código)", "50%% (sem código)")]
    public void LimparTexto_trata_sequencias_porcento(string entrada, string esperado)
        => Assert.Equal(esperado, LimpadorTextoCad.LimparTexto(entrada));
}

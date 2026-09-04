using CSMath;
using PdfSharp.Drawing;

namespace DwgParaPdf.Desenho;

/// <summary>
/// Transformação afim 2D: x' = A·x + C·y + Tx ; y' = B·x + D·y + Ty.
/// Usada para levar coordenadas do desenho (mundo, bloco, papel) até pontos da página (pt, Y para baixo).
/// </summary>
public readonly record struct Afim2D(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Afim2D Identidade = new(1, 0, 0, 1, 0, 0);

    public XPoint Aplicar(double x, double y) => new(A * x + C * y + Tx, B * x + D * y + Ty);
    public XPoint Aplicar(XYZ p) => Aplicar(p.X, p.Y);
    public XPoint Aplicar(XY p) => Aplicar(p.X, p.Y);

    /// <summary>Compõe: aplica <paramref name="interna"/> primeiro e depois esta.</summary>
    public Afim2D Compor(Afim2D interna) => new(
        A * interna.A + C * interna.B,
        B * interna.A + D * interna.B,
        A * interna.C + C * interna.D,
        B * interna.C + D * interna.D,
        A * interna.Tx + C * interna.Ty + Tx,
        B * interna.Tx + D * interna.Ty + Ty);

    /// <summary>Fator de escala médio (raiz do determinante), em unidades de saída por unidade de entrada.</summary>
    public double EscalaMedia => Math.Sqrt(Math.Abs(A * D - B * C));

    public static Afim2D Translacao(double tx, double ty) => new(1, 0, 0, 1, tx, ty);
    public static Afim2D Escala(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static Afim2D Rotacao(double radianos)
    {
        var c = Math.Cos(radianos);
        var s = Math.Sin(radianos);
        return new Afim2D(c, s, -s, c, 0, 0);
    }

    /// <summary>Mundo → página: escala uniforme, Y invertido, deslocamento.</summary>
    public static Afim2D Pagina(double escala, double tx, double ty) => new(escala, 0, 0, -escala, tx, ty);

    /// <summary>Transformação de um INSERT: escala, depois rotação, depois translação para o ponto de inserção.</summary>
    public static Afim2D DeInsercao(XYZ pontoInsercao, double escalaX, double escalaY, double rotacao)
    {
        var sx = escalaX == 0 ? 1 : escalaX;
        var sy = escalaY == 0 ? 1 : escalaY;
        return Translacao(pontoInsercao.X, pontoInsercao.Y).Compor(Rotacao(rotacao)).Compor(Escala(sx, sy));
    }
}

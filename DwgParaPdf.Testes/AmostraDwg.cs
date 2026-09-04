using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace DwgParaPdf.Testes;

/// <summary>Monta um desenho sintético com os tipos de texto que o extrator precisa tratar.</summary>
public static class AmostraDwg
{
    public const string TextoTitulo = "PLANTA BAIXA - NIVEL 100 %%d";
    public const string TextoEscala = "ESCALA 1:50";
    public const string MTextNotas = @"{\fArial|b1|i0|c0|p34;NOTAS GERAIS}\PConcreto fck = 30 MPa\PAço CA-50 \S1^2; %%c 12,5 mm";
    public const string ValorProjeto = "EDIFICIO EXEMPLO";
    public const string ValorDesenho = "PLANTA-01-R2";
    public const string NomeLayout = "PRANCHA 01";
    public const string TextoLayout = "PRANCHA 01 - PLANTA GERAL";

    public static CadDocument Criar(ACadVersion versao = ACadVersion.AC1032)
    {
        var doc = new CadDocument(versao);
        doc.Layers.Add(new Layer("TEXTOS"));

        doc.Entities.Add(new TextEntity { Value = TextoTitulo, InsertPoint = new XYZ(0, 100, 0), Height = 2.5 });
        doc.Entities.Add(new TextEntity { Value = TextoEscala, InsertPoint = new XYZ(60, 100.5, 0), Height = 2.5 });
        doc.Entities.Add(new MText { Value = MTextNotas, InsertPoint = new XYZ(0, 50, 0), Height = 2.5 });

        // carimbo: bloco com texto fixo + atributos, inserido deslocado no modelo
        var carimbo = new BlockRecord("CARIMBO");
        carimbo.Entities.Add(new TextEntity { Value = "PROJETO:", InsertPoint = new XYZ(0, 0, 0), Height = 2 });
        carimbo.Entities.Add(new AttributeDefinition { Tag = "PROJETO", Value = ValorProjeto, Prompt = "Projeto", InsertPoint = new XYZ(25, 0, 0), Height = 2 });
        carimbo.Entities.Add(new AttributeDefinition { Tag = "DESENHO", Value = ValorDesenho, Prompt = "Desenho", InsertPoint = new XYZ(25, -5, 0), Height = 2 });
        doc.BlockRecords.Add(carimbo);

        var insercao = new Insert(carimbo) { InsertPoint = new XYZ(200, 0, 0) };
        // no DWG o ATTRIB é gravado em coordenadas do espaço (não do bloco); o construtor copiou as definições
        // com a posição local, então desloca à mão para simular o que o AutoCAD grava
        foreach (var atributo in insercao.Attributes)
            atributo.InsertPoint = new XYZ(atributo.InsertPoint.X + 200, atributo.InsertPoint.Y, 0);
        doc.Entities.Add(new TextEntity { Value = "CARIMBO->", InsertPoint = new XYZ(150, 0, 0), Height = 2 });
        doc.Entities.Add(insercao);

        // cota linear de 15 unidades
        doc.Entities.Add(new DimensionLinear
        {
            FirstPoint = new XYZ(0, 0, 0),
            SecondPoint = new XYZ(15, 0, 0),
            DefinitionPoint = new XYZ(15, -10, 0),
        });

        // geometria variada para o renderizador
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 90, 0), EndPoint = new XYZ(120, 90, 0) });
        doc.Entities.Add(new Circle { Center = new XYZ(100, 60, 0), Radius = 8 });
        doc.Entities.Add(new Arc { Center = new XYZ(130, 60, 0), Radius = 8, StartAngle = 0, EndAngle = Math.PI });
        doc.Entities.Add(new Ellipse { Center = new XYZ(160, 60, 0), MajorAxisEndPoint = new XYZ(10, 0, 0), RadiusRatio = 0.5 });
        var contorno = new LwPolyline { IsClosed = true };
        contorno.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)));
        contorno.Vertices.Add(new LwPolyline.Vertex(new XY(40, 0)) { Bulge = 0.5 });
        contorno.Vertices.Add(new LwPolyline.Vertex(new XY(40, 30)));
        contorno.Vertices.Add(new LwPolyline.Vertex(new XY(0, 30)));
        doc.Entities.Add(contorno);
        doc.Entities.Add(new Solid { FirstCorner = new XYZ(60, 0, 0), SecondCorner = new XYZ(70, 0, 0), ThirdCorner = new XYZ(60, 10, 0), FourthCorner = new XYZ(70, 10, 0) });

        // layout de papel com um texto
        var layout = new Layout(NomeLayout);
        doc.Layouts.Add(layout);
        layout.AssociatedBlock.Entities.Add(new TextEntity { Value = TextoLayout, InsertPoint = new XYZ(10, 280, 0), Height = 5 });

        return doc;
    }

    public static byte[] SalvarDwg(CadDocument doc)
    {
        using var ms = new MemoryStream();
        using (var escritor = new DwgWriter(ms, doc))
            escritor.Write();
        return ms.ToArray();
    }

    public static byte[] SalvarDxf(CadDocument doc, bool binario = false)
    {
        using var ms = new MemoryStream();
        using (var escritor = new DxfWriter(ms, doc, binario))
            escritor.Write();
        return ms.ToArray();
    }
}

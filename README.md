# DwgParaPdf

Converte desenhos CAD (`.dwg` ou `.dxf`) em **PDF vetorial**: a geometria vira caminhos vetoriais e o texto
continua **texto real** (selecionável, pesquisável, extraível por qualquer ferramenta de RAG). Nada é rasterizado.

Aceita como entrada **caminho local**, **URL http(s)**, **bytes em memória** ou **stream**. Roda sem AutoCAD,
sem ODA e sem qualquer instalação além do .NET: a leitura do DWG é nativa
([ACadSharp](https://github.com/DomCR/ACadSharp), MIT) e o PDF é gerado com PDFsharp/MigraDoc (MIT).

## Três modos de saída

| Modo | O que sai | Quando usar |
|---|---|---|
| `desenho` (padrão) | Uma página para o espaço do modelo (extensão ajustada a A4…A0) e uma página por layout de papel com conteúdo, viewports renderizadas e recortadas, na folha declarada no layout. Como o AutoCAD plota. | Quer ver o desenho e também indexar o texto |
| `texto` | PDF só com o texto extraído, em ordem de leitura (cima→baixo, esquerda→direita), mais propriedades, camadas, blocos com atributos e cotas | Só quer o conteúdo textual, compacto |
| `ambos` | Páginas do desenho seguidas das páginas de texto | Melhor dos dois para RAG: figura fiel + texto já ordenado |

Em qualquer modo o resultado traz também `Texto` (Markdown leve) e a estrutura `DocumentoExtraido`.

## O que é renderizado no modo desenho

LINE, LWPOLYLINE/POLYLINE (com arcos por bulge e largura), ARC, CIRCLE, ELLIPSE, SPLINE, HATCH (sólida ou
padrão explodido em linhas; padrão muito denso vira preenchimento translúcido), SOLID, 3DFACE, POINT, TEXT,
MTEXT (quebra por largura, fixação, rotação), ATTRIB, INSERT (aninhado, MINSERT, cor/espessura/tipo de linha
ByBlock e camada 0), DIMENSION (pelo bloco da cota), LEADER, MLEADER, TABLE e VIEWPORT (recorte retangular
ou poligonal, camadas congeladas por viewport, twist). Cores ACI e true color (branco/7 plota preto), espessuras
de linha em mm, tipos de linha tracejados (LTSCALE e escala da entidade), fontes TrueType instaladas no
Windows pelo nome do arquivo do estilo (`calibri.ttf`, `arialbd.ttf`…); fontes SHX caem em Arial.

**Não renderizado**: REGION (sem geometria disponível), WIPEOUT (não mascara), imagens raster/OLE, sólidos 3D,
vistas 3D de viewport (renderizadas em planta). Os tipos ignorados aparecem nos avisos.

**Extensão do modelo**: união das caixas das entidades visíveis, com estimativa da área ocupada pelos textos e
descarte de entidades perdidas a mais de uma largura de núcleo do desenho (percentis 2–98). O papel vai de A4 a
A0, o menor em que o texto mediano fique ≥ 1,8 mm. Layouts usam a folha declarada (`PaperWidth`/`PaperHeight`
com rotação); plot "layout" em 1:1 quando cabe, senão o conteúdo é ajustado à folha, como o AutoCAD faz com
"extensão"/"ajustar ao papel". Layouts vazios são omitidos.

## O que é extraído como texto (modos texto/ambos e `--txt`/`--tsv`)

TEXT, MTEXT (códigos `\P`, `\f…;`, `\H…;`, `{}`, `%%c`, `%%d`, `%%p`, `\S1^2;` limpos ou convertidos), ATTRIB
(também em seção `Bloco [espaço]: TAG = valor`), texto em blocos aninhados com posição transformada, MLEADER,
células de TABLE, cotas (agrupadas), propriedades do arquivo (título, autor, palavras-chave…) e camadas.

## Requisitos

- .NET 10 SDK (compila também para net8/net9 mudando o `TargetFramework`).
- Fontes TrueType:
  - **Windows**: usa `C:\Windows\Fonts` automaticamente (Arial obrigatória; Calibri, Tahoma, Times etc. quando existirem).
  - **Linux/macOS/contêiner**: `DWGPARAPDF_FONTE=/caminho/fonte.ttf` ou um `.ttf` na pasta `fontes` ao lado do
    executável (uma única fonte para tudo). Sem isso, DejaVu/Liberation/Noto são procuradas.

## Uso pela linha de comando

```bash
dotnet build DwgParaPdf.sln -c Release
```

```bash
DwgParaPdf\bin\Release\net10.0\DwgParaPdf.exe "C:\projetos\PBV-CIV-001.dwg"
```

```
DwgParaPdf <entrada> [-o <saida.pdf>] [--modo desenho|texto|ambos] [--so-modelo] [--so-layouts]
           [--txt] [--tsv] [--sem-cotas] [--nome <arquivo.dwg>] [-v]

  <entrada>      caminho local (.dwg ou .dxf), URL http(s), ou "-" para ler o binário do stdin
  -o, --saida    caminho do PDF (padrão: mesmo nome da entrada com .pdf; URL/stdin: pasta atual)
  --modo         desenho (padrão) | texto | ambos
  --so-modelo    no modo desenho, só a página do espaço do modelo
  --so-layouts   no modo desenho, só as páginas dos layouts de papel
  --txt          grava também um .txt (Markdown leve) com o texto extraído
  --tsv          grava também um .tsv com cada texto e sua posição (espaço, tipo, camada, x, y, altura)
  --sem-cotas    no texto extraído, ignora entidades DIMENSION
  --nome         nome lógico do desenho quando a entrada é stdin
  -v, --verboso  imprime os avisos e a pilha em caso de erro
```

Exemplos:

```bash
DwgParaPdf.exe https://conta.blob.core.windows.net/desenhos/PBV-CIV-001.dwg?sv=... -o .\saida\PBV-CIV-001.pdf --modo ambos
```

```bash
type desenho.dwg | DwgParaPdf.exe - --nome desenho.dwg --txt
```

Lote (PowerShell), um PDF ao lado de cada DWG:

```bash
Get-ChildItem "J:\DWG arquivos" -Recurse -Filter *.dwg | ForEach-Object { & "C:\Repositorios\Sysdam\DwgParaPdf\DwgParaPdf\bin\Release\net10.0\DwgParaPdf.exe" $_.FullName }
```

Códigos de saída: `0` sucesso, `1` erro de uso, `2` falha na conversão, `130` cancelado.

## Uso como biblioteca

Referencie o projeto (ou copie a pasta `DwgParaPdf` sem o `Program.cs`). Namespace `DwgParaPdf`.

```csharp
var conversor = new ConversorDwgPdf(opcoes: new OpcoesConversao { Modo = ModoSaida.Ambos }); // singleton ok

ResultadoConversao r = await conversor.ConverterAsync(@"C:\desenhos\planta.dwg", ct);            // caminho
ResultadoConversao r2 = await conversor.ConverterAsync("https://.../planta.dwg", ct);              // URL
ResultadoConversao r3 = conversor.Converter(bytes, "planta.dwg");                                  // bytes
ResultadoConversao r4 = await conversor.ConverterAsync(FonteDwg.DeStream(stream, "planta.dwg"), ct); // stream

byte[] pdf = r.Pdf;                 // PDF vetorial (e/ou de texto, conforme o modo)
int paginas = r.Paginas;
string texto = r.Texto;             // texto extraído em Markdown leve
DocumentoExtraido d = r.Documento;  // propriedades, camadas, espaços com textos posicionados, blocos, avisos
string tsv = d.ParaTsv();
```

Ajustes:

```csharp
new OpcoesConversao
{
    Modo = ModoSaida.Desenho,
    Desenho = new OpcoesDesenho { IncluirModelo = true, IncluirLayouts = true, AlturaTextoMinimaMm = 1.8, MargemMm = 10 },
};
new ExtratorTextoDwg { IncluirCotas = false, ProfundidadeMaximaBlocos = 4, MaximoAvisos = 20 };
```

## Testes

```bash
dotnet test DwgParaPdf.sln -c Release
```

34 testes: DWG sintético (TEXT, MTEXT com códigos, bloco com atributos, cota, geometria variada, layout) nas
versões AC1015, AC1018, AC1024 e AC1032, DXF ASCII e binário, as quatro formas de entrada (caminho, URL via
`HttpListener` local, bytes, stream), os três modos e o filtro de páginas. `amostra.dwg` fica ao lado da DLL
de testes para rodar a CLI à mão.

Validado com 8 desenhos reais de AutoCAD (mapas cadastrais, mobilidade urbana, formatos de prancha, blocos de
vegetação, mecânica; 0,1 a 19 MB). O maior (211 mil textos, 639 mil entidades) leva ~25 s e gera 21 MB.

Ferramenta de apoio: com `DWGPARAPDF_DIAG=<arquivo.dwg>` o teste `DiagnosticoLayoutsTestes` despeja as
configurações de folha dos layouts e as maiores caixas de texto estimadas (para depurar dimensionamento).

## Detalhes que custaram caro (para quem for mexer)

- A viewport `id=1` de cada layout **não é a folha**: é a janela do espaço do papel na tela. A folha vem de
  `PaperWidth`/`PaperHeight`/`PaperRotation`; as unidades do layout vêm de `PaperUnits` (polegadas ou mm).
- `TextEntity.GetBoundingBox()` lança exceção no ACadSharp 3.7.1; a caixa dos textos é estimada aqui.
- Arquivos reais trazem `MText.RectangleWidth = 2e-10`: largura de quebra menor que um caractere é lixo.
- `Arc.CreateFromBulge` com vértices coincidentes produz NaN/raio zero: bulge degenerado vira segmento reto.
- A extensão salva no cabeçalho (`$EXTMIN/$EXTMAX`) inclui entidades perdidas; não a use para enquadrar.
- Textos em `Insert.Attributes` já vêm em coordenadas do espaço, não do bloco.

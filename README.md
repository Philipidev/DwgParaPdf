# DwgParaPdf

Converte desenhos CAD (`.dwg` ou `.dxf`) em **PDF vetorial**: a geometria vira caminhos vetoriais e o texto
continua **texto real** (selecionável, pesquisável e extraível por qualquer ferramenta de indexação/RAG).
Nada é rasterizado. Opcionalmente gera também um PDF só com o texto do desenho em ordem de leitura.

Roda sem AutoCAD, sem ODA File Converter e sem nenhuma instalação além do .NET. A leitura do DWG é feita em
código gerenciado pelo [ACadSharp](https://github.com/DomCR/ACadSharp) e o PDF é escrito com
[PDFsharp/MigraDoc](https://github.com/empira/pdfsharp). Ambos MIT.

Aceita como entrada **caminho local**, **URL http(s)**, **bytes em memória** ou **stream**, tanto pela linha
de comando quanto como biblioteca .NET.

---

## Índice

1. [Por que existe](#por-que-existe)
2. [Download (sem compilar)](#download-sem-compilar)
3. [Instalação e compilação](#instalação-e-compilação)
3. [Uso pela linha de comando](#uso-pela-linha-de-comando)
4. [Uso como biblioteca](#uso-como-biblioteca)
5. [Modos de saída](#modos-de-saída)
6. [Nível de detalhe (`--lod`)](#nível-de-detalhe---lod)
7. [Cores sobre papel branco (`--cores`)](#cores-sobre-papel-branco---cores)
8. [O que é renderizado](#o-que-é-renderizado)
9. [O que é extraído como texto](#o-que-é-extraído-como-texto)
10. [Como o enquadramento funciona](#como-o-enquadramento-funciona)
11. [Limitações conhecidas](#limitações-conhecidas)
12. [Pendências e roteiro](#pendências-e-roteiro)
13. [Solução de problemas](#solução-de-problemas)
14. [Estrutura do projeto](#estrutura-do-projeto)
15. [Testes e como validar mudanças](#testes-e-como-validar-mudanças)
16. [Contribuindo](#contribuindo)
17. [Histórico de versões](#histórico-de-versões)
18. [Licenças](#licenças)

---

## Por que existe

Ferramentas gratuitas de DWG→PDF ou exigem AutoCAD/TrueView, ou rasterizam o desenho (o texto vira imagem), ou
convertem o texto em contornos (não dá para pesquisar). Para alimentar um RAG com plantas, mapas e pranchas é
preciso o desenho fiel **e** o texto acessível. Este projeto faz as duas coisas em um só PDF, em segundos, em
qualquer máquina com .NET.

Se você precisa de fidelidade 100% ao que o AutoCAD plota (estilos de plotagem CTB/STB, fontes SHX exatas,
objetos ACIS), a resposta continua sendo o próprio AutoCAD (AccoreConsole), o Autodesk Platform Services
(Design Automation) ou um SDK comercial (ODA, Aspose.CAD). Este projeto cobre o caso comum sem custo.

## Download (sem compilar)

A cada versão (`tag v*`) a pipeline publica pacotes prontos na página **Releases** do repositório
(`https://github.com/<usuario>/<repositorio>/releases/latest`). Baixe, extraia e rode. Não precisa de conta no GitHub.

| Pacote | Precisa instalar algo? | Para quem |
|---|---|---|
| `DwgParaPdf-<versão>-win-x64-contido.zip` | Nada. Um único `DwgParaPdf.exe` com o .NET embutido (~40 MB) | Uso direto no Windows |
| `DwgParaPdf-<versão>-win-x64-com-dlls.zip` | [Runtime .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) | Quem já tem .NET; pacote pequeno |
| `DwgParaPdf-<versão>-linux-x64-contido.tar.gz` | Nada. Traz a fonte DejaVu na pasta `fontes` | Servidores e contêineres Linux |
| `DwgParaPdf-<versão>-osx-arm64-contido.tar.gz` | Nada. Traz a fonte DejaVu na pasta `fontes` | macOS Apple Silicon |

```bash
DwgParaPdf.exe "C:\desenhos\PLANTA-01.dwg"
```

```bash
chmod +x DwgParaPdf && ./DwgParaPdf ./desenhos/PLANTA-01.dwg
```

Cada execução da pipeline (push, PR, manual) também deixa os mesmos pacotes como **artefatos do workflow** na
aba Actions, por 90 dias; esses exigem login no GitHub para baixar. Para gerar uma Release:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

A pipeline está em `.github/workflows/build.yml`: testa em Windows e Linux, publica os quatro pacotes e, em tag,
cria a Release com notas geradas automaticamente.

## Instalação e compilação

Requisitos:

- [.NET SDK 10](https://dotnet.microsoft.com/download) (funciona em .NET 8 ou 9 trocando `TargetFramework` nos dois `.csproj`).
- Fontes TrueType (ver [Fontes](#fontes) abaixo).

```bash
git clone <url-do-repositorio>
cd DwgParaPdf
dotnet build DwgParaPdf.sln -c Release
```

O executável fica em `DwgParaPdf/bin/Release/net10.0/DwgParaPdf.exe` (no Linux/macOS, `DwgParaPdf` sem extensão).

Para gerar um executável único, sem depender do .NET instalado na máquina de destino:

```bash
dotnet publish DwgParaPdf/DwgParaPdf.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```

```bash
dotnet publish DwgParaPdf/DwgParaPdf.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64
```

### Fontes

PDFsharp precisa de arquivos `.ttf` para embutir o texto no PDF.

- **Windows**: nada a fazer. O conversor lê `C:\Windows\Fonts` (e as fontes por usuário) e resolve pelo nome do
  arquivo que o AutoCAD guarda no estilo de texto (`arial.ttf`, `calibri.ttf`, `arialbd.ttf`, `tahoma.ttf`...).
  Arial precisa existir (é a fonte padrão e a substituta das fontes SHX).
- **Linux / macOS / contêiner**: escolha uma destas opções, em ordem de prioridade:
  1. Variável `DWGPARAPDF_FONTE=/caminho/para/fonte.ttf`.
  2. Uma pasta `fontes` ao lado do executável com pelo menos um `.ttf` (o primeiro em ordem alfabética é usado).
  3. Caminhos comuns testados automaticamente: DejaVu Sans, Liberation Sans, Noto Sans.

  Nesses ambientes **uma única fonte** é usada para todo o texto (negrito/itálico simulados).

Sem fonte disponível a conversão falha com uma mensagem clara em vez de gerar PDF sem texto.

## Uso pela linha de comando

```
DwgParaPdf <entrada> [-o <saida.pdf>] [--modo desenho|texto|ambos] [--so-modelo] [--so-layouts]
           [--txt] [--tsv] [--sem-cotas] [--nome <arquivo.dwg>] [-v]

  <entrada>      caminho local (.dwg ou .dxf), URL http(s), ou "-" para ler o binário do stdin
  -o, --saida    caminho do PDF (padrão: mesmo nome da entrada com .pdf; URL/stdin: pasta atual)
  --modo         desenho (padrão) | texto | ambos  (ver "Modos de saída")
  --so-modelo    no modo desenho, só a página do espaço do modelo
  --so-layouts   no modo desenho, só as páginas dos layouts de papel
  --lod          nível de detalhe da geometria: alto (padrão) | medio | baixo  (ver "Nível de detalhe")
  --cores        original | texto (padrão) | texto-preto | tudo | mono  (ver "Cores sobre papel branco")
  --txt          grava também um .txt (Markdown leve) com o texto extraído em ordem de leitura
  --tsv          grava também um .tsv com cada texto e sua posição (espaço, tipo, camada, x, y, altura)
  --sem-cotas    no texto extraído, ignora entidades DIMENSION
  --nome         nome lógico do desenho quando a entrada é stdin (título do PDF e nome de saída)
  -v, --verboso  imprime os avisos do leitor/renderizador e a pilha em caso de erro
  -h, --help     ajuda
```

Exemplos:

```bash
DwgParaPdf.exe "C:\desenhos\PLANTA-01.dwg"
```

```bash
DwgParaPdf.exe https://storage.exemplo.com/desenhos/PLANTA-01.dwg -o .\saida\PLANTA-01.pdf --modo ambos --txt
```

```bash
type PLANTA-01.dwg | DwgParaPdf.exe - --nome PLANTA-01.dwg
```

Lote em PowerShell (um PDF ao lado de cada DWG, recursivo):

```bash
Get-ChildItem "C:\desenhos" -Recurse -Filter *.dwg | ForEach-Object { & ".\DwgParaPdf\bin\Release\net10.0\DwgParaPdf.exe" $_.FullName }
```

Lote em bash:

```bash
find ./desenhos -iname '*.dwg' -print0 | xargs -0 -n1 ./DwgParaPdf/bin/Release/net10.0/DwgParaPdf
```

Saída típica:

```
PDF gerado: C:\desenhos\PLANTA-01.pdf (3 página(s), 1.14 MB, modo desenho)
Formato AC1032; 78841 entidade(s) desenhada(s), 0 com erro; 1982 texto(s); 0 cota(s); 41 camada(s); 5 aviso(s); 1.1s
```

Códigos de saída: `0` sucesso, `1` erro de uso, `2` falha na conversão, `130` cancelado (Ctrl+C).

## Uso como biblioteca

Referencie o projeto `DwgParaPdf/DwgParaPdf.csproj` (ou copie a pasta sem o `Program.cs`). Tudo está no
namespace `DwgParaPdf`. A classe `ConversorDwgPdf` é thread-safe e pode ser registrada como singleton.

```csharp
using DwgParaPdf;
using DwgParaPdf.Desenho;

var conversor = new ConversorDwgPdf(
    http: httpClientOpcional,                       // para URLs; padrão interno com timeout de 2 min
    extrator: new ExtratorTextoDwg { IncluirCotas = true },
    opcoes: new OpcoesConversao
    {
        Modo = ModoSaida.Ambos,
        Desenho = new OpcoesDesenho { IncluirModelo = true, IncluirLayouts = true, AlturaTextoMinimaMm = 1.8, MargemMm = 10 },
    });

// qualquer uma das quatro formas de entrada
ResultadoConversao r1 = await conversor.ConverterAsync(@"C:\desenhos\planta.dwg", ct);
ResultadoConversao r2 = await conversor.ConverterAsync("https://.../planta.dwg", ct);
ResultadoConversao r3 = conversor.Converter(bytes, "planta.dwg");
ResultadoConversao r4 = await conversor.ConverterAsync(FonteDwg.DeStream(stream, "planta.dwg"), ct);

byte[] pdf = r1.Pdf;                  // PDF pronto para gravar/enviar
int paginas = r1.Paginas;
string texto = r1.Texto;              // texto extraído em Markdown leve (bom para indexar direto)
DocumentoExtraido d = r1.Documento;   // propriedades, camadas, espaços com textos posicionados, blocos, avisos
string tsv = d.ParaTsv();             // um texto por linha, com coordenadas
IReadOnlyList<string> avisos = d.Avisos;
```

`FonteDwg.Interpretar(string)` decide sozinho entre URL e caminho. `FonteDwg.DeBytes`, `DeStream`, `DeCaminho`
e `DeUrl` existem para quem quer ser explícito.

## Modos de saída

| Modo | Conteúdo do PDF | Uso |
|---|---|---|
| `desenho` (padrão) | Página do espaço do modelo (extensão ajustada a A4…A0) e uma página por layout de papel com conteúdo, com as viewports renderizadas e recortadas na folha declarada. É o que o AutoCAD plota. | Ver o desenho e indexar o texto que está nele |
| `texto` | Só o texto extraído, em ordem de leitura, mais propriedades do arquivo, camadas, blocos com atributos e cotas | Conteúdo textual compacto |
| `ambos` | Páginas do desenho seguidas das páginas de texto | Melhor combinação para RAG: figura fiel + texto já ordenado e agrupado |

Em qualquer modo `ResultadoConversao.Texto` e `Documento` ficam disponíveis.

## Nível de detalhe (`--lod`)

Desenhos densos (topografia com milhares de curvas de nível, hachuras de padrão em seções) geram PDFs de
dezenas ou centenas de MB. O nível de detalhe reduz a geometria **sem tocar no texto**, que é sempre gravado
integralmente (é o que interessa para busca e RAG).

| Nível | Polilinhas | Arcos | Hachuras de padrão | Coordenadas | Entidades minúsculas |
|---|---|---|---|---|---|
| `alto` (padrão) | só vértices coincidentes removidos | 1 segmento a cada 1,5 pt | até 40 mil linhas, senão preenchimento translúcido | 4 decimais | mantidas |
| `medio` | simplificadas (Douglas-Peucker) a 0,3 pt | 1 a cada 3 pt | até 8 mil linhas | 1 decimal | mantidas |
| `baixo` | simplificadas a 0,8 pt | 1 a cada 6 pt | sempre preenchimento translúcido | 1 decimal | menores que 0,8 pt somem |

A tolerância é medida em pontos **da página** (1 pt = 0,35 mm): em `medio` nada se move mais que 0,1 mm no
papel, invisível a olho e ainda fiel com zoom moderado. Hachuras sólidas, cores, espessuras, tracejados,
cotas, blocos e viewports não mudam. Use `--so-layouts` junto quando as pranchas já mostram tudo que importa:
a página do modelo costuma duplicar a geometria.

## Cores sobre papel branco (`--cores`)

O AutoCAD desenha em fundo escuro, então amarelo, ciano e verde-claro são comuns e somem no papel branco.

| Modo | Efeito |
|---|---|
| `original` | Cores do arquivo; só branco/índice 7 vira preto |
| `texto` (padrão) | Textos claros demais (luminância > 0,45) são escurecidos **mantendo o matiz**: amarelo vira oliva, ciano vira petróleo, cinza claro vira cinza escuro. Linhas, hachuras e legendas ficam como estão |
| `texto-preto` | Todo texto preto; geometria intacta |
| `tudo` | O mesmo escurecimento de `texto` aplicado também à geometria |
| `mono` | Tudo preto, equivalente ao `monochrome.ctb` do AutoCAD |

Para RAG e leitura, `texto` costuma bastar; `mono` é a escolha para impressão em preto e branco.

## O que é renderizado

| Entidade | Como |
|---|---|
| LINE, LWPOLYLINE, POLYLINE 2D/3D | Traço; arcos por *bulge*; largura constante/por vértice vira espessura da caneta |
| ARC, CIRCLE, ELLIPSE | Poligonalizados com passo proporcional ao tamanho na página (suave em qualquer zoom razoável) |
| SPLINE | Poligonalizada pela biblioteca; recai em pontos de ajuste/controle se falhar |
| HATCH | Sólida: preenchimento par-ímpar (ilhas ok). Padrão: linhas do padrão explodidas e recortadas; acima de 40 mil linhas vira preenchimento translúcido na cor da hachura |
| SOLID, 3DFACE, POINT | Polígono preenchido, contorno, ponto de 0,7 pt |
| TEXT | Texto real; altura, rotação, fator de largura, oblíquo, alinhamentos (esquerda/centro/direita/meio/ajustado/fit) e vertical (base/baixo/meio/topo) |
| MTEXT | Texto real; códigos de formatação removidos; quebra pela largura da caixa; ponto de fixação (9 posições); rotação por ângulo ou vetor de direção; espaçamento de linha |
| ATTRIB | Como TEXT (invisíveis/ocultos não saem) |
| INSERT | Blocos aninhados com transformação completa (escala X/Y, rotação, espelhamento), MINSERT (linhas × colunas), cor/espessura/tipo de linha ByBlock e regra da camada 0 |
| DIMENSION | Pelo bloco anônimo da cota (linhas, setas e texto) |
| LEADER, MLEADER | Linhas de chamada e texto do MLEADER |
| TABLE | Pelo bloco gráfico da tabela |
| VIEWPORT (em layouts) | Modelo recortado ao retângulo ou ao contorno poligonal; alvo da vista (`ViewTarget`), centro, escala e *twist*; camadas congeladas por viewport; borda só se a camada da viewport for plotável |
| WIPEOUT | Máscara branca na ordem de desenho do arquivo (esconde o que foi desenhado antes) |

Propriedades: cores ACI e true color (só o índice 7 plota preto; branco true color e índice 255 ficam brancos,
porque são as máscaras que escondem o que está atrás); espessuras de
linha em mm (ByLayer/ByBlock/padrão 0,25 mm); tipos de linha tracejados com LTSCALE e escala da entidade;
camadas desligadas, congeladas, não plotáveis e `Defpoints` não saem; fontes TrueType instaladas pelo nome do
arquivo do estilo, negrito/itálico pelos *flags* do estilo; fontes SHX (simplex, romans, txt, isocp) saem em
**Arial Narrow**, cuja largura de caractere é próxima da original (Arial faria os blocos de notas quebrarem em
mais linhas e invadirem o carimbo). Uma linha de MTEXT até 15 % mais larga que a caixa é comprimida em vez de
quebrada, porque no AutoCAD ela cabia em uma linha.

## O que é extraído como texto

TEXT, MTEXT (`\P`, `\f…;`, `\H…;`, `\C…;`, `{}`, `\S1^2;` → `1/2`, `%%c` → Ø, `%%d` → °, `%%p` → ±, `\U+00E7` → ç),
ATTRIB (no fluxo de leitura e em seção própria `BLOCO [espaço]: TAG = valor; …`), textos dentro de blocos
aninhados com a posição transformada, MLEADER, células de TABLE, cotas (valor ou texto sobrescrito, agrupadas
por frequência), propriedades do arquivo (título, assunto, autor, palavras-chave, comentários, datas,
customizadas) e nomes de camadas.

Ordem de leitura: de cima para baixo, da esquerda para a direita. Textos na mesma faixa vertical (tolerância
0,6 × altura mediana) são unidos com ` | `; lacunas horizontais maiores que 30 alturas quebram a linha; MTEXT
multi-linha vira parágrafo próprio. Duplicatas no mesmo ponto são descartadas.

## Como o enquadramento funciona

**Página do modelo.** União das caixas das entidades visíveis, com estimativa da área ocupada pelos textos
(a biblioteca não calcula caixa de texto). Entidades perdidas longe do desenho são descartadas: calcula-se o
núcleo (percentis 2–98 dos centros) e só permanece o que cai a até uma largura de núcleo dele, desde que o
descarte seja minoria (< 10 %). A folha vai de A4 a A0, orientada pela extensão, a menor em que o texto mediano
fique com pelo menos `AlturaTextoMinimaMm` (1,8 mm) no papel. Acima de A0 os visualizadores ficam lentos sem
ganho real, já que o PDF é vetorial.

**Layouts.** A folha é a declarada nas configurações de plotagem (`PaperWidth`/`PaperHeight` em mm, com a
rotação). As unidades do layout vêm de `PaperUnits` (mm ou polegadas). A região enquadrada segue a ordem de
confiança: janela de plotagem (`PlotType = Window`), extensão salva do layout pelo AutoCAD, e por último as
caixas das entidades visíveis (MULTILEADER e WIPEOUT ilegíveis trazem caixas erradas). Se o tipo de plotagem é
"layout" e o conteúdo cabe em 1:1, ele é posicionado pela margem imprimível; caso contrário o conteúdo é ajustado
à folha, como o AutoCAD faz com "ajustar ao papel". Layouts sem entidades e sem viewport ativa são omitidos.
Folhas não declaradas ou absurdas (> 6 m) caem em A1.

## Limitações conhecidas

Renderização:

- **Sistema de coordenadas do objeto (OCS/extrusão)** não é aplicado: entidades com `Normal` diferente de +Z
  (ex.: arcos/círculos espelhados com normal −Z, blocos rotacionados em 3D) saem no lugar errado.
- **REGION**, sólidos 3D, malhas e superfícies (geometria ACIS) não têm representação disponível: ignorados.
- **Imagens raster** e **OLE** não saem (por escolha: PDF só vetorial).
- **MULTILEADER** de alguns arquivos AutoCAD 2013 (AC1027) não é lido pela biblioteca ("Could not read
  MULTILEADER"): esses textos de chamada somem. Aparece nos avisos.
- **Fontes SHX** (romans, simplex, isocp…) viram Arial Narrow: aparência próxima, não idêntica. Fontes SHX
  grandes (asiáticas) não são tratadas.
- **MTEXT** perde formatação interna: uma só fonte e cor por entidade; frações empilhadas saem como `a/b`;
  sublinhado, sobrelinha, campos (`%<\AcVar…>%`), colunas e fundo (máscara) não são reproduzidos.
- **Estilos de plotagem** (CTB/STB) não são aplicados; `--cores` cobre os casos comuns (escurecer claros, mono).
- **Tipos de linha complexos** (com formas ou texto embutido) saem só com os traços; `PSLTSCALE` é ignorado.
- **Setas** de LEADER/MLEADER e o bloco de conteúdo do MLEADER não são desenhados.
- **Ordem de desenho** (`SORTENTS`) é ignorada: a ordem é a do arquivo. Em layouts as entidades do papel são
  sempre desenhadas por cima das viewports.
- **Viewports**: vistas 3D são renderizadas em planta (aviso emitido); sobreposições de propriedade de camada
  por viewport (cor/tipo de linha diferentes por viewport) não se aplicam; o sinal do *twist* não foi validado
  com arquivo real (alvo da vista e centro foram validados).
- **Blocos dinâmicos**: o estado de visibilidade atual não é avaliado (todas as entidades do bloco anônimo são
  desenhadas, o que costuma coincidir com o esperado).
- **XREFs** não são resolvidas (o arquivo referenciado não é carregado).
- Espessuras de linha são absolutas em mm, como na plotagem; em folhas grandes as linhas parecem finas.

Extração de texto:

- Ordem de leitura é heurística; tabelas desenhadas com linhas soltas podem ter células misturadas.
- Texto "explodido" em linhas e texto dentro de imagens não é recuperável.

Formato e ambiente:

- DWG anteriores ao R13 (AC1012) não são suportados pela biblioteca de leitura.
- A poligonalização de arcos/splines segue o tamanho na página; zoom extremo mostra facetas.
- Arquivos muito grandes (centenas de milhares de entidades) geram PDFs de dezenas de MB; visualizadores
  baseados em PDFium (Chrome/Edge) levam até um minuto para pintar páginas A0 densas. Acrobat/Foxit abrem rápido.
- Em Linux uma única fonte TrueType é usada para todo o texto.
- Tudo é processado em memória (arquivo inteiro + PDF); não há streaming.

## Pendências e roteiro

Em ordem aproximada de valor/esforço. Contribuições bem-vindas.

1. **OCS/extrusão**: aplicar a matriz do vetor normal (Arbitrary Axis Algorithm) nas entidades 2D antes de renderizar.
2. **Leitura de CTB/STB** (cor → cor/espessura da tabela de plotagem).
3. **MTEXT rico**: mudanças de fonte/cor inline, frações empilhadas de verdade, sublinhado, campos, colunas, máscara de fundo.
4. **Mapa de fontes configurável** (ex.: `romans.shx → Liberation Sans Narrow` no Linux) e suporte a `.otf`/`.ttc`.
5. **MULTILEADER AC1027**: contornar a falha de leitura da biblioteca (ou contribuir a correção no ACadSharp).
6. **Sobreposições de camada por viewport** (VPLAYER) e tipos de linha com formas.
7. **XREF**: resolver caminhos relativos e carregar arquivos referenciados.
8. **Opções de página**: folha e escala explícitas para o modelo; desligar o filtro de outliers; margem por opção.
9. **Setas** de LEADER/MLEADER e bloco de conteúdo do MLEADER.
10. **Ordem de desenho** (`SORTENTS`) e *draw order* entre viewports e papel.
11. **Empacotamento**: pacote NuGet da biblioteca e imagem Docker com fontes livres (a pipeline de build/testes/Release já existe).
12. **Desempenho**: renderização paralela por página, redução de tamanho do PDF (reuso de padrões de hachura como *tiling patterns*).
13. **Fixtures reais**: pasta com DWGs públicos e testes de regressão comparando rasterizações (PyMuPDF) por *hash* perceptual.
14. **Camada de texto para RAG**: opção de anexar o texto em ordem de leitura como metadados/anotações em vez de páginas extras.

## Solução de problemas

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| `Nenhuma fonte TrueType encontrada` | Linux/contêiner sem `.ttf` acessível | Defina `DWGPARAPDF_FONTE` ou crie a pasta `fontes` ao lado do executável |
| `Não foi possível ler 'x.dwg' como Dwg` | Arquivo corrompido, anterior ao R13 ou não é DWG | Abra no AutoCAD/TrueView e salve como DWG 2018 ou DXF; tente a extensão certa |
| Texto cortado na borda da página do modelo | Texto muito largo em fonte SHX estimado a menos | Aumente `MargemMm`; abra uma *issue* com o arquivo |
| Página do modelo quase vazia com desenho minúsculo | Entidade perdida longe do desenho que o filtro não descartou (> 10 % das entidades, ou dentro de uma largura de núcleo) | Apague/afaste a entidade no CAD; ou gere só os layouts com `--so-layouts` |
| Layout aparece deslocado ou fora da folha | Configuração de plotagem inconsistente no arquivo | Rode com `-v`: o aviso indica se a folha foi ajustada; use `--so-modelo` se preferir |
| Aviso `Tipo de entidade não desenhado: REGION` | Geometria ACIS sem representação | Sem solução no momento (ver roteiro); no CAD, exploda a região em linhas |
| Aviso `Fonte 'xyz' indisponível; usando Arial` | Fonte do estilo não instalada | Instale a fonte no Windows ou aceite Arial |
| PDF demora a abrir no Chrome/Edge | Página A0 com dezenas de milhares de entidades | Abra no Acrobat/Foxit/SumatraPDF, ou gere `--so-layouts` |
| Muitos avisos `Unlisted object … UnknownNonGraphicalObject` | Objetos de verticais (Civil 3D, Architecture) que a biblioteca não conhece | Normal; não afetam a saída |

Para investigar dimensionamento de folha/viewports, rode o teste de diagnóstico apontando para o arquivo:

```bash
DWGPARAPDF_DIAG="C:\desenhos\PLANTA-01.dwg" dotnet test DwgParaPdf.sln -c Release --filter FullyQualifiedName~DiagnosticoLayoutsTestes
```

Ele grava `PLANTA-01.layouts.txt` ao lado do DWG com folhas, viewports, extensões e as maiores caixas de texto estimadas.

## Estrutura do projeto

```
DwgParaPdf.sln
DwgParaPdf/                      biblioteca + CLI (namespace DwgParaPdf)
  Program.cs                     linha de comando
  ConversorDwgPdf.cs             fachada: lê uma vez, extrai texto, gera PDF no modo pedido
  FonteDwg.cs                    origem do desenho (caminho, URL, bytes, stream)
  LeitorCad.cs                   abre DWG/DXF detectando pelo conteúdo, modo tolerante, coleta avisos
  ExtratorTextoDwg.cs            coleta TEXT/MTEXT/ATTRIB/MLEADER/TABLE/DIMENSION com posição
  LimpadorTextoCad.cs            remove códigos MTEXT e sequências %%
  OrdenadorLeitura.cs            agrupa textos em linhas de leitura
  GeradorPdf.cs                  PDF só-texto (MigraDoc) e resolução de fontes (Windows/Linux)
  Modelos.cs                     DocumentoExtraido, TextoCad, ResultadoConversao…
  Desenho/
    Afim2D.cs                    transformação afim 2D (mundo/bloco/papel → página)
    RenderizadorEntidades.cs     desenha cada entidade em XGraphics; cores, espessuras, tracejados, texto
    GeradorPdfDesenho.cs         páginas: extensão do modelo, folha dos layouts, viewports com recorte
DwgParaPdf.Testes/               xunit
  AmostraDwg.cs                  monta um DWG sintético com todos os tipos tratados
  ConversaoTestes.cs             leitura, extração, formas de entrada, DXF, URL local
  DesenhoTestes.cs               modo desenho, páginas, filtros, Afim2D
  LimpadorTextoCadTestes.cs      códigos MTEXT e %%
  DiagnosticoLayoutsTestes.cs    ferramenta de apoio (ativa por variável de ambiente)
```

Fluxo: `FonteDwg` → `LeitorCad` → (`ExtratorTextoDwg` → `OrdenadorLeitura`) e/ou (`GeradorPdfDesenho` →
`RenderizadorEntidades`) → `GeradorPdf` (modo texto/ambos) → `ResultadoConversao`.

## Testes e como validar mudanças

```bash
dotnet test DwgParaPdf.sln -c Release
```

Os testes geram um DWG sintético (TEXT, MTEXT com códigos, bloco com atributos, cota, linha, círculo, arco,
elipse, polilinha com arco, sólido, layout) nas versões AC1015, AC1018, AC1024 e AC1032, mais DXF ASCII e
binário, e cobrem as quatro formas de entrada (caminho, URL via `HttpListener` local, bytes, stream), os três
modos e os filtros de página. O arquivo `amostra.dwg` fica ao lado da DLL de testes para rodar a CLI à mão.

Precisa de DWGs reais para testar? O [Bibliocad](https://www.bibliocad.com/pt/dwg/) tem milhares de desenhos
gratuitos para baixar (plantas, mapas, blocos, mecânica) em versões variadas do AutoCAD. Baixe alguns, rode a
CLI com `-v` e confira os avisos.

Para validar visualmente com arquivos reais, converta e rasterize as páginas (Python + PyMuPDF):

```bash
pip install pymupdf
```

```python
import fitz, sys
doc = fitz.open(sys.argv[1])
for i, pagina in enumerate(doc):
    zoom = min(1.0, 1600 / pagina.rect.width)
    pagina.get_pixmap(matrix=fitz.Matrix(zoom, zoom), alpha=False).save(f"pagina_{i+1}.png")
```

Compare com a visualização do AutoCAD/TrueView ou com o PDF plotado por ele.

O projeto foi validado com oito desenhos públicos de AutoCAD (mapas cadastrais e de mobilidade urbana, formatos
de prancha A1–A5, blocos de vegetação, desenho mecânico), de 0,1 a 19 MB, e com 76 pranchas de engenharia
(AutoCAD 2013, 0,8 a 48 MB, layouts de 1050 × 594 mm plotados por janela, topografia densa, hachuras de seção).
O maior mapa (211 mil textos, 639 mil entidades) leva cerca de 25 s; uma prancha de 30 MB leva 10 a 40 s e gera
30 a 100 MB em `--lod alto`, 1 a 9 MB em `--lod medio`. Pico de memória observado: 3,3 GB por processo.

## Contribuindo

- Abra uma *issue* com o DWG (ou um trecho dele salvo como DXF) sempre que algo sair diferente do AutoCAD.
- Mantenha os testes verdes e adicione um caso na `AmostraDwg` quando tratar um tipo novo de entidade.
- Antes de mexer em enquadramento ou layouts, leia a seção [Como o enquadramento funciona](#como-o-enquadramento-funciona)
  e os detalhes abaixo.

Detalhes que custaram caro (para quem for mexer no código):

- A viewport `id=1` de cada layout **não é a folha**: é a janela do espaço do papel na tela do AutoCAD (pode ter
  metros). A folha vem de `PaperWidth`/`PaperHeight`/`PaperRotation`; as unidades do layout vêm de `PaperUnits`.
- `Viewport.ViewCenter` é dado no sistema de exibição, cuja origem é `ViewTarget` (em WCS). Ignorar o alvo deixa a
  viewport vazia quando o desenho está em coordenadas UTM.
- A extensão de um layout pelas entidades é traiçoeira; `PlotType = Window` e `Layout.MinExtents/MaxExtents` são
  o que o AutoCAD realmente plota.
- `TextEntity.GetBoundingBox()` lança exceção no ACadSharp 3.7.1; a caixa dos textos é estimada aqui.
- Arquivos reais trazem `MText.RectangleWidth = 2e-10`: largura de quebra menor que um caractere é lixo.
- `Arc.CreateFromBulge` com vértices coincidentes produz NaN ou raio zero: bulge degenerado vira segmento reto.
- A extensão salva no cabeçalho (`$EXTMIN/$EXTMAX`) inclui entidades perdidas; não a use para enquadrar.
- Textos em `Insert.Attributes` já vêm em coordenadas do espaço, não do bloco.
- `LwPolyline.ConstantWidth` pode vir corrompido em arquivos AutoCAD 2013 (largura 75 numa polilinha de 0,4 unidades);
  com pontas arredondadas isso vira um disco enorme. Limite a largura pela extensão da geometria.
- `TableEntity` herda de `Insert` e `AttributeEntity` de `TextEntity`: a ordem dos `case` importa.
- PDFsharp 6.2 (build Core) não resolve fontes sozinho; `TextStyle.TrueType` é um enum de *flags*, não o nome da fonte.

## Histórico de versões

**1.1.1**
- Polilinha com largura maior que 2× a própria extensão (leitura corrompida: "donut" de 0,4 unidades com largura 75) virava um disco gigante na prancha; a largura agora é limitada a 2× a extensão.
- Branco true color e índice 255 deixam de virar preto: são as máscaras de hachura sólida que escondem curvas de nível e apareciam como blocos pretos. Só a cor índice 7 plota preto, como no AutoCAD.
- Diagnóstico: modo pasta (`DWGPARAPDF_DIAG=<pasta>`) conta larguras suspeitas por arquivo; despejo de hachuras com arestas curvas e de polilinhas mais largas.

**1.1.0**
- `--lod alto|medio|baixo`: simplificação da geometria (Douglas-Peucker, arcos, hachuras, arredondamento) sem tocar no texto; pranchas densas caem de 30–100 MB para 1–9 MB em `medio`.
- `--cores original|texto|texto-preto|tudo|mono`: textos claros (amarelo, ciano) legíveis sobre o papel branco; padrão `texto`.
- Viewports com alvo de vista (`ViewTarget`) fora da origem passaram a renderizar (antes saíam vazias em desenhos UTM).
- Layouts enquadrados pela janela de plotagem ou pela extensão salva do layout, não pelas entidades.
- WIPEOUT renderizado como máscara branca.
- Fontes SHX mapeadas para Arial Narrow e MTEXT com compressão de até 15 % em vez de quebra: blocos de notas não invadem mais o carimbo.
- Página do modelo segue a proporção do conteúdo (sem faixas em branco em desenhos alongados).
- Teste de diagnóstico de layouts (`DWGPARAPDF_DIAG`) e 42 testes automatizados.

**1.0.0**
- Primeira versão: DWG/DXF para PDF vetorial (modelo + layouts com viewports), extração de texto em ordem de leitura, modos `desenho`, `texto` e `ambos`, CLI e biblioteca, pipeline com Releases para Windows, Linux e macOS.

## Licenças

Dependências: ACadSharp (MIT), PDFsharp e MigraDoc (MIT), xunit (Apache 2.0). Fontes TrueType não são
redistribuídas por este projeto; em Linux use fontes livres (DejaVu, Liberation, Noto) ou as que você tiver
licença para usar. Defina a licença do próprio projeto no arquivo `LICENSE` antes de publicar (sugestão: MIT).

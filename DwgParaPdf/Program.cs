using System.Text;
using DwgParaPdf;
using DwgParaPdf.Desenho;

Console.OutputEncoding = Encoding.UTF8;
return await Cli.ExecutarAsync(args);

internal static class Cli
{
    private const string Ajuda = """
        DwgParaPdf - converte DWG/DXF em PDF vetorial (geometria + texto selecionável), sem rasterizar.

        Uso:
          DwgParaPdf <entrada> [-o <saida.pdf>] [--modo desenho|texto|ambos] [--so-modelo] [--so-layouts]
                     [--txt] [--tsv] [--sem-cotas] [--nome <arquivo.dwg>] [-v]

          <entrada>      caminho local (.dwg ou .dxf), URL http(s), ou "-" para ler o binário do stdin
          -o, --saida    caminho do PDF (padrão: mesmo nome da entrada com .pdf; URL/stdin: pasta atual)
          --modo         desenho (padrão): páginas fiéis ao desenho (modelo + layouts com viewports)
                         texto: só o texto extraído, em ordem de leitura
                         ambos: páginas do desenho seguidas das páginas de texto
          --so-modelo    no modo desenho, só a página do espaço do modelo
          --so-layouts   no modo desenho, só as páginas dos layouts de papel
          --txt          grava também um .txt (Markdown leve) com o texto extraído
          --tsv          grava também um .tsv com cada texto e sua posição (espaço, tipo, camada, x, y, altura)
          --sem-cotas    no texto extraído, ignora entidades DIMENSION
          --nome         nome lógico do desenho quando a entrada é stdin (título do PDF e nome de saída)
          -v, --verboso  imprime os avisos e a pilha em caso de erro
          -h, --help     esta ajuda

        Exemplos:
          DwgParaPdf "C:\desenhos\PLANTA-01.dwg"
          DwgParaPdf https://storage.exemplo.com/desenhos/PLANTA-01.dwg -o .\saida\PLANTA-01.pdf --modo ambos
          type desenho.dwg | DwgParaPdf - --nome desenho.dwg --txt
        """;

    public static async Task<int> ExecutarAsync(string[] args)
    {
        string? entrada = null, saida = null, nome = null;
        var modo = ModoSaida.Desenho;
        var soModelo = false; var soLayouts = false;
        var gravarTxt = false; var gravarTsv = false; var semCotas = false; var verboso = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    Console.WriteLine(Ajuda);
                    return 0;
                case "-o" or "--saida":
                    if (++i >= args.Length) return Uso("Falta o valor de -o/--saida.");
                    saida = args[i];
                    break;
                case "--nome":
                    if (++i >= args.Length) return Uso("Falta o valor de --nome.");
                    nome = args[i];
                    break;
                case "--modo":
                    if (++i >= args.Length) return Uso("Falta o valor de --modo.");
                    if (!Enum.TryParse(args[i], ignoreCase: true, out modo)) return Uso($"Modo inválido: {args[i]} (use desenho, texto ou ambos).");
                    break;
                case "--so-modelo": soModelo = true; break;
                case "--so-layouts": soLayouts = true; break;
                case "--txt": gravarTxt = true; break;
                case "--tsv": gravarTsv = true; break;
                case "--sem-cotas": semCotas = true; break;
                case "-v" or "--verboso": verboso = true; break;
                default:
                    if (args[i].StartsWith('-') && args[i] != "-") return Uso($"Opção desconhecida: {args[i]}");
                    if (entrada is not null) return Uso("Informe uma única entrada.");
                    entrada = args[i];
                    break;
            }
        }

        if (entrada is null) return Uso("Informe a entrada.");
        if (soModelo && soLayouts) return Uso("--so-modelo e --so-layouts são excludentes.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var conversor = new ConversorDwgPdf(
                extrator: new ExtratorTextoDwg { IncluirCotas = !semCotas },
                opcoes: new OpcoesConversao
                {
                    Modo = modo,
                    Desenho = new OpcoesDesenho { IncluirModelo = !soLayouts, IncluirLayouts = !soModelo },
                });

            var fonte = entrada == "-"
                ? FonteDwg.DeBytes(await LerStdinAsync(cts.Token), nome ?? "stdin.dwg")
                : FonteDwg.Interpretar(entrada);

            var inicio = DateTime.UtcNow;
            var resultado = await conversor.ConverterAsync(fonte, cts.Token);

            saida ??= fonte.Tipo == FonteDwg.TipoFonte.Caminho
                ? Path.ChangeExtension(fonte.Caminho!, ".pdf")
                : Path.Combine(Environment.CurrentDirectory, resultado.NomeSugeridoPdf);
            saida = Path.GetFullPath(saida);
            Directory.CreateDirectory(Path.GetDirectoryName(saida)!);

            await File.WriteAllBytesAsync(saida, resultado.Pdf, cts.Token);
            if (gravarTxt)
                await File.WriteAllTextAsync(Path.ChangeExtension(saida, ".txt"), resultado.Texto, new UTF8Encoding(false), cts.Token);
            if (gravarTsv)
                await File.WriteAllTextAsync(Path.ChangeExtension(saida, ".tsv"), resultado.Documento.ParaTsv(), new UTF8Encoding(false), cts.Token);

            var d = resultado.Documento;
            Console.WriteLine($"PDF gerado: {saida} ({resultado.Paginas} página(s), {resultado.Pdf.Length / 1024.0 / 1024.0:0.00} MB, modo {modo.ToString().ToLowerInvariant()})");
            if (gravarTxt) Console.WriteLine($"TXT gerado: {Path.ChangeExtension(saida, ".txt")}");
            if (gravarTsv) Console.WriteLine($"TSV gerado: {Path.ChangeExtension(saida, ".tsv")}");
            Console.WriteLine($"Formato {d.Versao}; {resultado.EntidadesDesenhadas} entidade(s) desenhada(s), {resultado.EntidadesComErro} com erro; " +
                              $"{d.TotalTextos} texto(s); {d.TotalCotas} cota(s); {d.Camadas.Count} camada(s); {d.Avisos.Count} aviso(s); {(DateTime.UtcNow - inicio).TotalSeconds:0.0}s");

            if (verboso)
                foreach (var aviso in d.Avisos) Console.WriteLine($"  aviso: {aviso}");

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelado.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Erro: {ex.Message}");
            if (verboso) Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static int Uso(string mensagem)
    {
        Console.Error.WriteLine(mensagem);
        Console.Error.WriteLine();
        Console.Error.WriteLine(Ajuda);
        return 1;
    }

    private static async Task<byte[]> LerStdinAsync(CancellationToken ct)
    {
        using var stdin = Console.OpenStandardInput();
        using var ms = new MemoryStream();
        await stdin.CopyToAsync(ms, ct);
        if (ms.Length == 0) throw new InvalidDataException("Nada foi lido do stdin.");
        return ms.ToArray();
    }
}

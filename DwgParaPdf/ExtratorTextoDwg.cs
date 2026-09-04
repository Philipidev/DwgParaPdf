using System.Globalization;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

namespace DwgParaPdf;

/// <summary>
/// Lê um DWG/DXF com ACadSharp e coleta todo texto legível: TEXT, MTEXT, atributos de bloco (inclusive
/// blocos aninhados, com a posição transformada para o espaço), MLEADER, tabelas e cotas.
/// </summary>
public sealed class ExtratorTextoDwg
{
    /// <summary>Quantos níveis de bloco dentro de bloco seguir.</summary>
    public int ProfundidadeMaximaBlocos { get; init; } = 8;

    /// <summary>Se falso, ignora entidades DIMENSION.</summary>
    public bool IncluirCotas { get; init; } = true;

    /// <summary>Limite de avisos do leitor guardados no resultado.</summary>
    public int MaximoAvisos { get; init; } = 50;

    private sealed class Contexto
    {
        public required DocumentoExtraido Documento { get; init; }
        public required EspacoExtraido Espaco { get; init; }
        public List<Transform> Transformacoes { get; } = new();
        public HashSet<string> BlocosAbertos { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string Texto, long X, long Y)> Vistos { get; } = new();
        public int Profundidade;
    }

    public DocumentoExtraido Extrair(byte[] conteudo, string nomeOrigem)
    {
        var avisos = new List<string>();
        var doc = LeitorCad.Ler(conteudo, nomeOrigem, avisos);
        return Extrair(doc, nomeOrigem, avisos);
    }

    /// <summary>Extrai de um documento já lido. <paramref name="avisosLeitura"/> são os avisos do leitor, incorporados ao resultado.</summary>
    public DocumentoExtraido Extrair(CadDocument doc, string nomeOrigem, List<string>? avisosLeitura = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var resultado = new DocumentoExtraido { NomeOrigem = nomeOrigem };
        var avisos = new List<string>(avisosLeitura ?? new List<string>());

        resultado.Versao = doc.Header?.Version.ToString();
        PreencherPropriedades(doc, resultado, avisos);

        resultado.Camadas.AddRange(doc.Layers
            .Select(l => l.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        var modelo = new EspacoExtraido { Nome = "Model", EhModelo = true };
        Coletar(new Contexto { Documento = resultado, Espaco = modelo }, doc.ModelSpace.Entities, avisos);
        OrdenadorLeitura.Ordenar(modelo);
        resultado.Espacos.Add(modelo);

        foreach (var layout in doc.Layouts.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            var bloco = layout.AssociatedBlock;
            if (bloco is null || ReferenceEquals(bloco, doc.ModelSpace) || !layout.IsPaperSpace) continue;

            var espaco = new EspacoExtraido { Nome = layout.Name };
            Coletar(new Contexto { Documento = resultado, Espaco = espaco }, bloco.Entities, avisos);
            OrdenadorLeitura.Ordenar(espaco);
            resultado.Espacos.Add(espaco);
        }

        // o leitor repete o mesmo aviso para cada objeto; agrupa por mensagem
        var agrupados = avisos
            .GroupBy(a => a, StringComparer.Ordinal)
            .Select(g => g.Count() > 1 ? $"{g.Key} (x{g.Count()})" : g.Key)
            .ToList();
        if (agrupados.Count > MaximoAvisos)
        {
            var omitidos = agrupados.Count - MaximoAvisos;
            agrupados.RemoveRange(MaximoAvisos, omitidos);
            agrupados.Add($"... e mais {omitidos} aviso(s) distinto(s) omitido(s).");
        }
        resultado.Avisos.AddRange(agrupados);

        return resultado;
    }

    // ------------------------------------------------------------------ propriedades

    private static void PreencherPropriedades(CadDocument doc, DocumentoExtraido resultado, List<string> avisos)
    {
        try
        {
            var info = doc.SummaryInfo;
            if (info is null) return;

            Adicionar("Título", info.Title);
            Adicionar("Assunto", info.Subject);
            Adicionar("Autor", info.Author);
            Adicionar("Palavras-chave", info.Keywords);
            Adicionar("Comentários", info.Comments);
            Adicionar("Salvo por", info.LastSavedBy);
            Adicionar("Revisão", info.RevisionNumber);
            Adicionar("Base de hyperlink", info.HyperlinkBase);
            if (info.CreatedDate != default) Adicionar("Criado em", info.CreatedDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            if (info.ModifiedDate != default) Adicionar("Modificado em", info.ModifiedDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

            foreach (var par in info.Properties)
                Adicionar(par.Key, par.Value);
        }
        catch (Exception ex)
        {
            avisos.Add($"Propriedades do arquivo não lidas: {ex.Message}");
        }

        void Adicionar(string chave, string? valor)
        {
            var limpo = LimpadorTextoCad.LimparMText(valor);
            if (limpo.Length > 0 && !string.IsNullOrWhiteSpace(chave))
                resultado.Propriedades[chave] = limpo.Replace('\n', ' ');
        }
    }

    // ------------------------------------------------------------------ coleta

    private void Coletar(Contexto ctx, IEnumerable<Entity> entidades, List<string> avisos)
    {
        foreach (var entidade in entidades)
        {
            try
            {
                ColetarEntidade(ctx, entidade, avisos);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                avisos.Add($"Entidade {entidade.ObjectName} ignorada ({ctx.Espaco.Nome}): {ex.Message}");
            }
        }
    }

    private void ColetarEntidade(Contexto ctx, Entity entidade, List<string> avisos)
    {
        switch (entidade)
        {
            case AttributeDefinition:
                // definição dentro do bloco: guarda prompt/valor padrão, não o dado do desenho
                return;

            case AttributeEntity atributo:
                Adicionar(ctx, "ATTRIB", atributo, LimpadorTextoCad.LimparTexto(atributo.Value), atributo.InsertPoint, atributo.Height);
                return;

            case TextEntity texto:
                Adicionar(ctx, "TEXT", texto, LimpadorTextoCad.LimparTexto(texto.Value), texto.InsertPoint, texto.Height);
                return;

            case MText mtexto:
                Adicionar(ctx, "MTEXT", mtexto, LimpadorTextoCad.LimparMText(mtexto.Value), mtexto.InsertPoint, mtexto.Height);
                return;

            case TableEntity tabela:
                if (!ColetarTabela(ctx, tabela))
                    ColetarInsercao(ctx, tabela, avisos); // sem células legíveis: usa o bloco gráfico da tabela
                return;

            case Insert insercao:
                ColetarInsercao(ctx, insercao, avisos);
                return;

            case Dimension cota:
                if (!IncluirCotas) return;
                var textoCota = TextoDaCota(cota);
                if (!string.IsNullOrWhiteSpace(textoCota))
                    ctx.Espaco.Cotas.Add(textoCota.Replace('\n', ' '));
                return;

            case MultiLeader lider:
                var dados = lider.ContextData;
                if (dados is { HasTextContents: true } && !string.IsNullOrWhiteSpace(dados.TextLabel))
                    Adicionar(ctx, "MLEADER", lider, LimpadorTextoCad.LimparMText(dados.TextLabel), dados.TextLocation, dados.TextHeight);
                return;
        }
    }

    private void ColetarInsercao(Contexto ctx, Insert insercao, List<string> avisos)
    {
        var bloco = insercao.Block;
        var nomeBloco = bloco?.Name ?? "(sem bloco)";

        // ATTRIB já vem em coordenadas do espaço que contém o INSERT (não do bloco)
        var registro = new BlocoComAtributos { Bloco = nomeBloco, Espaco = ctx.Espaco.Nome };
        foreach (var atributo in insercao.Attributes)
        {
            var valor = LimpadorTextoCad.LimparTexto(atributo.Value);
            if (valor.Length == 0) continue;
            registro.Atributos.Add((atributo.Tag ?? string.Empty, valor));
            Adicionar(ctx, "ATTRIB", atributo, valor, atributo.InsertPoint, atributo.Height);
        }
        if (registro.Atributos.Count > 0)
            ctx.Documento.Blocos.Add(registro);

        if (bloco is null) return;

        if (ctx.Profundidade >= ProfundidadeMaximaBlocos)
        {
            avisos.Add($"Bloco '{nomeBloco}' além da profundidade máxima ({ProfundidadeMaximaBlocos}); conteúdo interno ignorado.");
            return;
        }
        if (!ctx.BlocosAbertos.Add(nomeBloco))
            return; // referência circular

        ctx.Transformacoes.Add(insercao.GetTransform());
        ctx.Profundidade++;
        try
        {
            Coletar(ctx, bloco.Entities, avisos);
        }
        finally
        {
            ctx.Profundidade--;
            ctx.Transformacoes.RemoveAt(ctx.Transformacoes.Count - 1);
            ctx.BlocosAbertos.Remove(nomeBloco);
        }
    }

    private static bool ColetarTabela(Contexto ctx, TableEntity tabela)
    {
        var y = tabela.InsertPoint.Y;
        var encontrou = false;

        foreach (var linha in tabela.Rows)
        {
            var celulas = new List<string>();
            foreach (var celula in linha.Cells)
            {
                var valor = ValorDaCelula(celula);
                if (valor.Length > 0) celulas.Add(valor);
            }

            if (celulas.Count > 0)
            {
                encontrou = true;
                Adicionar(ctx, "TABLE", tabela, string.Join(OrdenadorLeitura.SeparadorNaLinha, celulas),
                    new XYZ(tabela.InsertPoint.X, y, tabela.InsertPoint.Z), linha.Height);
            }
            y -= linha.Height;
        }

        return encontrou;
    }

    private static string ValorDaCelula(TableEntity.Cell celula)
    {
        var partes = new List<string>();
        foreach (var conteudo in celula.Contents)
        {
            var valor = conteudo.CadValue;
            if (valor is null || valor.IsEmpty) continue;
            var texto = LimpadorTextoCad.LimparMText(valor.FormattedValue);
            if (texto.Length == 0 && valor.Value is not null)
                texto = LimpadorTextoCad.LimparMText(Convert.ToString(valor.Value, CultureInfo.InvariantCulture));
            if (texto.Length > 0) partes.Add(texto.Replace('\n', ' '));
        }
        return string.Join(" ", partes);
    }

    private static string? TextoDaCota(Dimension cota)
    {
        string medicao;
        try
        {
            medicao = cota.GetMeasurementText();
        }
        catch
        {
            medicao = cota.Measurement.ToString("0.###", CultureInfo.InvariantCulture);
        }

        var texto = cota.Text;
        if (string.IsNullOrEmpty(texto)) return LimpadorTextoCad.LimparMText(medicao);
        if (texto == " ") return null; // AutoCAD: espaço = texto suprimido
        return LimpadorTextoCad.LimparMText(texto.Replace("<>", medicao));
    }

    // ------------------------------------------------------------------ apoio

    private static void Adicionar(Contexto ctx, string tipo, Entity origem, string texto, XYZ posicaoLocal, double altura)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;

        var posicao = ParaEspaco(ctx, posicaoLocal);
        var chave = (texto, (long)Math.Round(posicao.X * 10), (long)Math.Round(posicao.Y * 10));
        if (!ctx.Vistos.Add(chave)) return; // entidade duplicada no mesmo lugar

        var escala = EscalaAcumulada(ctx);
        ctx.Espaco.Textos.Add(new TextoCad(tipo, origem.Layer?.Name ?? string.Empty, texto, posicao.X, posicao.Y, altura * escala));
    }

    /// <summary>Aplica as transformações dos INSERTs abertos, do mais interno para o mais externo.</summary>
    private static XYZ ParaEspaco(Contexto ctx, XYZ ponto)
    {
        for (var i = ctx.Transformacoes.Count - 1; i >= 0; i--)
            ponto = ctx.Transformacoes[i].ApplyTransform(ponto, false);
        return ponto;
    }

    private static double EscalaAcumulada(Contexto ctx)
    {
        var escala = 1.0;
        foreach (var t in ctx.Transformacoes)
        {
            var s = Math.Abs(t.Scale.X);
            if (s > 1e-9) escala *= s;
        }
        return escala;
    }
}

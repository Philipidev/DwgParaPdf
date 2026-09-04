using System.Text;
using ACadSharp;
using ACadSharp.IO;

namespace DwgParaPdf;

/// <summary>Abre DWG ou DXF (detectando pelo conteúdo) em modo tolerante, recolhendo os avisos do leitor.</summary>
public static class LeitorCad
{
    private enum Formato { Dwg, Dxf }

    public static CadDocument Ler(byte[] conteudo, string nome, List<string> avisos)
    {
        ArgumentNullException.ThrowIfNull(conteudo);
        if (conteudo.Length == 0) throw new ArgumentException("Conteúdo vazio.", nameof(conteudo));

        void AoNotificar(object? remetente, NotificationEventArgs e)
        {
            if (e.NotificationType is NotificationType.Warning or NotificationType.Error or NotificationType.NotSupported)
                avisos.Add($"{e.NotificationType}: {e.Message}");
        }

        var formato = DetectarFormato(conteudo, nome);
        using var ms = new MemoryStream(conteudo, writable: false);

        try
        {
            if (formato == Formato.Dxf)
            {
                using var leitor = new DxfReader(ms, AoNotificar);
                leitor.Configuration.Failsafe = true;
                return leitor.Read();
            }

            var configuracao = new DwgReaderConfiguration
            {
                Failsafe = true,
                KeepUnknownEntities = false,
                KeepUnknownNonGraphicalObjects = false,
                ReadSummaryInfo = true,
            };
            return DwgReader.Read(ms, configuracao, AoNotificar);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"Não foi possível ler '{nome}' como {formato}: {ex.Message}", ex);
        }
    }

    private static Formato DetectarFormato(byte[] b, string nome)
    {
        // DWG começa com a versão em ASCII: "AC1015", "AC1018", "AC1021", "AC1024", "AC1027", "AC1032"...
        if (b.Length >= 6 && b[0] == (byte)'A' && b[1] == (byte)'C' && b[2] == (byte)'1' && b[3] == (byte)'0')
            return Formato.Dwg;

        var cabecalho = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 256));
        if (cabecalho.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal) ||
            cabecalho.Contains("SECTION", StringComparison.Ordinal))
            return Formato.Dxf;

        return Path.GetExtension(nome).Equals(".dxf", StringComparison.OrdinalIgnoreCase) ? Formato.Dxf : Formato.Dwg;
    }
}

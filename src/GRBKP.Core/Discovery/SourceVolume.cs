namespace GRBKP.Core.Discovery;

/// <summary>
/// O app nasceu para ler um SSD ANTIGO desconectado, montado como segunda unidade da máquina —
/// nesse caso nada do disco de origem está em uso e tudo pode ser lido à vontade. Mas também é
/// legítimo (e às vezes mais prático) apontar o app para o disco da própria máquina em uso, com o
/// Windows rodando em cima dele.
///
/// Os dois cenários exigem tratamentos diferentes: no disco em uso, o registro do sistema está
/// travado (o SID precisa vir do registro ao vivo) e os aplicativos do funcionário podem estar
/// abertos segurando seus próprios dados. Esta checagem é o que permite o app perceber em qual
/// dos dois está.
/// </summary>
public static class SourceVolume
{
    public static bool IsSameVolumeAs(string candidatePath, string referencePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(referencePath))
        {
            return false;
        }

        try
        {
            var candidateRoot = Path.GetPathRoot(Path.GetFullPath(candidatePath));
            var referenceRoot = Path.GetPathRoot(Path.GetFullPath(referencePath));
            return !string.IsNullOrEmpty(candidateRoot) &&
                string.Equals(candidateRoot, referenceRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

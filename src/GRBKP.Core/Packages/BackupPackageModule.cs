using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GRBKP.Core.Adapters;
using GRBKP.Core.Operations;

namespace GRBKP.Core.Packages;

/// <summary>
/// Resultado de provar um manifesto: quantas entradas dá para ler e quais linhas estão ilegíveis.
/// </summary>
public sealed record ManifestInspection(
    long TotalLines,
    long ValidEntries,
    IReadOnlyList<long> UnreadableLines)
{
    public bool IsIntact => UnreadableLines.Count == 0;
}

public sealed class BackupPackageModule(IFileSystemAdapter fileSystem, IClock clock)
{
    internal static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();
    internal static JsonSerializerOptions JsonLineOptions { get; } = CreateJsonLineOptions();

    public async Task<BackupPackageWriter> CreateWriterAsync(
        string packageRoot,
        Guid operationId,
        PackageMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(metadata);

        await fileSystem.CreateDirectoryAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        foreach (var directory in new[] { "data", "modules", "reports", "resume" })
        {
            await fileSystem.CreateDirectoryAsync(
                Path.Combine(packageRoot, directory),
                cancellationToken).ConfigureAwait(false);
        }

        var filesStream = await fileSystem.OpenWriteAsync(
            Path.Combine(packageRoot, "files.jsonl"),
            FileWriteMode.CreateOrOverwrite,
            cancellationToken).ConfigureAwait(false);
        var journalStream = await fileSystem.OpenWriteAsync(
            Path.Combine(packageRoot, "journal.jsonl"),
            FileWriteMode.CreateOrOverwrite,
            cancellationToken).ConfigureAwait(false);

        var checkpoint = new OperationCheckpoint(
            operationId,
            metadata.BackupId,
            OperationKind.Backup,
            PackageState.Created.ToString(),
            null,
            0,
            null,
            DurableCounters.Empty,
            UpdatedAtUtc: clock.UtcNow);

        var writer = new BackupPackageWriter(
            fileSystem,
            clock,
            packageRoot,
            metadata,
            checkpoint,
            filesStream,
            journalStream,
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
        await writer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return writer;
    }

    public async Task<BackupPackageWriter> OpenWriterAsync(
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        var metadata = await ReadMetadataAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        var checkpoint = await ReadOperationAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        var manifestPath = Path.Combine(packageRoot, "files.jsonl");

        // Antes de continuar, descarta qualquer sujeira no fim do manifesto deixada pela
        // interrupção anterior (ver RepairManifestAsync). Sem isto, o backup retomado termina
        // "completo" com uma linha impossível de interpretar e a restauração morre nela.
        var scan = await RepairManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var existingManifest = await fileSystem.OpenReadAsync(
            manifestPath,
            cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await existingManifest.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }
        }

        // O manifesto é a autoridade sobre o que realmente está gravado no pacote: o checkpoint
        // passa a refletir exatamente ele. Isso cobre os dois desencontros possíveis — checkpoint
        // à frente do manifesto (linhas perdidas na queda) e manifesto à frente do checkpoint
        // (linhas gravadas depois do último checkpoint durável, que seriam recopiadas e
        // duplicadas).
        checkpoint = checkpoint with
        {
            LastConfirmedSequence = scan.LastSequence,
            LastConfirmedPath = scan.LastPath,
            Counters = checkpoint.Counters with
            {
                CopiedFiles = scan.Files,
                CopiedBytes = scan.Bytes
            }
        };

        var filesStream = await fileSystem.OpenWriteAsync(
            manifestPath,
            FileWriteMode.Append,
            cancellationToken).ConfigureAwait(false);
        var journalStream = await fileSystem.OpenWriteAsync(
            Path.Combine(packageRoot, "journal.jsonl"),
            FileWriteMode.Append,
            cancellationToken).ConfigureAwait(false);
        var writer = new BackupPackageWriter(
            fileSystem,
            clock,
            packageRoot,
            metadata,
            checkpoint,
            filesStream,
            journalStream,
            hash);
        await writer.AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "operation-resumed",
            "success"), cancellationToken).ConfigureAwait(false);
        return writer;
    }

    public async Task<PackageMetadata> ReadMetadataAsync(
        string packageRoot,
        CancellationToken cancellationToken = default) =>
        await ReadJsonAsync<PackageMetadata>(
            Path.Combine(packageRoot, "package.json"),
            cancellationToken).ConfigureAwait(false);

    public async Task<OperationCheckpoint> ReadOperationAsync(
        string packageRoot,
        CancellationToken cancellationToken = default) =>
        await ReadJsonAsync<OperationCheckpoint>(
            Path.Combine(packageRoot, "operation.json"),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Percorre o manifesto inteiro só para PROVAR se ele está legível, sem lançar exceção no
    /// caminho. Serve para responder antes de usar: dá para restaurar com este pacote?
    ///
    /// Diferente da varredura usada na retomada de um backup, esta NÃO para na primeira linha
    /// quebrada — ela conta todas. O motivo é a diferença de situação: num backup interrompido as
    /// entradas seguintes serão recopiadas, então descartá-las é barato; num pacote já fechado
    /// elas são tudo o que existe, e condenar cento e tantas mil entradas boas por causa de uma
    /// quebrada seria jogar fora o backup inteiro.
    /// </summary>
    public async Task<ManifestInspection> InspectManifestAsync(
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        var unreadable = new List<long>();
        long validEntries = 0;
        long lineNumber = 0;

        await using var stream = await fileSystem.OpenReadAsync(
            Path.Combine(packageRoot, "files.jsonl"), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryReadEntry(Utf8NoBom.GetBytes(line)) is null)
            {
                unreadable.Add(lineNumber);
            }
            else
            {
                validEntries++;
            }
        }

        return new ManifestInspection(lineNumber, validEntries, unreadable);
    }

    public IAsyncEnumerable<FileManifestEntry> ReadFilesAsync(
        string packageRoot,
        CancellationToken cancellationToken = default) =>
        ReadFilesAsync(packageRoot, null, cancellationToken);

    /// <summary>
    /// Lê o manifesto. Com <paramref name="unreadableLines"/> informado, uma linha ilegível é
    /// anotada ali e pulada, em vez de abortar a leitura — assim um pacote já fechado com uma
    /// linha destruída ainda devolve tudo o que resta. Sem ele, qualquer linha ruim é erro, que é
    /// o que se quer em qualquer lugar onde pular seria perda silenciosa.
    /// </summary>
    public async IAsyncEnumerable<FileManifestEntry> ReadFilesAsync(
        string packageRoot,
        IList<long>? unreadableLines,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await using var stream = await fileSystem.OpenReadAsync(
            Path.Combine(packageRoot, "files.jsonl"),
            cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);

        long lineNumber = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = TryReadEntry(Utf8NoBom.GetBytes(line));
            if (entry is null)
            {
                if (unreadableLines is null)
                {
                    throw new InvalidDataException(
                        $"Linha {lineNumber} de files.jsonl está ilegível.");
                }

                unreadableLines.Add(lineNumber);
                continue;
            }

            // Caminho inválido NUNCA é pulado, nem quando linhas ilegíveis são toleradas: isso é
            // sinal de pacote adulterado (tentativa de escrever fora do destino), e aí a resposta
            // certa é recusar o pacote inteiro, não restaurar o resto dele.
            PackagePath.ValidateRelative(entry.PackageRelativePath);
            yield return entry;
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private sealed record ManifestScan(long ValidBytes, long LastSequence, string? LastPath, long Files, long Bytes);

    /// <summary>
    /// Descarta o que estiver quebrado no fim do manifesto antes de uma retomada.
    ///
    /// Quando o SSD é desconectado (ou falta energia) no meio de um backup, o Windows já estendeu
    /// o arquivo mas os dados ainda estavam em cache e se perdem; o NTFS preenche essa região com
    /// ZEROS para não vazar conteúdo antigo do disco. Aconteceu em produção: o app retomava,
    /// continuava anexando depois dos zeros e ainda calculava o hash por cima — o pacote terminava
    /// "completo" com uma linha impossível de interpretar, e a restauração morria nela.
    ///
    /// A varredura para na PRIMEIRA linha inválida e descarta tudo dali em diante, mesmo que
    /// existam linhas boas depois. É deliberado: só assim a numeração fica sem buracos. Manter as
    /// linhas posteriores deixaria um vão de arquivos que nunca seriam recopiados — exatamente a
    /// perda silenciosa que se quer evitar. O preço é recopiar esses arquivos, que é barato perto
    /// de um pacote que não restaura.
    ///
    /// Um manifesto íntegro não é tocado: nada é reescrito e nada se perde.
    /// </summary>
    private async Task<ManifestScan> RepairManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var scan = await ScanManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var info = await fileSystem.GetInfoAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (info is null || info.Length == scan.ValidBytes)
        {
            return scan;
        }

        // Reescreve só o trecho válido, pelo mesmo padrão de temporário + renomeação usado no
        // resto do pacote: ou o manifesto saneado aparece inteiro, ou o anterior continua lá.
        var temporaryPath = manifestPath + ".grbkp.saneando";
        await using (var source = await fileSystem.OpenReadAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false))
        await using (var destination = await fileSystem.OpenWriteAsync(
            temporaryPath, FileWriteMode.CreateOrOverwrite, cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[128 * 1024];
            var remaining = scan.ValidBytes;
            while (remaining > 0)
            {
                var wanted = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                remaining -= read;
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await fileSystem.MoveAsync(temporaryPath, manifestPath, overwrite: true, cancellationToken)
            .ConfigureAwait(false);
        return scan;
    }

    private async Task<ManifestScan> ScanManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        long validBytes = 0;
        long lastSequence = 0;
        long files = 0;
        long bytes = 0;
        string? lastPath = null;

        await using var stream = await fileSystem.OpenReadAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[128 * 1024];
        var line = new List<byte>(1024);
        long offset = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                offset++;
                var current = buffer[index];
                if (current != (byte)'\n')
                {
                    line.Add(current);
                    continue;
                }

                var entry = TryReadUsableEntry(line);
                if (entry is null)
                {
                    // Primeira linha quebrada: daqui em diante nada é confiável.
                    return new ManifestScan(validBytes, lastSequence, lastPath, files, bytes);
                }

                validBytes = offset;
                lastSequence = entry.Sequence;
                lastPath = entry.PackageRelativePath;
                files++;
                bytes += entry.Length;
                line.Clear();
            }
        }

        // Sobrou conteúdo sem quebra de linha final: linha cortada pela metade, fica de fora.
        return new ManifestScan(validBytes, lastSequence, lastPath, files, bytes);
    }

    private static FileManifestEntry? TryReadEntry(List<byte> line) =>
        TryReadEntry(CollectionsMarshal.AsSpan(line));

    /// <summary>
    /// Só a LEITURA da linha: devolve <c>null</c> quando o texto não é um registro interpretável
    /// (corrupção). A validação do CAMINHO fica fora de propósito — um caminho malicioso não é
    /// corrupção acidental, é adulteração, e quem chama precisa poder recusar o pacote inteiro em
    /// vez de pular a linha e seguir restaurando o resto.
    /// </summary>
    private static FileManifestEntry? TryReadEntry(ReadOnlySpan<byte> line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize<FileManifestEntry>(line, JsonLineOptions);
            return entry is null || string.IsNullOrWhiteSpace(entry.PackageRelativePath)
                ? null
                : entry;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Linha legível E com caminho aceitável — o que a retomada precisa para considerar uma
    /// entrada aproveitável. Aqui um caminho inválido encerra a varredura (o trecho é descartado),
    /// que é o tratamento certo para um manifesto que vai ser reescrito de qualquer forma.
    /// </summary>
    private static FileManifestEntry? TryReadUsableEntry(List<byte> line)
    {
        var entry = TryReadEntry(line);
        if (entry is null)
        {
            return null;
        }

        try
        {
            PackagePath.ValidateRelative(entry.PackageRelativePath);
            return entry;
        }
        catch (InvalidPackagePathException)
        {
            return null;
        }
    }

    private async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return value ?? throw new InvalidDataException($"JSON vazio ou inválido: {path}");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static JsonSerializerOptions CreateJsonLineOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class BackupPackageWriter : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly IFileSystemAdapter fileSystem;
    private readonly IClock clock;
    private readonly string packageRoot;
    private readonly Stream filesStream;
    private readonly Stream journalStream;
    private readonly IncrementalHash filesHash;
    private PackageMetadata metadata;
    private OperationCheckpoint checkpoint;
    private bool disposed;
    private int filesSinceCheckpoint;
    private DateTimeOffset lastCheckpointAtUtc;

    /// <summary>
    /// Regravar operation.json a cada arquivo copiado era o maior gargalo do backup: medido em
    /// 2.000 arquivos pequenos, essas regravações sozinhas custavam 3.471 ms de um total de
    /// 10.032 ms (a cópia crua dos mesmos arquivos leva 1.816 ms). Cada uma serializa o
    /// checkpoint, grava um arquivo temporário, dá flush e renomeia — quatro operações de disco
    /// por arquivo do usuário. Acrescentar uma linha ao manifesto e ao journal, em comparação,
    /// custou 74 ms no total: é irrisório e continua sendo feito a cada arquivo.
    ///
    /// O checkpoint agora é gravado em lotes. O que se perde: numa queda de energia, a retomada
    /// recomeça no máximo estes N arquivos atrás — todos já presentes no pacote, apenas copiados
    /// de novo. O que se ganha: o backup de um perfil real (centenas de milhares de arquivos)
    /// deixa de pagar esse pedágio em cada um deles. Transições de estado e a conclusão SEMPRE
    /// gravam, então um pacote finalizado ou pausado nunca fica com checkpoint atrasado.
    /// </summary>
    private const int CheckpointEveryFiles = 64;

    private static readonly TimeSpan CheckpointEveryInterval = TimeSpan.FromSeconds(2);

    internal BackupPackageWriter(
        IFileSystemAdapter fileSystem,
        IClock clock,
        string packageRoot,
        PackageMetadata metadata,
        OperationCheckpoint checkpoint,
        Stream filesStream,
        Stream journalStream,
        IncrementalHash filesHash)
    {
        this.fileSystem = fileSystem;
        this.clock = clock;
        this.packageRoot = packageRoot;
        this.metadata = metadata;
        this.checkpoint = checkpoint;
        this.filesStream = filesStream;
        this.journalStream = journalStream;
        this.filesHash = filesHash;
        lastCheckpointAtUtc = clock.UtcNow;
    }

    public PackageMetadata Metadata => metadata;

    public OperationCheckpoint Checkpoint => checkpoint;

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await WriteMetadataAsync(cancellationToken).ConfigureAwait(false);
        await WriteOperationAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "operation-created",
            "success"), cancellationToken).ConfigureAwait(false);
    }

    public async Task TransitionAsync(
        PackageState state,
        CancellationToken cancellationToken = default)
    {
        metadata = metadata with { Status = state };
        checkpoint = checkpoint with
        {
            State = state.ToString(),
            UpdatedAtUtc = clock.UtcNow
        };
        await WriteMetadataAsync(cancellationToken).ConfigureAwait(false);
        await WriteOperationAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "state-changed",
            state.ToString().ToLowerInvariant()), cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendCommittedFileAsync(
        FileManifestEntry entry,
        DurableCounters counters,
        CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(entry, BackupPackageModule.JsonLineOptions) + "\n";
        var bytes = Utf8.GetBytes(line);
        await filesStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        filesHash.AppendData(bytes);
        await filesStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        checkpoint = checkpoint with
        {
            LastConfirmedSequence = entry.Sequence,
            LastConfirmedPath = entry.PackageRelativePath,
            Counters = counters,
            UpdatedAtUtc = clock.UtcNow
        };
        metadata = metadata with { Counters = counters };
        await WriteOperationIfDueAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "file-committed",
            "success",
            entry.Sequence,
            entry.ModuleId,
            entry.PackageRelativePath), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registra de forma durável que um arquivo NÃO pôde ser copiado, sem escrevê-lo em
    /// files.jsonl — o manifesto precisa listar apenas o que realmente existe dentro do pacote,
    /// senão a restauração tentaria copiar um arquivo ausente. A sequência avança mesmo assim
    /// para que uma retomada não fique repetindo o mesmo arquivo problemático indefinidamente.
    /// </summary>
    public async Task AppendFailedFileAsync(
        long sequence,
        string moduleId,
        string packageRelativePath,
        string reason,
        DurableCounters counters,
        CancellationToken cancellationToken = default)
    {
        checkpoint = checkpoint with
        {
            LastConfirmedSequence = sequence,
            LastConfirmedPath = packageRelativePath,
            Counters = counters,
            UpdatedAtUtc = clock.UtcNow
        };
        metadata = metadata with { Counters = counters };
        await WriteOperationIfDueAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "file-failed",
            reason,
            sequence,
            moduleId,
            packageRelativePath), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// O journal é trilha de auditoria, não a fonte da verdade — quem manda é o files.jsonl. Por
    /// isso o flush por linha foi trocado por flush junto do checkpoint: o conteúdo é o mesmo, e
    /// numa queda o que importa (manifesto e checkpoint) continua com a mesma garantia de antes.
    /// Eventos de estado continuam indo direto para o disco na hora.
    /// </summary>
    public async Task AppendJournalAsync(
        JournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(entry, BackupPackageModule.JsonLineOptions) + "\n";
        var bytes = Utf8.GetBytes(line);
        await journalStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

        if (entry.EventType != "file-committed" && entry.EventType != "file-failed")
        {
            await journalStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CompleteAsync(
        DurableCounters counters,
        CancellationToken cancellationToken = default)
    {
        await TransitionAsync(PackageState.Verifying, cancellationToken).ConfigureAwait(false);
        await filesStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await journalStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var hash = Convert.ToHexString(filesHash.GetHashAndReset());
        metadata = metadata with
        {
            Status = PackageState.Complete,
            FinishedAtUtc = clock.UtcNow,
            Counters = counters,
            FilesManifestSha256 = hash
        };
        checkpoint = checkpoint with
        {
            State = PackageState.Complete.ToString(),
            Counters = counters,
            UpdatedAtUtc = clock.UtcNow
        };
        await WriteMetadataAsync(cancellationToken).ConfigureAwait(false);
        await WriteOperationAsync(cancellationToken).ConfigureAwait(false);
        await AppendJournalAsync(new JournalEntry(
            clock.UtcNow,
            checkpoint.OperationId,
            "state-changed",
            "complete"), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        filesHash.Dispose();
        await filesStream.DisposeAsync().ConfigureAwait(false);
        await journalStream.DisposeAsync().ConfigureAwait(false);
    }

    private Task WriteMetadataAsync(CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(
            Path.Combine(packageRoot, "package.json"),
            metadata,
            cancellationToken);

    private async Task WriteOperationAsync(CancellationToken cancellationToken)
    {
        await WriteJsonAtomicallyAsync(
            Path.Combine(packageRoot, "operation.json"),
            checkpoint,
            cancellationToken).ConfigureAwait(false);

        // O journal acompanha o checkpoint: a trilha de auditoria nunca fica mais atrasada do que
        // o ponto de retomada gravado.
        await journalStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        filesSinceCheckpoint = 0;
        lastCheckpointAtUtc = clock.UtcNow;
    }

    /// <summary>
    /// Grava o checkpoint só quando o lote fecha (ver <see cref="CheckpointEveryFiles"/>). Com um
    /// relógio fixo (testes) o critério de tempo nunca dispara, e o comportamento fica
    /// determinístico pela contagem de arquivos.
    /// </summary>
    private async Task WriteOperationIfDueAsync(CancellationToken cancellationToken)
    {
        filesSinceCheckpoint++;
        if (filesSinceCheckpoint < CheckpointEveryFiles &&
            clock.UtcNow - lastCheckpointAtUtc < CheckpointEveryInterval)
        {
            return;
        }

        await WriteOperationAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteJsonAtomicallyAsync<T>(
        string destinationPath,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".grbkp.partial.{checkpoint.OperationId:N}";
        await using (var stream = await fileSystem.OpenWriteAsync(
            temporaryPath,
            FileWriteMode.CreateOrOverwrite,
            cancellationToken).ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                value,
                BackupPackageModule.JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await fileSystem.MoveAsync(
            temporaryPath,
            destinationPath,
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
    }
}

using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using GRBKP.Core.Adapters;
using GRBKP.Core.Discovery;
using GRBKP.Core.Modules;
using GRBKP.Core.Modules.CorporateCleanup;
using GRBKP.Core.Modules.FileZilla;
using GRBKP.Core.Modules.Printing;
using GRBKP.Core.Operations;
using GRBKP.Core.Orchestration;
using GRBKP.Core.Packages;
using GRBKP.Core.Restore;
using GRBKP.Core.Selection;
using GRBKP.Core.Transfers;
using GRBKP.Windows;
using GRBKP.Windows.Commands;
using GRBKP.Windows.FileSystem;
using GRBKP.Windows.Processes;
using GRBKP.Windows.Registry;
using GRBKP.Windows.Services;

namespace GRBKP.App;

public partial class MainWindow : Window
{
    /// <summary>
    /// Rotulo do SSD externo onde o app roda na empresa — o padrao de destino/origem de backup
    /// aponta pra ele automaticamente. Buscado por rotulo de volume, nao por letra de unidade,
    /// porque a letra muda de computador para computador.
    /// </summary>
    private const string BackupDriveLabel = "SSD-BKP-GR";

    private readonly IFileSystemAdapter fileSystem = new WindowsFileSystemAdapter();
    private readonly IProcessAdapter processAdapter = new WindowsProcessAdapter();
    private readonly IClock clock = new SystemClock();
    private readonly WindowsProfileDiscovery profileDiscovery;
    private readonly OfflineProfileSidResolver sourceSidResolver;
    private readonly ProfileFolderRedirectionDetector redirectionDetector;
    private readonly LiveProfileSidReader destinationSidReader = new();
    private readonly BackupPackageModule packageModule;
    private readonly StreamingBackupEngine backupEngine;
    private readonly IReadOnlyDictionary<string, IMigrationModule> modules;
    private readonly MigrationOrchestrator orchestrator;
    private readonly FileZillaMigrationService fileZillaService;
    private readonly PrinterDriverMigrationService printerDriverService;
    private readonly CorporateCleanupService corporateCleanupService;

    private string? sourceVolumeRoot;
    private readonly List<(DiscoveredProfile Profile, RadioButton Control)> profileOptions = [];

    private string? restorePackageRoot;
    private string? restoreDestinationProfileRoot;
    private PackageMetadata? restoreMetadata;
    private readonly List<(string ModuleId, CheckBox Control)> restoreModuleOptions = [];

    /// <summary>
    /// Trava contra dois cliques: sem ela, clicar duas vezes em "Iniciar backup" dispara duas
    /// operações concorrentes lendo a mesma origem e gravando em pacotes diferentes (a tela de
    /// progresso esconde o botão, mas o segundo clique pode já estar na fila do Windows).
    /// </summary>
    private bool operationInProgress;

    private long progressTotalBytes;
    private long progressTotalFiles;
    private string progressContext = string.Empty;
    private DateTime? lastProgressRefresh;
    private string? currentFailureReportPath;

    private static readonly System.Windows.Media.Brush SuccessBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x8A, 0xF0, 0xA5));

    private static readonly System.Windows.Media.Brush FailureBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF0, 0x8A, 0x8A));

    private static readonly System.Windows.Media.Brush WarningBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xFF, 0xE8, 0xA3));

    private static readonly System.Windows.Media.Brush AccentBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x9A, 0xE7, 0xFF));

    private static readonly System.Windows.Media.Brush BodyBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE5, 0xF7, 0xFF));

    private enum ResultTone
    {
        Info,
        Warning,
        Success
    }

    /// <summary>
    /// Um bloco da tela de resultado: título curto e as linhas daquele assunto. Substitui o
    /// parágrafo único onde caminho de pasta, avisos e lista de erros se misturavam.
    /// </summary>
    private sealed record ResultSection(
        string Title,
        IReadOnlyList<string> Lines,
        ResultTone Tone = ResultTone.Info);

    public MainWindow()
    {
        InitializeComponent();
        profileDiscovery = new WindowsProfileDiscovery(fileSystem);
        sourceSidResolver = new OfflineProfileSidResolver(new OfflineRegistryHiveReader());
        redirectionDetector = new ProfileFolderRedirectionDetector(new OfflineRegistryHiveReader());
        packageModule = new BackupPackageModule(fileSystem, clock);
        backupEngine = new StreamingBackupEngine(fileSystem, packageModule, clock);
        modules = MigrationModuleCatalog.CreateAll(fileSystem, processAdapter);
        var restoreEngine = new StreamingRestoreEngine(
            fileSystem,
            packageModule,
            clock,
            modulesHandledSeparately: modules.Keys.ToHashSet());
        var moduleCoordinator = new AppModuleRestoreCoordinator(packageModule, modules);
        orchestrator = new MigrationOrchestrator(
            fileSystem, packageModule, clock, backupEngine, restoreEngine, moduleCoordinator);
        fileZillaService = new FileZillaMigrationService(fileSystem, new WindowsServiceAdapter());
        printerDriverService = new PrinterDriverMigrationService(
            fileSystem, new WindowsCommandAdapter(), new PrinterDriverDiscovery(fileSystem));
        corporateCleanupService = new CorporateCleanupService(
            fileSystem, new WindowsExternalUninstallerAdapter(), new WindowsCommandAdapter());

        LoadVisualAssets();
    }

    // ---- Navegação entre páginas ----

    private void GoHome_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage);

    private void GoToBackupSource_Click(object sender, RoutedEventArgs e)
    {
        BackupSourceStatusText.Text = "Informe a unidade e clique em Buscar perfis.";
        ShowPage(BackupSourcePage);
    }

    private void GoToRestoreSource_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RestorePackageTextBox.Text))
        {
            var backupDriveRoot = FindBackupDriveRoot();
            if (backupDriveRoot is not null)
            {
                RestorePackageTextBox.Text = backupDriveRoot;
            }
        }

        ShowPage(RestoreSourcePage);
    }

    private void GoToVerify_Click(object sender, RoutedEventArgs e)
    {
        VerifyStatusText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(VerifyPackageTextBox.Text))
        {
            var backupDriveRoot = FindBackupDriveRoot();
            if (backupDriveRoot is not null)
            {
                VerifyPackageTextBox.Text = backupDriveRoot;
            }
        }

        ShowPage(VerifyPage);
    }

    private void BrowseVerifyPackage_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(VerifyPackageTextBox, "Selecione a pasta do backup a conferir");

    private void BrowseSourceVolume_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(SourceVolumeTextBox, "Selecione a unidade ou pasta do SSD antigo");

    private void BrowseBackupDestination_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(BackupDestinationTextBox, "Selecione a pasta onde o backup será salvo");

    private void BrowseRestorePackage_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(RestorePackageTextBox, "Selecione a pasta do backup (pacote)");

    private void BrowseRestoreDestination_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(RestoreDestinationTextBox, "Selecione a pasta do perfil de destino");

    private static string? FindBackupDriveRoot()
    {
        try
        {
            return DriveInfo.GetDrives()
                .FirstOrDefault(drive => drive.IsReady && string.Equals(
                    drive.VolumeLabel, BackupDriveLabel, StringComparison.OrdinalIgnoreCase))
                ?.RootDirectory.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void BrowseFolder(TextBox target, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (Directory.Exists(target.Text))
        {
            dialog.InitialDirectory = target.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FolderName;
        }
    }

    private void ShowPage(UIElement page)
    {
        foreach (var candidate in new UIElement[]
        {
            HomePage, BackupSourcePage, BackupProfilePage, RestoreSourcePage,
            RestoreConfirmPage, VerifyPage, ProgressPage, ResultPage
        })
        {
            candidate.Visibility = ReferenceEquals(candidate, page)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ShowProgress(string title, string details)
    {
        ProgressTitleText.Text = title;
        ProgressDetailsText.Text = details;
        ProgressAmountText.Text = string.Empty;
        OperationProgressBar.IsIndeterminate = true;
        OperationProgressBar.Value = 0;
        progressTotalBytes = 0;
        progressTotalFiles = 0;
        lastProgressRefresh = null;
        ShowPage(ProgressPage);
        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.1)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        LoadingRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, animation);
    }

    /// <summary>
    /// Informa o tamanho total que será transferido, o que transforma o indicador indeterminado
    /// numa barra de progresso de verdade. No backup vem de uma medição prévia dos arquivos; na
    /// restauração, dos contadores gravados no próprio pacote.
    /// </summary>
    private void SetProgressTotals(long totalFiles, long totalBytes)
    {
        progressTotalFiles = totalFiles;
        progressTotalBytes = totalBytes;
        OperationProgressBar.IsIndeterminate = totalBytes <= 0;
        ReportProgress(new TransferProgress(0, 0), force: true);
    }

    /// <summary>
    /// Atualiza a barra no máximo a cada 120 ms: o motor confirma centenas de arquivos por
    /// segundo e redesenhar a cada um só tiraria tempo de CPU da própria cópia.
    /// </summary>
    private void ReportProgress(TransferProgress progress, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && lastProgressRefresh is { } last &&
            now - last < TimeSpan.FromMilliseconds(120))
        {
            return;
        }

        lastProgressRefresh = now;

        if (progressTotalBytes > 0)
        {
            var percentage = Math.Clamp(progress.Bytes * 100.0 / progressTotalBytes, 0, 100);
            OperationProgressBar.Value = percentage;
            ProgressAmountText.Text =
                $"{FormatBytes(progress.Bytes)} de {FormatBytes(progressTotalBytes)}  ·  " +
                $"{percentage:0}%";
        }
        else
        {
            ProgressAmountText.Text = progress.Files > 0
                ? $"{progress.Files:N0} arquivos  ·  {FormatBytes(progress.Bytes)}"
                : string.Empty;
        }

        if (progressTotalFiles > 0)
        {
            ProgressDetailsText.Text =
                $"{progress.Files:N0} de {progressTotalFiles:N0} arquivos — {progressContext}";
        }
    }

    private void ShowResult(
        bool success,
        string title,
        string headline,
        IReadOnlyList<ResultSection> sections,
        string? failureReportPath = null)
    {
        LoadingRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);

        var showImage = success && ResultSuccessIcon.Source is not null;
        ResultSuccessIcon.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        ResultIconText.Visibility = showImage ? Visibility.Collapsed : Visibility.Visible;
        ResultIconText.Text = success ? "✓" : "✗";
        ResultIconText.Foreground = success ? SuccessBrush : FailureBrush;

        ResultTitleText.Text = title;
        ResultHeadlineText.Text = headline;

        ResultDetailsPanel.Children.Clear();
        foreach (var section in sections)
        {
            ResultDetailsPanel.Children.Add(BuildSectionBlock(section));
        }

        currentFailureReportPath = failureReportPath;
        OpenFailureReportButton.Visibility = failureReportPath is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        ShowPage(ResultPage);
    }

    private static StackPanel BuildSectionBlock(ResultSection section)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        block.Children.Add(new TextBlock
        {
            Text = section.Title,
            Foreground = ToneBrush(section.Tone),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 3)
        });

        foreach (var line in section.Lines)
        {
            block.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = BodyBrush,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 1, 0, 0)
            });
        }

        return block;
    }

    private static System.Windows.Media.Brush ToneBrush(ResultTone tone) => tone switch
    {
        ResultTone.Warning => WarningBrush,
        ResultTone.Success => SuccessBrush,
        _ => AccentBrush
    };

    private void OpenFailureReport_Click(object sender, RoutedEventArgs e)
    {
        if (currentFailureReportPath is null || !File.Exists(currentFailureReportPath))
        {
            MessageBox.Show(this, "O relatório não está mais disponível.", "GR BKP");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                currentFailureReportPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Não foi possível abrir o relatório.\n\n{currentFailureReportPath}\n\n{ex.Message}",
                "GR BKP");
        }
    }

    /// <summary>
    /// Rede de segurança para qualquer erro não previsto: no WPF, uma exceção que escapa de um
    /// manipulador de evento derruba o processo inteiro — e, para o técnico, o app simplesmente
    /// SOME no meio do trabalho, sem dizer nada. Aqui ele mostra o que aconteceu e continua vivo.
    /// </summary>
    internal void ShowUnexpectedError(Exception exception)
    {
        operationInProgress = false;
        ShowResult(
            false,
            "Erro inesperado",
            "A operação foi interrompida",
            [
                new ResultSection(
                    "O que aconteceu",
                    [exception.Message],
                    ResultTone.Warning),
                new ResultSection(
                    "O que fazer",
                    [
                        "Anote a mensagem acima e tente de novo.",
                        "Se a origem ainda estiver disponível, nada foi perdido."
                    ])
            ]);
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        SourceVolumeTextBox.Text = @"F:\";
        PersonNameTextBox.Text = string.Empty;
        BackupDestinationTextBox.Text = string.Empty;
        RestorePackageTextBox.Text = string.Empty;
        RestoreDestinationTextBox.Text = string.Empty;
        CorporateCleanupCheckBox.IsChecked = true;
        ProfileListPanel.Children.Clear();
        profileOptions.Clear();
        ShowPage(HomePage);
    }

    // ---- Fluxo de backup ----

    private async void SearchProfiles_Click(object sender, RoutedEventArgs e)
    {
        var volumeRoot = SourceVolumeTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            BackupSourceStatusText.Text = "Informe uma unidade válida (ex.: F:\\).";
            return;
        }

        SearchProfilesButton.IsEnabled = false;
        BackupSourceStatusText.Text = "Buscando perfis…";
        try
        {
            var profiles = await profileDiscovery.DiscoverAsync(volumeRoot);
            if (profiles.Count == 0)
            {
                BackupSourceStatusText.Text = LooksLikeAProfileFolderItself(volumeRoot)
                    ? $"{volumeRoot} parece ser a pasta de UM perfil, não a raiz do disco. " +
                      $"Informe a unidade (ex.: {Path.GetPathRoot(volumeRoot)}) para que o app " +
                      "encontre todos os perfis dentro de Users."
                    : $"Nenhuma pasta encontrada em {Path.Combine(volumeRoot, "Users")}.";
                return;
            }

            sourceVolumeRoot = volumeRoot;
            PopulateProfileList(profiles);
            if (string.IsNullOrWhiteSpace(BackupDestinationTextBox.Text))
            {
                var backupDriveRoot = FindBackupDriveRoot();
                if (backupDriveRoot is not null)
                {
                    BackupDestinationTextBox.Text = backupDriveRoot;
                }
            }

            ShowPage(BackupProfilePage);
        }
        catch (Exception ex)
        {
            BackupSourceStatusText.Text = $"Não foi possível examinar {volumeRoot}: {ex.Message}";
        }
        finally
        {
            SearchProfilesButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Deteccao heuristica para o erro mais comum ao testar: apontar direto para a pasta de UM
    /// perfil (ex.: C:\Users\Fulano) em vez da raiz do disco (C:\) — o app sempre busca
    /// "&lt;o que foi digitado&gt;\Users", entao esse engano resulta em "nada encontrado" sem
    /// explicar por que.
    /// </summary>
    private static bool LooksLikeAProfileFolderItself(string candidatePath) =>
        File.Exists(Path.Combine(candidatePath, "NTUSER.DAT")) ||
        Directory.Exists(Path.Combine(candidatePath, "AppData"));

    private void PopulateProfileList(IReadOnlyList<DiscoveredProfile> profiles)
    {
        ProfileListPanel.Children.Clear();
        profileOptions.Clear();

        // Perfis disponiveis para backup (RealProfile) sempre no topo — o tecnico nao deveria
        // precisar rolar a lista passando por pastas de sistema/suspeitas pra achar um perfil real.
        var orderedProfiles = profiles
            .OrderBy(profile => profile.Classification == ProfileClassification.RealProfile ? 0 : 1)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var profile in orderedProfiles)
        {
            var isSelectable = profile.Classification == ProfileClassification.RealProfile;
            var label = profile.Classification switch
            {
                ProfileClassification.RealProfile => $"{profile.Name} — perfil real",
                ProfileClassification.TemplateOrSystem => $"{profile.Name} — pasta de sistema (ignorado)",
                _ => $"{profile.Name} — suspeito, revisar manualmente"
            };

            var radioButton = new RadioButton
            {
                GroupName = "ProfileSelection",
                Content = label,
                IsEnabled = isSelectable,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 16,
                Margin = new Thickness(4, 6, 4, 6),
                Opacity = isSelectable ? 1.0 : 0.55
            };
            if (isSelectable)
            {
                // Nome padrao do backup = nome do perfil selecionado, mas continua editavel —
                // atualiza se o tecnico trocar de perfil antes de digitar algo proprio.
                radioButton.Checked += (_, _) => PersonNameTextBox.Text = profile.Name;
            }

            ProfileListPanel.Children.Add(radioButton);
            profileOptions.Add((profile, radioButton));
        }

        var firstSelectable = profileOptions.FirstOrDefault(option => option.Control.IsEnabled);
        if (firstSelectable.Control is not null)
        {
            firstSelectable.Control.IsChecked = true;
        }
    }

    private async void StartBackup_Click(object sender, RoutedEventArgs e)
    {
        if (operationInProgress)
        {
            return;
        }

        var selected = profileOptions.FirstOrDefault(option => option.Control.IsChecked == true);
        if (selected.Profile is null || sourceVolumeRoot is null)
        {
            MessageBox.Show(this, "Selecione um perfil real antes de continuar.", "GR BKP");
            return;
        }

        var personLabel = PersonNameTextBox.Text.Trim();
        var destinationBase = BackupDestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(personLabel) || string.IsNullOrWhiteSpace(destinationBase))
        {
            MessageBox.Show(
                this, "Informe o nome da pessoa e a pasta de destino.", "GR BKP");
            return;
        }

        var profile = selected.Profile;
        if (!await ConfirmRedirectedFoldersAsync(profile))
        {
            return;
        }

        if (!await ConfirmOpenApplicationsAsync(sourceVolumeRoot))
        {
            return;
        }

        var personFolder = Path.Combine(destinationBase, $"BackUp[{personLabel}]");
        var packageRoot = await ResolvePackageRootAsync(personFolder);
        if (packageRoot is null)
        {
            return;
        }

        operationInProgress = true;
        progressContext = $"perfil {profile.Name}";
        ShowProgress("Analisando arquivos…", $"Medindo o tamanho do perfil {profile.Name}");
        try
        {
            var applicableModules = new Dictionary<string, IMigrationModule>();
            var discoveries = new Dictionary<string, ModuleDiscovery>();
            foreach (var (moduleId, module) in modules)
            {
                var discovery = await module.DiscoverAsync(
                    new MigrationContext(profile.ProfileRoot, profile.Name, fileSystem), CancellationToken.None);
                if (discovery.IsApplicable)
                {
                    applicableModules[moduleId] = module;
                    discoveries[moduleId] = discovery;
                }
            }

            var publicRoot = Path.Combine(sourceVolumeRoot, "Users", "Public");
            var selectionPolicy = new SelectionPolicyModule(
                fileSystem, MigrationModuleCatalog.KnownAppDataFootprints());
            var selectionRun = selectionPolicy.Select(
                new ProfileSelection(profile.Name, profile.ProfileRoot, publicRoot));

            // SID do perfil de origem, lido offline do SOFTWARE hive do proprio SSD antigo — vai
            // no pacote pra a restauracao poder confirmar depois que o destino e o mesmo usuario
            // de dominio (ver ConfirmRestore_Click), sem depender so do nome da pasta baterem.
            string? sourceProfileSid = null;
            try
            {
                // Quando a origem é o disco da máquina EM USO, o hive SOFTWARE está travado pelo
                // Windows e a leitura offline nunca devolve nada — o SID precisa vir do registro
                // ao vivo, que descreve exatamente estes mesmos perfis.
                var sids = SourceIsLiveSystemVolume(sourceVolumeRoot)
                    ? OfflineProfileSidResolver.MatchByProfileRoot(
                        destinationSidReader.ResolveAll(), [profile])
                    : await sourceSidResolver.ResolveSidsByProfileRootAsync(
                        sourceVolumeRoot, [profile]);
                sids.TryGetValue(profile.ProfileRoot, out sourceProfileSid);
            }
            catch (Exception)
            {
                // SID e um extra de seguranca, nao um requisito — se o hive nao puder ser lido
                // (formato inesperado, chave ausente etc.), o backup segue sem essa informacao.
            }

            var metadata = new PackageMetadata(
                "1.0",
                "1.0.0-fase2",
                Guid.NewGuid(),
                PackageState.Created,
                clock.UtcNow,
                null,
                new SourceComputer(
                    Environment.MachineName,
                    sourceVolumeRoot,
                    Environment.OSVersion.VersionString,
                    RuntimeInformation.ProcessArchitecture.ToString()),
                new SourceProfile(profile.Name, profile.ProfileRoot, sourceProfileSid),
                "fase2-basica",
                [.. applicableModules.Keys],
                DurableCounters.Empty,
                new IntegrityPolicy("SHA256", true, "size-and-handle+sha256-critical"),
                IsEncrypted: false);

            // Medição prévia: percorre a mesma seleção só lendo tamanhos, sem abrir nenhum
            // arquivo. Custa alguns segundos e é o que permite mostrar "3,4 GB de 8,9 GB" em vez
            // de um indicador girando sem previsão nenhuma.
            var measurement = await Task.Run(() => MeasureAsync(
                selectionPolicy.Select(new ProfileSelection(profile.Name, profile.ProfileRoot, publicRoot)),
                applicableModules,
                discoveries));

            ShowProgress("Fazendo backup…", $"Copiando o perfil {profile.Name}");
            ProgressDetailsText.Text = $"Salvando em {packageRoot}";
            SetProgressTotals(measurement.Files, measurement.Bytes);

            var result = await orchestrator.RunOrResumeBackupAsync(
                new BackupRequest(Guid.NewGuid(), packageRoot, metadata),
                BuildBackupItemsAsync(selectionRun, applicableModules, discoveries),
                new Progress<TransferProgress>(value => ReportProgress(value)));

            if (result.Result.State == PackageState.Complete)
            {
                var machineLevelLines = await CaptureMachineLevelServicesAsync(
                    sourceVolumeRoot, packageRoot);
                var sections = new List<ResultSection>
                {
                    new("Onde ficou salvo", [packageRoot])
                };

                var skipped = DescribeSkippedFolders(selectionRun.Summary);
                if (skipped.Count > 0)
                {
                    sections.Add(new ResultSection(
                        "Pastas deixadas de fora pela regra de seleção", skipped, ResultTone.Warning));
                }

                if (machineLevelLines.Count > 0)
                {
                    sections.Add(new ResultSection("Itens do computador", machineLevelLines));
                }

                var reportPath = WriteFailureReport(
                    packageRoot, "backup", result.Result.Errors, result.Result.Counters.FailedFiles);
                if (result.Result.Counters.FailedFiles > 0)
                {
                    sections.Add(new ResultSection(
                        $"{result.Result.Counters.FailedFiles} arquivo(s) não puderam ser copiados " +
                        "e ficaram de fora",
                        [
                            "Abaixo está o relatório com cada arquivo e o motivo do erro.",
                            "Causas comuns: arquivo aberto em outro programa, sem permissão de " +
                            "leitura, ou danificado."
                        ],
                        ResultTone.Warning));
                }

                ShowResult(
                    true,
                    result.Outcome == OrchestratedBackupOutcome.Resumed
                        ? "Backup concluído (retomado)"
                        : "Backup concluído",
                    $"{result.Result.Counters.CopiedFiles:N0} arquivos  ·  " +
                    FormatBytes(result.Result.Counters.CopiedBytes),
                    sections,
                    reportPath);
            }
            else
            {
                ShowResult(
                    false,
                    result.Result.State == PackageState.Paused
                        ? "Backup interrompido"
                        : "Backup não concluído",
                    $"{result.Result.Counters.CopiedFiles:N0} arquivos copiados antes de parar",
                    [
                        new ResultSection(
                            "Motivo",
                            result.Result.Errors.Count > 0
                                ? [.. result.Result.Errors.Select(error => error.Message).Distinct()]
                                : ["Sem detalhes adicionais."],
                            ResultTone.Warning),
                        new ResultSection("Pasta do backup incompleto", [packageRoot])
                    ]);
            }
        }
        catch (Exception ex)
        {
            ShowResult(
                false,
                "Falha no backup",
                "A operação foi interrompida",
                [new ResultSection("O que aconteceu", [ex.Message], ResultTone.Warning)]);
        }
        finally
        {
            operationInProgress = false;
        }
    }

    /// <summary>
    /// A origem é o disco desta própria máquina, com o Windows rodando em cima dele?
    /// </summary>
    private static bool SourceIsLiveSystemVolume(string sourceVolumeRoot) =>
        SourceVolume.IsSameVolumeAs(sourceVolumeRoot, Environment.SystemDirectory);

    /// <summary>
    /// Só faz sentido quando a origem é o disco da máquina EM USO: um aplicativo aberto segura os
    /// próprios dados (base do Outlook, do Thunderbird, do navegador) e eles seriam copiados no
    /// meio de uma escrita — sem erro nenhum aparecendo, mas com a cópia inconsistente. Com o SSD
    /// antigo desconectado, o cenário original do app, nada disso roda e nem chega a perguntar.
    ///
    /// Fechar é decisão do técnico, não do app: pode haver trabalho não salvo do funcionário na
    /// tela. Por isso três opções, e "continuar assim mesmo" é legítimo.
    /// </summary>
    private async Task<bool> ConfirmOpenApplicationsAsync(string sourceVolumeRoot)
    {
        if (!SourceIsLiveSystemVolume(sourceVolumeRoot))
        {
            return true;
        }

        IReadOnlyList<string> running;
        try
        {
            running = await processAdapter.GetRunningProcessesAsync(
                MigrationModuleCatalog.KnownProcessNames());
        }
        catch (Exception)
        {
            return true;
        }

        if (running.Count == 0)
        {
            return true;
        }

        var answer = MessageBox.Show(
            this,
            "Você está copiando do disco desta máquina, e estes programas do perfil estão " +
            $"abertos agora:\n\n  {string.Join(", ", running)}\n\n" +
            "Enquanto estão abertos, eles seguram os próprios dados (e-mails, favoritos, " +
            "senhas), que podem ser copiados pela metade sem nenhum erro aparecer.\n\n" +
            "Fechar esses programas agora e seguir com o backup?\n\n" +
            "Sim = fecha à força e copia (trabalho não salvo se perde).\n" +
            "Não = copia assim mesmo, com o risco acima.\n" +
            "Cancelar = volta, para você salvar e fechar por conta própria.",
            "Programas abertos na origem",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (answer == MessageBoxResult.Yes)
        {
            await processAdapter.TerminateProcessTreesAsync(running);
        }

        return true;
    }

    /// <summary>
    /// Decide se este backup começa do zero (pasta nova com data e hora) ou retoma um pacote que
    /// ficou pelo caminho. Retornar <c>null</c> significa que o técnico desistiu no diálogo.
    ///
    /// Sem isso, a retomada dos motores era código inalcançável: a interface sempre criava uma
    /// pasta nova, então um backup de 200 GB interrompido em 80% recomeçava do zero.
    /// </summary>
    private async Task<string?> ResolvePackageRootAsync(string personFolder)
    {
        var newPackageRoot = Path.Combine(
            personFolder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm"));

        IncompletePackage? incomplete;
        try
        {
            incomplete = await new IncompletePackageFinder(fileSystem, packageModule)
                .FindMostRecentAsync(personFolder);
        }
        catch (Exception)
        {
            // Procurar pendências é conveniência, não requisito: na dúvida, começa um novo.
            return newPackageRoot;
        }

        if (incomplete is null)
        {
            return newPackageRoot;
        }

        var copied = incomplete.Counters.CopiedFiles;
        var answer = MessageBox.Show(
            this,
            $"Existe um backup de {personLabelFrom(incomplete.PackageRoot)} que não foi concluído:\n\n" +
            $"  Pasta: {incomplete.PackageRoot}\n" +
            $"  Situação: {DescribePackageState(incomplete.Status)}\n" +
            $"  Já copiados: {copied} arquivo(s) • {FormatBytes(incomplete.Counters.CopiedBytes)}\n\n" +
            "Deseja RETOMAR de onde parou?\n\n" +
            "Sim = continua o backup existente (não recopia o que já foi feito).\n" +
            "Não = começa um backup novo, do zero, em outra pasta.",
            "Backup incompleto encontrado",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => incomplete.PackageRoot,
            MessageBoxResult.No => newPackageRoot,
            _ => null
        };

        static string personLabelFrom(string packageRoot) =>
            Path.GetFileName(packageRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string DescribePackageState(PackageState state) => state switch
    {
        PackageState.Paused => "interrompido (mídia desconectada ou disco cheio)",
        PackageState.Copying => "parou durante a cópia",
        PackageState.Inspecting => "parou logo no início",
        PackageState.Verifying => "parou na verificação final",
        PackageState.Incomplete => "incompleto",
        PackageState.Failed => "falhou",
        _ => state.ToString()
    };

    /// <summary>
    /// Antes de copiar qualquer coisa, checa no registro do próprio perfil se Documentos/Área de
    /// Trabalho/Downloads/Imagens apontam para fora do perfil (Backup de pastas do OneDrive,
    /// redirecionamento corporativo para a rede). Nesse caso a varredura padrão acharia a pasta
    /// vazia e o backup terminaria "com sucesso" sem os arquivos do funcionário. O técnico precisa
    /// decidir isso de olhos abertos, não descobrir depois.
    /// </summary>
    private async Task<bool> ConfirmRedirectedFoldersAsync(DiscoveredProfile profile)
    {
        IReadOnlyList<RedirectedFolder> redirected;
        try
        {
            redirected = await redirectionDetector.DetectAsync(profile.ProfileRoot);
        }
        catch (Exception)
        {
            // A detecção é um alerta extra, nunca um bloqueio: se falhar, o backup segue.
            return true;
        }

        if (redirected.Count == 0)
        {
            return true;
        }

        var list = string.Join(
            "\n", redirected.Select(folder => $"  • {folder.FolderName} → {folder.ConfiguredPath}"));
        var answer = MessageBox.Show(
            this,
            $"As pastas abaixo deste perfil NÃO ficam no lugar padrão:\n\n{list}\n\n" +
            "O backup copia apenas os caminhos padrão, então o conteúdo dessas pastas " +
            "NÃO será incluído. Isso normalmente acontece quando o OneDrive está fazendo " +
            "backup das pastas, ou quando há redirecionamento para a rede.\n\n" +
            "Continuar assim mesmo? (Escolha Não para tratar essas pastas manualmente antes.)",
            "Atenção: pastas fora do lugar padrão",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return answer == MessageBoxResult.Yes;
    }

    private static async IAsyncEnumerable<TransferItem> BuildBackupItemsAsync(
        SelectionRun selectionRun,
        IReadOnlyDictionary<string, IMigrationModule> applicableModules,
        IReadOnlyDictionary<string, ModuleDiscovery> discoveries)
    {
        var sequence = 0L;
        await foreach (var item in selectionRun.Items)
        {
            sequence++;
            yield return item with { Sequence = sequence };
        }

        foreach (var (moduleId, module) in applicableModules)
        {
            await foreach (var item in module.EnumerateBackupAsync(
                discoveries[moduleId], CancellationToken.None))
            {
                sequence++;
                yield return item with { Sequence = sequence };
            }
        }
    }

    /// <summary>
    /// FileZilla Server e drivers de impressora sao de NIVEL DE MAQUINA (dependem da raiz do
    /// volume, nao da raiz do perfil) — nao se encaixam no contrato IMigrationModule e por isso
    /// nao passam pelo AppModuleRestoreCoordinator; sao capturados diretamente para dentro do
    /// mesmo pacote, num passo a mais apos os dados pessoais/Modules de perfil terminarem.
    /// Tratado como melhor esforco (nunca reprova o backup): a config do FileZilla e os drivers
    /// sao um extra, nao o objetivo principal do backup do perfil.
    /// </summary>
    private async Task<List<string>> CaptureMachineLevelServicesAsync(string sourceVolumeRoot, string packageRoot)
    {
        var lines = new List<string>();

        try
        {
            var fileZillaResult = await fileZillaService.CaptureAsync(sourceVolumeRoot, packageRoot);
            if (fileZillaResult.Captured)
            {
                lines.Add("FileZilla Server: configuração capturada.");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"FileZilla Server: não foi possível capturar ({ex.Message}).");
        }

        try
        {
            var printerResult = await printerDriverService.CaptureAsync(sourceVolumeRoot, packageRoot);
            if (printerResult.Drivers.Count > 0)
            {
                lines.Add($"Drivers de impressora: {printerResult.Drivers.Count} capturado(s).");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"Drivers de impressora: não foi possível capturar ({ex.Message}).");
        }

        return lines;
    }

    // ---- Conferência de pacote ----

    /// <summary>
    /// A pergunta que o técnico precisa responder antes de formatar o SSD antigo: "está tudo lá?".
    /// Relê o pacote inteiro contra o manifesto. Deliberadamente não oferece nenhuma ação
    /// corretiva: se algo estiver errado, a resposta é refazer o backup com a origem ainda intacta.
    /// </summary>
    private async void VerifyPackage_Click(object sender, RoutedEventArgs e)
    {
        if (operationInProgress)
        {
            return;
        }

        var packageRoot = VerifyPackageTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            VerifyStatusText.Text = "Informe a pasta do backup.";
            return;
        }

        if (!Directory.Exists(Path.Combine(packageRoot, "data")) ||
            !File.Exists(Path.Combine(packageRoot, "package.json")))
        {
            VerifyStatusText.Text =
                $"{packageRoot} não parece ser uma pasta de backup do GR BKP. " +
                "Escolha a pasta que contém package.json (normalmente BackUp[nome]\\data e hora).";
            return;
        }

        operationInProgress = true;
        ShowProgress("Conferindo backup…", $"Relendo e verificando {packageRoot}");
        try
        {
            var verifier = new BackupPackageVerifier(fileSystem, packageModule);
            var progress = new Progress<long>(count =>
                ProgressDetailsText.Text = $"{count} arquivos conferidos…");
            var report = await Task.Run(() => verifier.VerifyAsync(packageRoot, progress));

            if (report.IsIntact)
            {
                ShowResult(
                    true,
                    "Backup íntegro",
                    $"{report.VerifiedFiles:N0} arquivos  ·  {FormatBytes(report.VerifiedBytes)}",
                    [
                        new ResultSection(
                            "O que foi conferido",
                            [
                                "Todos os arquivos da lista estão presentes e com o tamanho correto.",
                                $"{report.HashCheckedFiles:N0} arquivo(s) crítico(s) também tiveram " +
                                "o conteúdo conferido byte a byte.",
                                "A lista de arquivos do pacote está íntegra."
                            ]),
                        new ResultSection(
                            "Conclusão",
                            ["Pode seguir com a formatação da origem."],
                            ResultTone.Success)
                    ]);
            }
            else
            {
                var reportPath = WriteVerificationReport(packageRoot, report);
                ShowResult(
                    false,
                    "Backup com problemas",
                    $"{report.Issues.Count:N0} problema(s) encontrado(s)",
                    [
                        new ResultSection(
                            "NÃO formate a origem",
                            [
                                "Refaça o backup deste perfil enquanto o SSD antigo ainda está " +
                                "disponível.",
                                "O relatório abaixo lista cada problema encontrado."
                            ],
                            ResultTone.Warning),
                        new ResultSection(
                            "Pacote conferido",
                            [packageRoot])
                    ],
                    reportPath);
            }
        }
        catch (Exception ex)
        {
            ShowResult(
                false,
                "Não foi possível conferir",
                "A conferência não pôde ser feita",
                [new ResultSection("O que aconteceu", [ex.Message], ResultTone.Warning)]);
        }
        finally
        {
            operationInProgress = false;
        }
    }

    // ---- Fluxo de restauração ----

    private async void RestoreSourceNext_Click(object sender, RoutedEventArgs e)
    {
        var packageRoot = RestorePackageTextBox.Text.Trim();
        var destinationRoot = RestoreDestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(packageRoot) || string.IsNullOrWhiteSpace(destinationRoot))
        {
            RestoreSourceStatusText.Text = "Informe as duas pastas.";
            return;
        }

        try
        {
            var metadata = await packageModule.ReadMetadataAsync(packageRoot);
            if (metadata.Status != PackageState.Complete)
            {
                RestoreSourceStatusText.Text =
                    $"Este backup está com status \"{metadata.Status}\" — não pode ser restaurado.";
                return;
            }

            restorePackageRoot = packageRoot;
            restoreDestinationProfileRoot = destinationRoot;
            restoreMetadata = metadata;
            PopulateRestoreModuleChoices(metadata);
            RestoreConfirmationDetails.Text =
                $"Origem: {packageRoot}\n" +
                $"Perfil: {metadata.Profile.Name}\n" +
                $"Destino: {destinationRoot}\n" +
                $"Módulos no pacote: {(metadata.Modules.Count > 0 ? string.Join(", ", metadata.Modules) : "nenhum")}\n" +
                DescribeSidMatch(metadata.Profile.Sid, destinationRoot);
            ShowPage(RestoreConfirmPage);
        }
        catch (Exception ex)
        {
            RestoreSourceStatusText.Text = $"Não foi possível ler o backup em {packageRoot}: {ex.Message}";
        }
    }

    /// <summary>
    /// Le os perfis de destino ao vivo (I/O) e delega a comparacao em si para
    /// <see cref="ProfileSidMatcher"/> (logica pura, testada em GRBKP.Core.Tests).
    /// </summary>
    private string DescribeSidMatch(string? packageSid, string destinationProfileRoot)
    {
        IReadOnlyList<ProfileSidInfo> destinationProfiles;
        try
        {
            destinationProfiles = destinationSidReader.ResolveAll();
        }
        catch (Exception)
        {
            return "Conferência de usuário: não foi possível consultar o registro do destino.";
        }

        return ProfileSidMatcher.Describe(packageSid, destinationProfiles, destinationProfileRoot)
            .Message;
    }

    /// <summary>
    /// Uma caixa de marcação por aplicativo presente no pacote, todas ligadas por padrão. Permite
    /// refazer só o que faltou numa restauração anterior, sem repetir os arquivos pessoais.
    /// </summary>
    private void PopulateRestoreModuleChoices(PackageMetadata metadata)
    {
        RestoreModulesPanel.Children.Clear();
        restoreModuleOptions.Clear();
        RestorePersonalFilesCheckBox.IsChecked = true;

        foreach (var moduleId in metadata.Modules)
        {
            var caixa = new CheckBox
            {
                Content = FriendlyModuleName(moduleId),
                IsChecked = true,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                Margin = new Thickness(0, 2, 18, 2)
            };
            RestoreModulesPanel.Children.Add(caixa);
            restoreModuleOptions.Add((moduleId, caixa));
        }
    }

    private static string FriendlyModuleName(string moduleId) => moduleId switch
    {
        "thunderbird" => "Thunderbird",
        "firefox" => "Firefox",
        "chrome" => "Google Chrome",
        "edge" => "Microsoft Edge",
        "office" => "Office / Outlook",
        "adobe-acrobat" => "Adobe Acrobat Reader",
        _ => moduleId
    };

    private void RestoreConfirmBack_Click(object sender, RoutedEventArgs e) => ShowPage(RestoreSourcePage);

    private async void ConfirmRestore_Click(object sender, RoutedEventArgs e)
    {
        if (operationInProgress)
        {
            return;
        }

        if (restorePackageRoot is null || restoreDestinationProfileRoot is null)
        {
            ShowPage(RestoreSourcePage);
            return;
        }

        var runCorporateCleanup = CorporateCleanupCheckBox.IsChecked == true;
        var restorePersonalFiles = RestorePersonalFilesCheckBox.IsChecked == true;
        var chosenModules = restoreModuleOptions
            .Where(option => option.Control.IsChecked == true)
            .Select(option => option.ModuleId)
            .ToList();

        if (!restorePersonalFiles && chosenModules.Count == 0)
        {
            MessageBox.Show(
                this,
                "Escolha pelo menos uma coisa para restaurar: os arquivos pessoais ou algum aplicativo.",
                "GR BKP");
            return;
        }

        var operationId = Guid.NewGuid();
        var publicRoot = Path.Combine(
            Path.GetDirectoryName(restoreDestinationProfileRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? restoreDestinationProfileRoot,
            "Public");
        var operationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GRBKP", "operations", operationId.ToString("N"));

        operationInProgress = true;
        progressContext = $"destino {Path.GetFileName(restoreDestinationProfileRoot)}";

        if (!restorePersonalFiles)
        {
            await RestoreOnlyApplicationsAsync(
                operationId, restorePackageRoot, restoreDestinationProfileRoot,
                operationRoot, chosenModules, runCorporateCleanup);
            operationInProgress = false;
            return;
        }

        ShowProgress("Restaurando…", $"Restaurando para {restoreDestinationProfileRoot}");

        // Na restauração o total já é conhecido: está gravado no próprio pacote.
        SetProgressTotals(
            restoreMetadata?.Counters.CopiedFiles ?? 0,
            restoreMetadata?.Counters.CopiedBytes ?? 0);
        try
        {
            var result = await orchestrator.RunOrResumeRestoreAsync(
                new RestoreRequest(
                    operationId, restorePackageRoot, restoreDestinationProfileRoot,
                    publicRoot, operationRoot),
                new Progress<TransferProgress>(value => ReportProgress(value)),
                chosenModules);

            if (result.OverallState == RestoreState.Complete)
            {
                var machineLevelLines = await RestoreMachineLevelServicesAsync(
                    restorePackageRoot,
                    restoreMetadata?.Profile.OriginalPath,
                    restoreDestinationProfileRoot);
                var cleanupLines = runCorporateCleanup
                    ? await ApplyCorporateCleanupAsync(restoreDestinationProfileRoot)
                    : [];

                var sections = new List<ResultSection>
                {
                    new("Onde os dados foram restaurados", [restoreDestinationProfileRoot]),
                    new(
                        "Aplicativos",
                        result.ModuleOutcome.Results.Count > 0
                            ? [.. result.ModuleOutcome.Results.Select(DescribeModuleOutcome)]
                            : ["Nenhum aplicativo neste backup."])
                };

                if (machineLevelLines.Count > 0)
                {
                    sections.Add(new ResultSection("Itens do computador", machineLevelLines));
                }

                if (cleanupLines.Count > 0)
                {
                    sections.Add(new ResultSection("Limpeza corporativa", cleanupLines));
                }

                var reportPath = WriteFailureReport(
                    restorePackageRoot,
                    "restauracao",
                    result.PersonalDataResult.Errors,
                    result.PersonalDataResult.Counters.FailedFiles);
                if (result.PersonalDataResult.Counters.FailedFiles > 0)
                {
                    sections.Add(new ResultSection(
                        $"{result.PersonalDataResult.Counters.FailedFiles} arquivo(s) não puderam " +
                        "ser restaurados e ficaram de fora",
                        [
                            "Abaixo está o relatório com cada arquivo e o motivo do erro.",
                            "Causas comuns: arquivo aberto em outro programa, sem permissão, ou " +
                            "danificado no pacote."
                        ],
                        ResultTone.Warning));
                }

                sections.Add(new ResultSection(
                    "Próximo passo",
                    ["Reinicie este computador quando for possível para concluir a migração."],
                    ResultTone.Warning));

                ShowResult(
                    true,
                    "Restauração concluída",
                    $"{result.PersonalDataResult.Counters.CopiedFiles:N0} arquivos pessoais  ·  " +
                    FormatBytes(result.PersonalDataResult.Counters.CopiedBytes),
                    sections,
                    reportPath);
            }
            else
            {
                var failedModules = result.ModuleOutcome.Results
                    .Where(moduleResult => moduleResult.Outcome == ModuleOutcome.Failed)
                    .Select(moduleResult => moduleResult.ModuleId)
                    .ToList();

                var sections = new List<ResultSection>
                {
                    new(
                        "Motivo",
                        result.PersonalDataResult.Errors.Count > 0
                            ? [.. result.PersonalDataResult.Errors
                                .Select(error => error.Message).Distinct()]
                            : ["A restauração não foi concluída."],
                        ResultTone.Warning)
                };

                if (failedModules.Count > 0)
                {
                    sections.Add(new ResultSection(
                        "Aplicativos que falharam",
                        [
                            string.Join(", ", failedModules),
                            "Os dados que já existiam no destino para esses aplicativos foram " +
                            "preservados."
                        ],
                        ResultTone.Warning));
                }

                ShowResult(
                    false,
                    result.OverallState == RestoreState.Paused
                        ? "Restauração interrompida"
                        : "Restauração não concluída",
                    $"{result.PersonalDataResult.Counters.CopiedFiles:N0} arquivos restaurados " +
                    "antes de parar",
                    sections);
            }
        }
        catch (Exception ex)
        {
            ShowResult(
                false,
                "Falha na restauração",
                "A operação foi interrompida",
                [new ResultSection("O que aconteceu", [ex.Message], ResultTone.Warning)]);
        }
        finally
        {
            operationInProgress = false;
        }
    }

    /// <summary>
    /// Restaura SÓ os aplicativos escolhidos, sem repetir os arquivos pessoais. Existe por um
    /// caso real: numa restauração de 50 GB o Thunderbird falhou, e como os aplicativos eram
    /// restaurados em sequência parando no primeiro erro, Chrome e Edge nunca chegaram a ser
    /// tentados. Sem esta opção, recuperá-los exigiria repetir a operação inteira.
    /// </summary>
    private async Task RestoreOnlyApplicationsAsync(
        Guid operationId,
        string packageRoot,
        string destinationProfileRoot,
        string operationRoot,
        IReadOnlyList<string> moduleIds,
        bool runCorporateCleanup)
    {
        var nomes = string.Join(", ", moduleIds.Select(FriendlyModuleName));
        ShowProgress("Restaurando aplicativos…", nomes);
        ProgressAmountText.Text = "Trocando os dados de cada aplicativo…";

        try
        {
            var outcome = await orchestrator.RestoreOnlyModulesAsync(
                new RestoreRequest(
                    operationId, packageRoot, destinationProfileRoot,
                    Path.Combine(destinationProfileRoot, "Public"), operationRoot),
                moduleIds);

            var cleanupLines = runCorporateCleanup
                ? await ApplyCorporateCleanupAsync(destinationProfileRoot)
                : [];

            var sections = new List<ResultSection>
            {
                new("Onde foi restaurado", [destinationProfileRoot]),
                new(
                    "Aplicativos",
                    outcome.Results.Count > 0
                        ? [.. outcome.Results.Select(DescribeModuleOutcome)]
                        : ["Nenhum aplicativo foi restaurado."],
                    outcome.Succeeded ? ResultTone.Info : ResultTone.Warning),
                new(
                    "Arquivos pessoais",
                    ["Não foram tocados nesta operação, como você escolheu."])
            };

            if (cleanupLines.Count > 0)
            {
                sections.Add(new ResultSection("Limpeza corporativa", cleanupLines));
            }

            var falhas = outcome.Results.Count(r => r.Outcome == ModuleOutcome.Failed);
            if (falhas > 0)
            {
                sections.Add(new ResultSection(
                    "Atenção",
                    [
                        "Os dados que já existiam no destino para os aplicativos que falharam " +
                        "foram preservados — nada foi perdido.",
                        "Feche o aplicativo que falhou e tente de novo apenas ele."
                    ],
                    ResultTone.Warning));
            }

            ShowResult(
                outcome.Succeeded,
                outcome.Succeeded ? "Aplicativos restaurados" : "Alguns aplicativos falharam",
                $"{outcome.Results.Count(r => r.Outcome == ModuleOutcome.Applied)} de " +
                $"{outcome.Results.Count} aplicativo(s) restaurado(s)",
                sections);
        }
        catch (Exception ex)
        {
            ShowResult(
                false,
                "Falha ao restaurar aplicativos",
                "A operação foi interrompida",
                [new ResultSection("O que aconteceu", [ex.Message], ResultTone.Warning)]);
        }
    }

    /// <summary>
    /// Contraparte de <see cref="CaptureMachineLevelServicesAsync"/> na restauração — acionada só
    /// depois que a restauração de perfil (dados pessoais + Modules) termina com sucesso, já que
    /// revincular o FileZilla com um perfil de destino que nem chegou a ser restaurado não faria
    /// sentido. Também melhor esforço: uma falha aqui (ex.: serviço do FileZilla com nome
    /// diferente do esperado numa instalação real — ver TODO em FileZillaMigrationService) não
    /// reprova a restauração inteira, só aparece no resumo para o técnico revisar manualmente.
    /// </summary>
    private async Task<List<string>> RestoreMachineLevelServicesAsync(
        string packageRoot, string? oldProfileRoot, string newProfileRoot)
    {
        var lines = new List<string>();

        if (oldProfileRoot is not null)
        {
            var destinationVolumeRoot = Path.GetPathRoot(newProfileRoot) ?? newProfileRoot;
            try
            {
                var fileZillaResult = await fileZillaService.RestoreAsync(
                    packageRoot, oldProfileRoot, newProfileRoot, destinationVolumeRoot);
                if (fileZillaResult.Applied)
                {
                    lines.Add("FileZilla Server: configuração restaurada.");
                }
                else if (fileZillaResult.DiagnosticMessage is not null)
                {
                    lines.Add($"FileZilla Server: {fileZillaResult.DiagnosticMessage}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"FileZilla Server: falha ao restaurar ({ex.Message}).");
            }
        }

        try
        {
            var printerOutcomes = await printerDriverService.RestoreAsync(packageRoot);
            if (printerOutcomes.Count > 0)
            {
                var succeeded = printerOutcomes.Count(outcome => outcome.Succeeded);
                lines.Add($"Drivers de impressora: {succeeded}/{printerOutcomes.Count} instalado(s).");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"Drivers de impressora: falha ao restaurar ({ex.Message}).");
        }

        return lines;
    }

    /// <summary>
    /// Limpeza corporativa (OneDrive + Copilot) do computador de DESTINO — so acionada quando o
    /// tecnico deixou a caixa marcada e a restauracao de perfil ja terminou com sucesso. Melhor
    /// esforco, igual aos servicos de nivel de maquina: uma falha aqui aparece no resumo, mas nao
    /// reprova a restauracao.
    /// </summary>
    private async Task<List<string>> ApplyCorporateCleanupAsync(string destinationProfileRoot)
    {
        try
        {
            var destinationVolumeRoot = Path.GetPathRoot(destinationProfileRoot) ?? destinationProfileRoot;
            var result = await corporateCleanupService.ApplyAsync(
                destinationVolumeRoot, destinationProfileRoot);
            return [.. result.Messages];
        }
        catch (Exception ex)
        {
            return [$"Falha na limpeza: {ex.Message}"];
        }
    }

    /// <summary>
    /// A política de seleção pode deliberadamente DEIXAR DE FORA uma pasta inteira — hoje o caso
    /// é Imagens com menos de 21 arquivos, tratada como pasta de amostras do Windows e não como
    /// fotos do funcionário. Essa decisão ficava só no resumo interno, nunca chegava à tela: o
    /// técnico terminaria o backup sem saber que as imagens não foram copiadas.
    /// </summary>
    private static List<string> DescribeSkippedFolders(SelectionSummary summary) =>
    [
        .. summary.Decisions
            .Where(decision => decision.Kind == SelectionDecisionKind.IgnoredBelowThreshold)
            .Select(decision =>
                $"{decision.LogicalFolder}: {decision.LocalFileCount} arquivo(s), abaixo do " +
                $"mínimo de {decision.RequiredFileCount} — não incluída")
    ];

    private static string DescribeModuleOutcome(ModuleResult result)
    {
        var nome = result.ModuleId switch
        {
            "thunderbird" => "Thunderbird",
            "firefox" => "Firefox",
            "chrome" => "Google Chrome",
            "edge" => "Microsoft Edge",
            "office" => "Office / Outlook",
            "adobe-acrobat" => "Adobe Acrobat Reader",
            _ => result.ModuleId
        };

        var situacao = result.Outcome switch
        {
            ModuleOutcome.Applied => "restaurado",
            ModuleOutcome.NotApplicable => "não havia dados neste backup",
            ModuleOutcome.PreservedNotRestored => "preservado no pacote, não reintegrado",
            _ => "FALHOU — dados anteriores do destino preservados"
        };

        return $"{nome}: {situacao}";
    }

    /// <summary>
    /// Relatório em texto dos problemas achados na conferência, gravado dentro do próprio pacote
    /// para o técnico poder abrir, ler com calma e levar consigo.
    /// </summary>
    private static string? WriteVerificationReport(
        string packageRoot, PackageVerificationReport report)
    {
        try
        {
            var reportsFolder = Path.Combine(packageRoot, "reports");
            Directory.CreateDirectory(reportsFolder);
            var reportPath = Path.Combine(
                reportsFolder, $"conferencia-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

            var linhas = new List<string>
            {
                "GR BKP — conferência de backup",
                $"Data: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                $"Pacote: {packageRoot}",
                $"Situação do pacote: {report.PackageStatus}",
                $"Lista de arquivos íntegra: {(report.ManifestHashMatches ? "sim" : "NÃO")}",
                $"Arquivos conferidos: {report.VerifiedFiles:N0}",
                $"Problemas encontrados: {report.Issues.Count:N0}",
                string.Empty,
                "ATENÇÃO: não formate a origem enquanto houver problemas nesta lista.",
                string.Empty,
                new string('-', 78),
                string.Empty
            };

            var numero = 0;
            foreach (var issue in report.Issues)
            {
                numero++;
                linhas.Add($"{numero}. {issue.PackageRelativePath}");
                linhas.Add($"   Problema: {issue.Detail}");
                linhas.Add(string.Empty);
            }

            File.WriteAllLines(reportPath, linhas, Encoding.UTF8);
            return reportPath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Percorre a seleção inteira só lendo TAMANHOS, sem abrir nenhum arquivo, para conhecer o
    /// total antes de começar a copiar. É o que permite uma barra de progresso de verdade.
    /// </summary>
    private static async Task<(long Files, long Bytes)> MeasureAsync(
        SelectionRun selectionRun,
        IReadOnlyDictionary<string, IMigrationModule> applicableModules,
        IReadOnlyDictionary<string, ModuleDiscovery> discoveries)
    {
        long files = 0;
        long bytes = 0;
        await foreach (var item in BuildBackupItemsAsync(selectionRun, applicableModules, discoveries))
        {
            files++;
            bytes += item.Length;
        }

        return (files, bytes);
    }

    /// <summary>
    /// Arquivos que não puderam ser copiados viram um relatório de texto dentro do próprio pacote,
    /// com um arquivo por linha e o motivo do erro. Antes essa lista era despejada na tela de
    /// resultado, onde só cabiam cinco caminhos gigantes e o resto virava "… e mais 34" — sem
    /// nenhuma forma de o técnico ver quais eram.
    /// </summary>
    private static string? WriteFailureReport(
        string packageRoot,
        string operation,
        IReadOnlyList<OperationError> errors,
        long failedFiles)
    {
        if (failedFiles <= 0 || errors.Count == 0)
        {
            return null;
        }

        try
        {
            var reportsFolder = Path.Combine(packageRoot, "reports");
            Directory.CreateDirectory(reportsFolder);
            var reportPath = Path.Combine(
                reportsFolder,
                $"arquivos-nao-copiados-{operation}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

            var linhas = new List<string>
            {
                "GR BKP — arquivos que não puderam ser copiados",
                $"Operação: {operation}",
                $"Data: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                $"Pacote: {packageRoot}",
                $"Total: {failedFiles} arquivo(s)",
                string.Empty,
                "Causas comuns: arquivo aberto em outro programa, sem permissão de leitura, ou",
                "danificado. Arquivos internos do Windows (UsrClass.dat, settings.dat de apps da",
                "Loja, arquivos LOCK) ficam travados enquanto o perfil está em uso e normalmente",
                "não representam perda de dados do funcionário.",
                string.Empty,
                new string('-', 78),
                string.Empty
            };

            var numero = 0;
            foreach (var error in errors.Where(error => !string.IsNullOrWhiteSpace(error.RelativePath)))
            {
                numero++;
                linhas.Add($"{numero}. {error.RelativePath}");
                linhas.Add($"   Motivo: {error.Message}");
                linhas.Add(string.Empty);
            }

            File.WriteAllLines(reportPath, linhas, Encoding.UTF8);
            return reportPath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    // ---- Assets visuais ----

    private void LoadVisualAssets()
    {
        TryLoadImage(
            Path.Combine(AppContext.BaseDirectory, "Assets", "logo GRBKP.png"), MainLogoImage, LogoFallback);
        TryLoadImage(
            Path.Combine(AppContext.BaseDirectory, "Assets", "LOGO FAIXA DE BAIXO.png"),
            BrandStripImage,
            BrandStripFallback);
        TryLoadImage(
            Path.Combine(AppContext.BaseDirectory, "Assets", "icon-iniciarBKP.png"), StartBackupIcon);
        TryLoadImage(
            Path.Combine(AppContext.BaseDirectory, "Assets", "icon-extrairBKP.png"), ExtractBackupIcon);
        TryLoadImage(
            Path.Combine(AppContext.BaseDirectory, "Assets", "icon-conferirBKP.png"), VerifyBackupIcon);
        TryLoadImage(
            Path.Combine(AppContext.BaseDirectory, "Assets", "icon-sucesso.png"), ResultSuccessIcon);
        // O ícone de sucesso só aparece quando o resultado é positivo; ShowResult decide.
        ResultSuccessIcon.Visibility = Visibility.Collapsed;
    }

    private static void TryLoadImage(string path, Image image, UIElement? fallback = null)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            image.Source = bitmap;
            image.Visibility = Visibility.Visible;
            if (fallback is not null)
            {
                fallback.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // O texto de fallback permanece visivel se o asset original nao puder ser decodificado.
        }
    }
}

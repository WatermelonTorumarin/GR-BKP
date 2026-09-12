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
            RestoreConfirmPage, ProgressPage, ResultPage
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
        ShowPage(ProgressPage);
        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.1)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        LoadingRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, animation);
    }

    private void ShowResult(bool success, string title, string summary)
    {
        LoadingRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        ResultIconText.Text = success ? "✓" : "✗";
        ResultIconText.Foreground = success
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0xF0, 0xA5))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x8A, 0x8A));
        ResultTitleText.Text = title;
        ResultSummaryText.Text = summary;
        ShowPage(ResultPage);
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

        var packageRoot = Path.Combine(
            destinationBase,
            $"BackUp[{personLabel}]",
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm"));

        ShowProgress("Fazendo backup…", $"Copiando dados de {profile.Name} para {packageRoot}");
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
                var sids = await sourceSidResolver.ResolveSidsByProfileRootAsync(
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
                new IntegrityPolicy("SHA256", false, "size-and-handle"),
                IsEncrypted: false);

            var result = await orchestrator.RunOrResumeBackupAsync(
                new BackupRequest(Guid.NewGuid(), packageRoot, metadata),
                BuildBackupItemsAsync(selectionRun, applicableModules, discoveries));

            if (result.Result.State == PackageState.Complete)
            {
                var machineLevelSummary = await CaptureMachineLevelServicesAsync(
                    sourceVolumeRoot, packageRoot);
                ShowResult(
                    true,
                    "Backup concluído",
                    $"{result.Result.Counters.CopiedFiles} arquivos • " +
                    $"{FormatBytes(result.Result.Counters.CopiedBytes)}\n\n" +
                    $"Os dados foram salvos em:\n{packageRoot}" +
                    machineLevelSummary +
                    DescribeFileFailures(result.Result.Counters.FailedFiles, result.Result.Errors));
            }
            else
            {
                var errorSummary = result.Result.Errors.Count > 0
                    ? string.Join("; ", result.Result.Errors.Select(err => err.Message))
                    : "sem detalhes adicionais";
                ShowResult(
                    false,
                    result.Result.State == PackageState.Paused
                        ? "Backup interrompido"
                        : "Backup não concluído",
                    $"O backup não foi concluído. {errorSummary}");
            }
        }
        catch (Exception ex)
        {
            ShowResult(false, "Falha no backup", ex.Message);
        }
    }

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
    private async Task<string> CaptureMachineLevelServicesAsync(string sourceVolumeRoot, string packageRoot)
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

        return lines.Count > 0 ? "\n\n" + string.Join("\n", lines) : string.Empty;
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

    private void RestoreConfirmBack_Click(object sender, RoutedEventArgs e) => ShowPage(RestoreSourcePage);

    private async void ConfirmRestore_Click(object sender, RoutedEventArgs e)
    {
        if (restorePackageRoot is null || restoreDestinationProfileRoot is null)
        {
            ShowPage(RestoreSourcePage);
            return;
        }

        var runCorporateCleanup = CorporateCleanupCheckBox.IsChecked == true;
        var operationId = Guid.NewGuid();
        var publicRoot = Path.Combine(
            Path.GetDirectoryName(restoreDestinationProfileRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? restoreDestinationProfileRoot,
            "Public");
        var operationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GRBKP", "operations", operationId.ToString("N"));

        ShowProgress("Restaurando…", $"Restaurando dados para {restoreDestinationProfileRoot}");
        try
        {
            var result = await orchestrator.RunOrResumeRestoreAsync(new RestoreRequest(
                operationId, restorePackageRoot, restoreDestinationProfileRoot, publicRoot, operationRoot));

            if (result.OverallState == RestoreState.Complete)
            {
                var moduleSummary = result.ModuleOutcome.Results.Count > 0
                    ? string.Join(
                        ", ",
                        result.ModuleOutcome.Results.Select(r => $"{r.ModuleId}: {r.Outcome}"))
                    : "nenhum módulo de aplicativo neste backup";
                var machineLevelSummary = await RestoreMachineLevelServicesAsync(
                    restorePackageRoot,
                    restoreMetadata?.Profile.OriginalPath,
                    restoreDestinationProfileRoot);
                var cleanupSummary = runCorporateCleanup
                    ? await ApplyCorporateCleanupAsync(restoreDestinationProfileRoot)
                    : string.Empty;
                ShowResult(
                    true,
                    "Restauração concluída",
                    $"{result.PersonalDataResult.Counters.CopiedFiles} arquivos pessoais • " +
                    $"{FormatBytes(result.PersonalDataResult.Counters.CopiedBytes)}\n\n" +
                    $"Aplicativos: {moduleSummary}\n\n" +
                    $"Dados restaurados para:\n{restoreDestinationProfileRoot}" +
                    DescribeFileFailures(
                        result.PersonalDataResult.Counters.FailedFiles,
                        result.PersonalDataResult.Errors) +
                    machineLevelSummary +
                    cleanupSummary +
                    "\n\nReinicie este computador quando for possível para concluir a migração.");
            }
            else
            {
                var failedModules = result.ModuleOutcome.Results
                    .Where(r => r.Outcome == ModuleOutcome.Failed)
                    .Select(r => r.ModuleId);
                var engineErrors = result.PersonalDataResult.Errors.Count > 0
                    ? "\n\n" + string.Join(
                        "\n", result.PersonalDataResult.Errors.Select(error => error.Message).Distinct())
                    : string.Empty;
                var detail = (result.OverallState == RestoreState.Failed && failedModules.Any()
                    ? $"Falha ao restaurar: {string.Join(", ", failedModules)}. " +
                      "Os dados anteriores desses aplicativos foram preservados no destino."
                    : "A restauração não foi concluída.") + engineErrors;
                var title = result.OverallState == RestoreState.Paused
                    ? "Restauração interrompida"
                    : "Restauração não concluída";
                ShowResult(false, title, detail);
            }
        }
        catch (Exception ex)
        {
            ShowResult(false, "Falha na restauração", ex.Message);
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
    private async Task<string> RestoreMachineLevelServicesAsync(
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

        return lines.Count > 0 ? "\n\n" + string.Join("\n", lines) : string.Empty;
    }

    /// <summary>
    /// Limpeza corporativa (OneDrive + Copilot) do computador de DESTINO — so acionada quando o
    /// tecnico deixou a caixa marcada e a restauracao de perfil ja terminou com sucesso. Melhor
    /// esforco, igual aos servicos de nivel de maquina: uma falha aqui aparece no resumo, mas nao
    /// reprova a restauracao.
    /// </summary>
    private async Task<string> ApplyCorporateCleanupAsync(string destinationProfileRoot)
    {
        try
        {
            var destinationVolumeRoot = Path.GetPathRoot(destinationProfileRoot) ?? destinationProfileRoot;
            var result = await corporateCleanupService.ApplyAsync(
                destinationVolumeRoot, destinationProfileRoot);
            return "\n\n" + string.Join("\n", result.Messages);
        }
        catch (Exception ex)
        {
            return $"\n\nLimpeza corporativa: falha ({ex.Message}).";
        }
    }

    /// <summary>
    /// Arquivos individuais que não puderam ser copiados (travados por outro processo, sem
    /// permissão, corrompidos) não derrubam mais a operação inteira — mas o técnico PRECISA ver
    /// que existiram, com exemplos de caminho, senão a resiliência viraria perda silenciosa.
    /// </summary>
    private static string DescribeFileFailures(
        long failedFiles, IReadOnlyList<OperationError> errors)
    {
        if (failedFiles <= 0)
        {
            return string.Empty;
        }

        var examples = errors
            .Where(error => !string.IsNullOrWhiteSpace(error.RelativePath))
            .Take(5)
            .Select(error => $"  • {error.RelativePath}");
        var remaining = failedFiles > 5 ? $"\n  • … e mais {failedFiles - 5}" : string.Empty;

        return $"\n\n⚠ {failedFiles} arquivo(s) não puderam ser copiados e ficaram de fora:\n" +
            string.Join("\n", examples) + remaining +
            "\nCausas comuns: arquivo aberto em outro programa, sem permissão de leitura, ou " +
            "danificado. Confira esses itens manualmente.";
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

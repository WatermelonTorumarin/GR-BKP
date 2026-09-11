using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using GRBKP.Core.Adapters;
using GRBKP.Core.Discovery;
using GRBKP.Core.Modules;
using GRBKP.Core.Operations;
using GRBKP.Core.Orchestration;
using GRBKP.Core.Packages;
using GRBKP.Core.Restore;
using GRBKP.Core.Selection;
using GRBKP.Core.Transfers;
using GRBKP.Windows;
using GRBKP.Windows.FileSystem;
using GRBKP.Windows.Processes;

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
    private readonly BackupPackageModule packageModule;
    private readonly StreamingBackupEngine backupEngine;
    private readonly IReadOnlyDictionary<string, IMigrationModule> modules;
    private readonly MigrationOrchestrator orchestrator;

    private string? sourceVolumeRoot;
    private readonly List<(DiscoveredProfile Profile, RadioButton Control)> profileOptions = [];

    private string? restorePackageRoot;
    private string? restoreDestinationProfileRoot;

    public MainWindow()
    {
        InitializeComponent();
        profileDiscovery = new WindowsProfileDiscovery(fileSystem);
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
                new SourceProfile(profile.Name, profile.ProfileRoot, null),
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
                ShowResult(
                    true,
                    "Backup concluído",
                    $"{result.Result.Counters.CopiedFiles} arquivos • " +
                    $"{FormatBytes(result.Result.Counters.CopiedBytes)}\n\n" +
                    $"Os dados foram salvos em:\n{packageRoot}");
            }
            else
            {
                var errorSummary = result.Result.Errors.Count > 0
                    ? string.Join("; ", result.Result.Errors.Select(err => err.Message))
                    : "sem detalhes adicionais";
                ShowResult(
                    false,
                    $"Backup {result.Result.State.ToString().ToLowerInvariant()}",
                    $"O backup não foi concluído. {errorSummary}");
            }
        }
        catch (Exception ex)
        {
            ShowResult(false, "Falha no backup", ex.Message);
        }
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
            RestoreConfirmationDetails.Text =
                $"Origem: {packageRoot}\n" +
                $"Perfil: {metadata.Profile.Name}\n" +
                $"Destino: {destinationRoot}\n" +
                $"Módulos no pacote: {(metadata.Modules.Count > 0 ? string.Join(", ", metadata.Modules) : "nenhum")}";
            ShowPage(RestoreConfirmPage);
        }
        catch (Exception ex)
        {
            RestoreSourceStatusText.Text = $"Não foi possível ler o backup em {packageRoot}: {ex.Message}";
        }
    }

    private void RestoreConfirmBack_Click(object sender, RoutedEventArgs e) => ShowPage(RestoreSourcePage);

    private async void ConfirmRestore_Click(object sender, RoutedEventArgs e)
    {
        if (restorePackageRoot is null || restoreDestinationProfileRoot is null)
        {
            ShowPage(RestoreSourcePage);
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
                ShowResult(
                    true,
                    "Restauração concluída",
                    $"{result.PersonalDataResult.Counters.CopiedFiles} arquivos pessoais • " +
                    $"{FormatBytes(result.PersonalDataResult.Counters.CopiedBytes)}\n\n" +
                    $"Aplicativos: {moduleSummary}\n\n" +
                    $"Dados restaurados para:\n{restoreDestinationProfileRoot}");
            }
            else
            {
                var failedModules = result.ModuleOutcome.Results
                    .Where(r => r.Outcome == ModuleOutcome.Failed)
                    .Select(r => r.ModuleId);
                var detail = result.OverallState == RestoreState.Failed && failedModules.Any()
                    ? $"Falha ao restaurar: {string.Join(", ", failedModules)}. " +
                      "Os dados anteriores desses aplicativos foram preservados no destino."
                    : "A restauração não foi concluída.";
                ShowResult(false, $"Restauração {result.OverallState.ToString().ToLowerInvariant()}", detail);
            }
        }
        catch (Exception ex)
        {
            ShowResult(false, "Falha na restauração", ex.Message);
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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using D365SdkGenerator.Models;
using D365SdkGenerator.Services;

namespace D365SdkGenerator.App;

public partial class MainWindow : Window
{
    private readonly SettingsService settingsService = new();
    private readonly PreflightService preflightService = new();
    private readonly ObservableCollection<LogRow> logRows = [];
    private readonly DispatcherTimer preflightTimer;
    private readonly AppPreferences preferences;

    private GeneratorSettings? settings;
    private CancellationTokenSource? generationCancellation;
    private bool isRunning;
    private bool isReady;
    private bool suppressUpdates;
    private bool closeAfterGeneration;
    private int preflightVersion;
    private string? generatedProjectDirectory;
    private string? generatedSolutionFile;
    private string? lastGeneratedTarget;

    public MainWindow()
    {
        InitializeComponent();

        preferences = AppPreferences.Load();
        LogList.ItemsSource = logRows;
        logRows.CollectionChanged += OnLogRowsChanged;

        preflightTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };

        preflightTimer.Tick += OnPreflightTimerTick;
    }

    private SdkVersion? SelectedSdk => SdkCombo.SelectedItem as SdkVersion;

    // ------------------------------------------------------------------ startup

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            settings = await settingsService.LoadAsync(SettingsService.LocateCatalogFile());
        }
        catch (Exception ex)
        {
            FailStartup($"sdk-catalog.json could not be loaded. {ex.Message}");
            return;
        }

        if (settings.Versions.Count == 0)
        {
            FailStartup("sdk-catalog.json does not contain any SDK versions.");
            return;
        }

        suppressUpdates = true;

        SdkCombo.ItemsSource = settings.Versions;
        SdkCombo.SelectedItem = SelectInitialSdk();

        ProjectNameBox.Text = Coalesce(
            preferences.ProjectName,
            ProjectRules.DefaultProjectName);

        PublisherBox.Text = Coalesce(
            preferences.Publisher,
            ProjectRules.DefaultPublisher);

        OutputBox.Text = Coalesce(
            preferences.OutputDirectory,
            ProjectRules.GetDefaultOutputDirectory());

        GitCheck.IsChecked = preferences.InitializeGit;

        suppressUpdates = false;
        isReady = true;

        UpdateDerivedState();
        await RefreshPreflightAsync();
    }

    private SdkVersion SelectInitialSdk()
    {
        var versions = settings!.Versions;

        if (!string.IsNullOrWhiteSpace(preferences.SdkVersion))
        {
            var remembered = versions.FirstOrDefault(version =>
                version.Version.Equals(preferences.SdkVersion, StringComparison.OrdinalIgnoreCase));

            if (remembered is not null)
            {
                return remembered;
            }
        }

        var concrete = versions
            .Where(version => !version.Version.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return concrete.Count > 0 ? concrete[^1] : versions[^1];
    }

    private void FailStartup(string message)
    {
        SetBusyState(false);

        AppendLog(GeneratorLogLevel.Error, message);
        StatusText.Text = "Cannot start";

        SdkCombo.IsEnabled = false;
        ProjectNameBox.IsEnabled = false;
        PublisherBox.IsEnabled = false;
        OutputBox.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        GitCheck.IsEnabled = false;
        GenerateButton.IsEnabled = false;
    }

    // ------------------------------------------------------------------ form state

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (suppressUpdates)
        {
            return;
        }

        UpdateDerivedState();
        SchedulePreflight();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (suppressUpdates)
        {
            return;
        }

        UpdateDerivedState();
        SchedulePreflight();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (suppressUpdates)
        {
            return;
        }

        UpdateDerivedState();
    }

    private void UpdateDerivedState(bool updateStatus = true)
    {
        if (!isReady)
        {
            return;
        }

        var sdk = SelectedSdk;
        SdkPill.Text = sdk?.Version ?? "SDK";

        var projectName = ProjectNameBox.Text.Trim();
        var publisher = PublisherBox.Text.Trim();
        var output = OutputBox.Text.Trim();

        var nameError = ProjectRules.ValidateProjectName(projectName);
        var publisherError = ProjectRules.ValidatePublisher(publisher);
        var outputError = ProjectRules.ValidateOutputDirectory(output);

        var targetDirectory = nameError is null && outputError is null
            ? ProjectRules.GetProjectDirectory(output, projectName)
            : null;

        // Right after a successful run the target exists by definition. That is not an
        // error, so it disables Generate without a red message.
        var isCompletedTarget =
            targetDirectory is not null &&
            lastGeneratedTarget is not null &&
            targetDirectory.Equals(lastGeneratedTarget, StringComparison.OrdinalIgnoreCase);

        if (targetDirectory is not null &&
            !isCompletedTarget &&
            ProjectRules.TargetDirectoryIsOccupied(output, projectName))
        {
            outputError =
                $"'{projectName}' already exists in this folder. Choose another name or output folder.";
        }

        SetFieldError(ProjectNameError, nameError);
        SetFieldError(PublisherError, publisherError);
        SetFieldError(OutputError, outputError);

        if (targetDirectory is not null)
        {
            CreatesText.Text = isCompletedTarget
                ? "Generated. Change the project name to create another."
                : targetDirectory;

            CreatesText.ToolTip = targetDirectory;
        }
        else
        {
            CreatesText.Text = "Complete the fields above";
            CreatesText.ToolTip = null;
        }

        GenerateButton.IsEnabled =
            !isRunning &&
            !isCompletedTarget &&
            sdk is not null &&
            nameError is null &&
            publisherError is null &&
            outputError is null;

        if (!isRunning && updateStatus)
        {
            StatusText.Text = GenerateButton.IsEnabled
                ? "Ready"
                : "Complete the required fields";
        }
    }

    private static void SetFieldError(System.Windows.Controls.TextBlock target, string? message)
    {
        target.Text = message ?? string.Empty;
        target.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private bool TryBuildConfiguration(out ProjectConfiguration configuration)
    {
        configuration = null!;

        var sdk = SelectedSdk;
        var projectName = ProjectNameBox.Text.Trim();
        var publisher = PublisherBox.Text.Trim();
        var output = OutputBox.Text.Trim();

        if (sdk is null ||
            ProjectRules.ValidateProjectName(projectName) is not null ||
            ProjectRules.ValidatePublisher(publisher) is not null ||
            ProjectRules.ValidateOutputDirectory(output) is not null ||
            ProjectRules.TargetDirectoryIsOccupied(output, projectName))
        {
            return false;
        }

        configuration = new ProjectConfiguration
        {
            Sdk = sdk,
            ProjectName = projectName,
            Publisher = publisher,
            OutputDirectory = output,
            IncludePos = true,
            IncludeScaleUnit = true,
            InitializeGit = GitCheck.IsChecked == true
        };

        return true;
    }

    private void SetBusyState(bool busy)
    {
        isRunning = busy;

        Progress.IsIndeterminate = busy;
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.Content = busy ? "Generating…" : "Generate project";
        GenerateButton.IsEnabled = false;

        SdkCombo.IsEnabled = !busy;
        ProjectNameBox.IsEnabled = !busy;
        PublisherBox.IsEnabled = !busy;
        OutputBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        GitCheck.IsEnabled = !busy;

        if (busy)
        {
            StatusText.Text = "Generating…";
        }
    }

    // ------------------------------------------------------------------ preflight

    private void SchedulePreflight()
    {
        preflightTimer.Stop();
        preflightTimer.Start();
    }

    private async void OnPreflightTimerTick(object? sender, EventArgs e)
    {
        preflightTimer.Stop();
        await RefreshPreflightAsync();
    }

    private async Task RefreshPreflightAsync()
    {
        var version = ++preflightVersion;

        if (!isReady || !TryBuildConfiguration(out var configuration))
        {
            PreflightList.ItemsSource = null;
            PreflightPanel.Visibility = Visibility.Collapsed;
            return;
        }

        IReadOnlyList<PreflightCheck> checks;

        try
        {
            checks = await preflightService.RunAsync(configuration);
        }
        catch
        {
            // A probe failure must never block generation.
            return;
        }

        // A newer request has already been queued; discard this stale result.
        if (version != preflightVersion || !isReady)
        {
            return;
        }

        PreflightList.ItemsSource = checks.Select(PreflightRow.FromCheck).ToList();
        PreflightPanel.Visibility = checks.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        ShortRootButton.Visibility = checks.Any(check => check.Id == "long-path")
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnUseShortRootClick(object sender, RoutedEventArgs e)
    {
        OutputBox.Text = ProjectRules.PreferredShortOutputRoot;
        OutputBox.CaretIndex = OutputBox.Text.Length;
        OutputBox.Focus();
    }

    // ------------------------------------------------------------------ generation

    private async void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (isRunning || !TryBuildConfiguration(out var configuration))
        {
            UpdateDerivedState();
            return;
        }

        SetBusyState(true);
        logRows.Clear();

        OpenFolderButton.Visibility = Visibility.Collapsed;
        OpenSolutionButton.Visibility = Visibility.Collapsed;

        generatedProjectDirectory = ProjectRules.GetProjectDirectory(
            configuration.OutputDirectory,
            configuration.ProjectName);

        generatedSolutionFile = ProjectRules.GetSolutionFile(
            generatedProjectDirectory,
            configuration.ProjectName);

        var log = new DelegateGeneratorLog(entry =>
            _ = Dispatcher.BeginInvoke(() => AppendLog(entry)));

        var generator = new SdkGeneratorService(
            new TemplateSourceService(log),
            new TemplateService(),
            new ProjectIdentityService(log),
            new BuildSettingsService(log),
            new SolutionService(),
            new ProjectValidator(log),
            new GitService(),
            log);

        AppendLog(
            GeneratorLogLevel.Info,
            $"Generating {configuration.ProjectName} with Commerce SDK {configuration.Sdk.Version}");

        AppendLog(
            GeneratorLogLevel.Info,
            $"Store Commerce {configuration.Sdk.StoreCommerce.Branch} · " +
            $"Scale Unit {configuration.Sdk.ScaleUnit.Branch}");

        AppendLog(GeneratorLogLevel.Info, $"Output: {generatedProjectDirectory}");
        AppendLog(GeneratorLogLevel.Info, string.Empty);

        generationCancellation = new CancellationTokenSource();
        var cancellationToken = generationCancellation.Token;

        try
        {
            await Task.Run(
                () => generator.GenerateAsync(configuration, cancellationToken),
                cancellationToken);

            AppendLog(GeneratorLogLevel.Info, string.Empty);
            AppendLog(GeneratorLogLevel.Success, "Project generated successfully.");
            AppendLog(GeneratorLogLevel.Info, $"Location : {generatedProjectDirectory}");
            AppendLog(GeneratorLogLevel.Info, $"Solution : {generatedSolutionFile}");

            StatusText.Text = "Project generated successfully.";
            OpenFolderButton.Visibility = Visibility.Visible;
            OpenSolutionButton.Visibility = Visibility.Visible;
            lastGeneratedTarget = generatedProjectDirectory;

            preferences.ProjectName = configuration.ProjectName;
            preferences.Publisher = configuration.Publisher;
            preferences.OutputDirectory = configuration.OutputDirectory;
            preferences.SdkVersion = configuration.Sdk.Version;
            preferences.InitializeGit = configuration.InitializeGit;
            preferences.Save();
        }
        catch (OperationCanceledException)
        {
            AppendLog(
                GeneratorLogLevel.Warning,
                "Generation cancelled. Any partially generated project was removed.");

            StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog(GeneratorLogLevel.Error, ex.Message);
            StatusText.Text = "Generation failed";
        }
        finally
        {
            generationCancellation?.Dispose();
            generationCancellation = null;

            SetBusyState(false);
            UpdateDerivedState(updateStatus: false);

            if (closeAfterGeneration)
            {
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling…";
        generationCancellation?.Cancel();
    }

    // ------------------------------------------------------------------ activity

    private void OnLogRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        EmptyLogHint.Visibility = logRows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (logRows.Count == 0)
        {
            return;
        }

        var last = logRows[^1];
        _ = Dispatcher.BeginInvoke(
            () => LogList.ScrollIntoView(last),
            DispatcherPriority.Background);
    }

    private void AppendLog(GeneratorLogEntry entry) => AppendLog(entry.Level, entry.Message, entry.Timestamp);

    private void AppendLog(GeneratorLogLevel level, string message) =>
        AppendLog(level, message, DateTimeOffset.Now);

    private void AppendLog(GeneratorLogLevel level, string message, DateTimeOffset timestamp)
    {
        logRows.Add(new LogRow(
            timestamp.ToLocalTime().ToString("HH:mm:ss"),
            message,
            BrushFor(level)));
    }

    private static Brush BrushFor(GeneratorLogLevel level) => level switch
    {
        GeneratorLogLevel.Success => CreateBrush("#0E7A3D"),
        GeneratorLogLevel.Warning => CreateBrush("#8A5A00"),
        GeneratorLogLevel.Error => CreateBrush("#C42B1C"),
        _ => CreateBrush("#3B4553")
    };

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => logRows.Clear();

    // ------------------------------------------------------------------ shell actions

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the output folder",
            Multiselect = false
        };

        var current = OutputBox.Text.Trim();

        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true &&
            !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            OutputBox.Text = dialog.FolderName;
            OutputBox.CaretIndex = OutputBox.Text.Length;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(generatedProjectDirectory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{generatedProjectDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog(GeneratorLogLevel.Warning, $"Could not open the folder. {ex.Message}");
        }
    }

    private void OnOpenSolutionClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(generatedSolutionFile) ||
            !File.Exists(generatedSolutionFile))
        {
            AppendLog(
                GeneratorLogLevel.Warning,
                $"The solution file was not found at '{generatedSolutionFile}'.");

            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = generatedSolutionFile,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog(
                GeneratorLogLevel.Warning,
                $"Could not open the solution. Open it from Visual Studio instead. {ex.Message}");
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (isRunning)
        {
            // Let the pipeline roll back its partial output before the process exits.
            e.Cancel = true;
            closeAfterGeneration = true;
            StatusText.Text = "Cancelling…";
            generationCancellation?.Cancel();

            return;
        }

        preferences.ProjectName = ProjectNameBox.Text.Trim();
        preferences.Publisher = PublisherBox.Text.Trim();
        preferences.OutputDirectory = OutputBox.Text.Trim();
        preferences.SdkVersion = SelectedSdk?.Version;
        preferences.InitializeGit = GitCheck.IsChecked == true;
        preferences.Save();
    }

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    // ------------------------------------------------------------------ view rows

    private sealed record LogRow(string Time, string Message, Brush Foreground);

    private sealed record PreflightRow(
        string Title,
        string Detail,
        string? Fix,
        Brush Accent,
        Brush Backdrop,
        Visibility FixVisibility)
    {
        public static PreflightRow FromCheck(PreflightCheck check)
        {
            var (accent, backdrop) = check.Severity switch
            {
                PreflightSeverity.Error => ("#C42B1C", "#FDF1EF"),
                PreflightSeverity.Warning => ("#8A5A00", "#FFF7E3"),
                _ => ("#0F6CBD", "#F1F7FD")
            };

            return new PreflightRow(
                check.Title,
                check.Detail,
                check.Fix,
                CreateBrush(accent),
                CreateBrush(backdrop),
                string.IsNullOrWhiteSpace(check.Fix)
                    ? Visibility.Collapsed
                    : Visibility.Visible);
        }
    }
}

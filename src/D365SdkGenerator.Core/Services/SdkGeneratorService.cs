using System.Text.Json;
using System.Text.RegularExpressions;
using D365SdkGenerator.Models;

namespace D365SdkGenerator.Services;

public sealed class SdkGeneratorService
{
    private readonly TemplateSourceService sourceService;
    private readonly TemplateService templateService;
    private readonly ProjectIdentityService identityService;
    private readonly BuildSettingsService buildSettings;
    private readonly SolutionService solutionService;
    private readonly ProjectValidator validator;
    private readonly GitService git;
    private readonly IGeneratorLog log;

    public SdkGeneratorService(
        TemplateSourceService sourceService,
        TemplateService templateService,
        ProjectIdentityService identityService,
        BuildSettingsService buildSettings,
        SolutionService solutionService,
        ProjectValidator validator,
        GitService git,
        IGeneratorLog? log = null)
    {
        this.sourceService = sourceService;
        this.templateService = templateService;
        this.identityService = identityService;
        this.buildSettings = buildSettings;
        this.solutionService = solutionService;
        this.validator = validator;
        this.git = git;
        this.log = log ?? NullGeneratorLog.Instance;
    }

    public async Task GenerateAsync(
        ProjectConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var outputRoot = Path.GetFullPath(configuration.OutputDirectory);
        var projectDirectory = Path.Combine(outputRoot, configuration.ProjectName);

        if (Directory.Exists(projectDirectory) &&
            Directory.EnumerateFileSystemEntries(projectDirectory).Any())
        {
            throw new IOException(
                $"Target project directory already exists and is not empty: {projectDirectory}");
        }

        Directory.CreateDirectory(projectDirectory);

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "D365SdkGenerator",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempRoot);

        try
        {
            var storeCommerceSource = await sourceService.DownloadAndExtractAsync(
                configuration.Sdk.StoreCommerce,
                tempRoot,
                "Store Commerce",
                cancellationToken);

            var storeCommerceRoot = Path.Combine(
                projectDirectory,
                "src",
                "PackagingSamples",
                "StoreCommerceApp");

            log.Info("Copying Store Commerce packaging sample...");
            await templateService.CopyDirectoryAsync(
                storeCommerceSource,
                storeCommerceRoot,
                cancellationToken);

            await CopyRepositoryMetadataAsync(
                storeCommerceSource,
                projectDirectory,
                cancellationToken);

            // Must happen before any "dotnet" command runs inside the generated tree.
            log.Info("Checking .NET SDK compatibility...");
            var alignedSdk = await solutionService.EnsureSdkCompatibleAsync(
                projectDirectory,
                cancellationToken);

            if (alignedSdk is not null)
            {
                log.Warning(
                    $"global.json aligned to .NET SDK {alignedSdk} (template pin is not installed)");
            }

            if (configuration.IncludeScaleUnit)
            {
                var scaleUnitSource = await sourceService.DownloadAndExtractAsync(
                    configuration.Sdk.ScaleUnit,
                    tempRoot,
                    "Scale Unit",
                    cancellationToken);

                await CopyScaleUnitProjectsAsync(
                    scaleUnitSource,
                    storeCommerceRoot,
                    cancellationToken);
            }

            await identityService.ApplyAsync(
                projectDirectory,
                storeCommerceRoot,
                configuration,
                cancellationToken);

            // Building must not deploy: the Commerce SDK would otherwise run the Store
            // Commerce installer after every Debug build started from Visual Studio.
            await buildSettings.ApplyAsync(projectDirectory, cancellationToken);

            var solutionFile = FindSolutionFile(
                storeCommerceRoot,
                configuration.ProjectName);

            if (solutionFile is null)
            {
                throw new FileNotFoundException(
                    $"Could not find '{configuration.ProjectName}.sln' after customization.");
            }

            if (configuration.IncludeScaleUnit)
            {
                var scaleUnitProject = FindSingleProjectFile(
                    Path.Combine(storeCommerceRoot, "ScaleUnit"),
                    "Scale Unit");

                var scaleUnitInstaller = FindSingleProjectFile(
                    Path.Combine(storeCommerceRoot, "ScaleUnit.Installer"),
                    "Scale Unit Installer");

                log.Info("Adding Scale Unit projects to the solution...");
                await solutionService.AddProjectsAsync(
                    solutionFile,
                    new[] { scaleUnitProject, scaleUnitInstaller },
                    cancellationToken);
            }

            await validator.ValidateAsync(
                projectDirectory,
                storeCommerceRoot,
                solutionFile,
                configuration,
                cancellationToken);

            var metadata = new
            {
                generator = "D365SdkGenerator",
                generatedUtc = DateTimeOffset.UtcNow,
                sdkVersion = configuration.Sdk.Version,
                projectName = configuration.ProjectName,
                publisher = configuration.Publisher,
                namespaceRoot = configuration.NamespaceRoot,
                storeCommercePackageName = configuration.ProjectName,
                scaleUnitPackageName = configuration.IncludeScaleUnit
                    ? configuration.ScaleUnitPackageName
                    : null,
                commerceSdkPackagesVersion = TryReadCommerceSdkPackagesVersion(projectDirectory),
                sdkAlignedTo = alignedSdk,
                installStoreCommerceExtensionsAfterBuild = false,
                includePos = configuration.IncludePos,
                includeScaleUnit = configuration.IncludeScaleUnit,
                storeCommerceRepository = configuration.Sdk.StoreCommerce.Repository,
                storeCommerceBranch = configuration.Sdk.StoreCommerce.Branch,
                scaleUnitRepository = configuration.Sdk.ScaleUnit.Repository,
                scaleUnitBranch = configuration.Sdk.ScaleUnit.Branch
            };

            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, ".d365sdk.json"),
                JsonSerializer.Serialize(
                    metadata,
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            if (configuration.InitializeGit)
            {
                log.Info("Initializing Git repository...");
                await git.InitAsync(projectDirectory, cancellationToken);
            }
        }
        catch
        {
            // A rollback failure must never hide the error that caused it.
            try
            {
                if (Directory.Exists(projectDirectory))
                {
                    Directory.Delete(projectDirectory, recursive: true);
                }
            }
            catch (Exception cleanupError)
            {
                log.Warning(
                    $"Could not remove the partial project at '{projectDirectory}': " +
                    cleanupError.Message);
            }

            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch (Exception cleanupError)
            {
                log.Warning(
                    $"Could not remove the temporary folder '{tempRoot}': " +
                    cleanupError.Message);
            }
        }
    }

    private static string FindSingleProjectFile(
        string directory,
        string description)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"{description} directory was not found: {directory}");
        }

        var projects = Directory
            .EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .ToList();

        if (projects.Count == 1)
        {
            return projects[0];
        }

        if (projects.Count == 0)
        {
            throw new FileNotFoundException(
                $"No .csproj file was found inside {description}: {directory}");
        }

        throw new InvalidOperationException(
            $"Multiple .csproj files were found inside {description}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, projects));
    }

    private async Task CopyScaleUnitProjectsAsync(
        string scaleUnitSourceRoot,
        string storeCommerceRoot,
        CancellationToken cancellationToken)
    {
        var scaleUnitSource = Path.Combine(scaleUnitSourceRoot, "ScaleUnit");
        var installerSource = Path.Combine(scaleUnitSourceRoot, "Installer");

        if (!Directory.Exists(scaleUnitSource))
        {
            throw new DirectoryNotFoundException(
                $"Scale Unit template folder was not found: {scaleUnitSource}");
        }

        if (!Directory.Exists(installerSource))
        {
            throw new DirectoryNotFoundException(
                $"Scale Unit installer template folder was not found: {installerSource}");
        }

        log.Info("Copying Scale Unit project...");
        await templateService.CopyDirectoryAsync(
            scaleUnitSource,
            Path.Combine(storeCommerceRoot, "ScaleUnit"),
            cancellationToken);

        log.Info("Copying Scale Unit installer project...");
        await templateService.CopyDirectoryAsync(
            installerSource,
            Path.Combine(storeCommerceRoot, "ScaleUnit.Installer"),
            cancellationToken);
    }

    private static async Task CopyRepositoryMetadataAsync(
        string storeCommerceSourceRoot,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        // Not every branch ships a global.json (9.59 removed it), so the repository root
        // is identified by the files that are always present.
        var repositoryRoot = FindRepositoryRoot(
            storeCommerceSourceRoot,
            "nuget.config",
            "repo.props");

        foreach (var fileName in new[] { "global.json", "nuget.config", "repo.props" })
        {
            var source = Path.Combine(repositoryRoot, fileName);

            if (!File.Exists(source))
            {
                if (fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase))
                {
                    // Without global.json the installed SDK is used as-is.
                    continue;
                }

                throw new FileNotFoundException(
                    $"Required repository metadata file '{fileName}' was not found.");
            }

            var destination = Path.Combine(projectDirectory, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var input = File.OpenRead(source);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static string? TryReadCommerceSdkPackagesVersion(string projectDirectory)
    {
        var repoProps = Path.Combine(projectDirectory, "repo.props");
        if (!File.Exists(repoProps))
        {
            return null;
        }

        var match = Regex.Match(
            File.ReadAllText(repoProps),
            "<CommerceSdkPackagesVersion>([^<]+)</CommerceSdkPackagesVersion>");

        return match.Success ? match.Groups[1].Value : null;
    }

    private static string FindRepositoryRoot(
        string sourceRoot,
        params string[] requiredFiles)
    {
        var current = new DirectoryInfo(sourceRoot);

        while (current is not null)
        {
            if (requiredFiles.All(file =>
                    File.Exists(Path.Combine(current.FullName, file))))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Store Commerce repository root metadata.");
    }

    private static string? FindSolutionFile(
        string root,
        string projectName)
    {
        return Directory
            .EnumerateFiles(root, "*.sln", SearchOption.AllDirectories)
            .FirstOrDefault(file =>
                Path.GetFileNameWithoutExtension(file)
                    .Equals(projectName, StringComparison.OrdinalIgnoreCase));
    }
}

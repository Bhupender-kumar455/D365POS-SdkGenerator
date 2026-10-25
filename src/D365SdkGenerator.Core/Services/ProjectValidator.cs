using System.Text.RegularExpressions;
using D365SdkGenerator.Models;

namespace D365SdkGenerator.Services;

public sealed class ProjectValidator
{
    private static readonly string[] LegacyMarkers =
    [
        "Contoso.StoreCommercePackagingSamples",
        "Contoso.StoreCommercePackagingSample"
    ];

    private readonly IGeneratorLog log;

    public ProjectValidator(IGeneratorLog? log = null) =>
        this.log = log ?? NullGeneratorLog.Instance;

    public async Task ValidateAsync(
        string projectRoot,
        string storeCommerceRoot,
        string solutionFile,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        log.Info("Validating generated solution...");

        var projectFiles = new List<string>
        {
            Path.Combine(storeCommerceRoot, "ChannelDatabase", $"{configuration.ProjectName}.ChannelDatabase.csproj"),
            Path.Combine(storeCommerceRoot, "CommerceRuntime", $"{configuration.ProjectName}.CommerceRuntime.csproj"),
            Path.Combine(storeCommerceRoot, "HardwareStation", $"{configuration.ProjectName}.HardwareStation.csproj"),
            Path.Combine(storeCommerceRoot, "Pos", $"{configuration.ProjectName}.Pos.csproj"),
            Path.Combine(storeCommerceRoot, "StoreCommerce.Installer", $"{configuration.ProjectName}.StoreCommerce.Installer.csproj")
        };

        var scaleUnitProject = Path.Combine(
            storeCommerceRoot,
            "ScaleUnit",
            $"{configuration.ProjectName}.ScaleUnit.csproj");

        var scaleUnitInstaller = Path.Combine(
            storeCommerceRoot,
            "ScaleUnit.Installer",
            $"{configuration.ProjectName}.ScaleUnit.Installer.csproj");

        if (configuration.IncludeScaleUnit)
        {
            projectFiles.Add(scaleUnitProject);
            projectFiles.Add(scaleUnitInstaller);
        }

        var requiredFiles = new List<string>
        {
            Path.Combine(projectRoot, "nuget.config"),
            Path.Combine(projectRoot, "repo.props"),
            Path.Combine(storeCommerceRoot, "CustomizationPackage.props"),
            solutionFile
        };

        // global.json is optional: release/9.59 no longer ships one, and without it the
        // installed .NET SDK is used directly.

        requiredFiles.AddRange(projectFiles);

        foreach (var file in requiredFiles)
        {
            if (!File.Exists(file))
            {
                throw new InvalidOperationException(
                    $"Generated SDK is missing required file: {file}");
            }
        }

        await ValidateNoLegacyIdentityAsync(
            projectRoot,
            cancellationToken);

        await ValidateSolutionEntriesAsync(
            solutionFile,
            projectFiles,
            cancellationToken);

        if (configuration.IncludeScaleUnit)
        {
            await ValidateProjectReferencesAsync(
                scaleUnitProject,
                cancellationToken);

            await ValidateProjectReferencesAsync(
                scaleUnitInstaller,
                cancellationToken);
        }

        log.Success("Required files  : OK");
        log.Success("Legacy names    : OK");
        log.Success("Solution entries: OK");
        log.Success("Project refs    : OK");
    }

    private static async Task ValidateNoLegacyIdentityAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(
                     projectRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Same predicate the identity pass uses, so checks and rewrites cannot drift.
            if (!TextFileClassifier.IsText(file) ||
                TextFileClassifier.IsGitPath(file) ||
                TextFileClassifier.IsBuildOutputPath(file))
            {
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken);
            }
            catch
            {
                continue;
            }

            foreach (var marker in LegacyMarkers)
            {
                if (content.Contains(marker, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Legacy template identity '{marker}' remains in '{file}'.");
                }
            }
        }
    }

    private static async Task ValidateSolutionEntriesAsync(
        string solutionFile,
        IEnumerable<string> projectFiles,
        CancellationToken cancellationToken)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionFile)!;
        var solutionContent = await File.ReadAllTextAsync(solutionFile, cancellationToken);
        var projects = projectFiles.ToList();

        foreach (var projectFile in projects)
        {
            var relativePath = Path
                .GetRelativePath(solutionDirectory, projectFile)
                .Replace('/', '\\');

            if (!solutionContent.Contains(relativePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The solution '{solutionFile}' does not contain '{relativePath}'. " +
                    "'dotnet sln add' did not add every generated project.");
            }
        }

        ValidateProjectsAreAtSolutionRoot(
            solutionContent,
            solutionDirectory,
            projects);
    }

    private static readonly Regex SolutionProjectEntry = new(
        "Project\\(\"[^\"]+\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]*)\",\\s*\"(?<guid>\\{[0-9A-Fa-f-]+\\})\"",
        RegexOptions.IgnoreCase);

    private static readonly Regex SolutionNestedEntry = new(
        "^\\s*(?<project>\\{[0-9A-Fa-f-]+\\})\\s*=\\s*(?<folder>\\{[0-9A-Fa-f-]+\\})\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// "dotnet sln add" nests newly added projects in a solution folder named after their
    /// parent directory. The generated solution must keep every project at the root.
    /// Only GlobalSection(NestedProjects) describes solution folders; project sections can
    /// contain "{GUID} = {GUID}" dependency lines that must not be interpreted as nesting.
    /// </summary>
    private static void ValidateProjectsAreAtSolutionRoot(
        string solutionContent,
        string solutionDirectory,
        IEnumerable<string> projectFiles)
    {
        var targets = projectFiles
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var projectGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in SolutionProjectEntry.Matches(solutionContent))
        {
            var projectPath = Path.GetFullPath(Path.Combine(
                solutionDirectory,
                match.Groups["path"].Value
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar)));

            if (targets.Contains(projectPath))
            {
                projectGuids.Add(match.Groups["guid"].Value);
            }
        }

        var nestedSection = FindNestedProjectsSection(solutionContent);
        if (nestedSection is null)
        {
            return;
        }

        foreach (Match match in SolutionNestedEntry.Matches(nestedSection))
        {
            if (projectGuids.Contains(match.Groups["project"].Value))
            {
                throw new InvalidOperationException(
                    "A generated project is nested inside a solution folder. " +
                    "Every project must be placed at the solution root.");
            }
        }
    }

    private static string? FindNestedProjectsSection(string solutionContent)
    {
        var headerIndex = solutionContent.IndexOf(
            "GlobalSection(NestedProjects)",
            StringComparison.OrdinalIgnoreCase);

        if (headerIndex < 0)
        {
            return null;
        }

        var endIndex = solutionContent.IndexOf(
            "EndGlobalSection",
            headerIndex,
            StringComparison.OrdinalIgnoreCase);

        if (endIndex < 0)
        {
            return null;
        }

        return solutionContent[headerIndex..endIndex];
    }

    private static async Task ValidateProjectReferencesAsync(
        string projectFile,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(projectFile, cancellationToken);
        var projectDirectory = Path.GetDirectoryName(projectFile)!;

        var matches = Regex.Matches(
            content,
            @"<ProjectReference\s+Include=[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            var include = match.Groups[1].Value;
            if (include.Contains("$(", StringComparison.Ordinal))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(
                Path.Combine(
                    projectDirectory,
                    include.Replace('/', Path.DirectorySeparatorChar)));

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException(
                    $"Broken project reference in '{projectFile}': {include}");
            }
        }
    }
}

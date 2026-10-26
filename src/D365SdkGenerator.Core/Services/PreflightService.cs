using System.Text.RegularExpressions;
using D365SdkGenerator.Models;

namespace D365SdkGenerator.Services;

public enum PreflightSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>
/// One environment finding shown in the UI before generation starts. <see cref="Id"/> is a
/// stable identifier the UI uses to offer a targeted fix (for example "long-path").
/// </summary>
public sealed record PreflightCheck(
    string Id,
    PreflightSeverity Severity,
    string Title,
    string Detail,
    string? Fix = null);

public enum RequirementState
{
    Present,
    Missing
}

/// <summary>
/// A tool the generated solution needs in order to build. Unlike <see cref="PreflightCheck"/>
/// these are reported whether or not something is wrong, so the window can show the whole
/// toolchain at a glance instead of only the problems.
/// </summary>
public sealed record BuildRequirement(
    string Id,
    string Name,
    RequirementState State,
    string? Version,
    string Detail,
    string? Fix = null,
    string? ActionUrl = null,
    string ActionLabel = "Download");

/// <summary>
/// Catches the failures that are otherwise only discovered after a ten-minute build:
/// long paths, a missing TypeScript compiler, or the .NET 10 proxy-generator requirement
/// of Commerce SDK 9.59 and later.
/// </summary>
public sealed class PreflightService
{
    private const int LongPathWarningThreshold = 240;

    private static readonly Regex VersionPattern = new(
        @"\d+\.\d+(?:\.\d+)?",
        RegexOptions.Compiled);

    // The POS proxy generator of 9.59+ is a .NET 10 application; the build falls back to
    // the .NET 8 copy when no .NET 10 runtime is installed, and that copy cannot load the
    // .NET 10 RetailServerLibrary that ships in the package.
    private static readonly Version DotNet10 = new(10, 0);

    // Probing spawns processes, and the UI re-runs preflight while the user types.
    private static readonly TimeSpan ProbeCacheLifetime = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, (DateTime At, string? Value)> versionCache =
        new(StringComparer.OrdinalIgnoreCase);

    private (DateTime At, bool Known, string? Version) runtimeCache;

    public async Task<IReadOnlyList<PreflightCheck>> RunAsync(
        ProjectConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var checks = new List<PreflightCheck>();

        var dotnetInstalled = await CheckDotNetSdkAsync(checks, cancellationToken);

        CheckLongPaths(configuration, checks);

        await CheckNodeJsAsync(checks, cancellationToken);

        if (Version.TryParse(configuration.Sdk.Version, out var sdkVersion) &&
            sdkVersion >= new Version(9, 59))
        {
            if (OperatingSystem.IsWindows() && !ProjectRules.IsExecutableOnPath("pwsh.exe"))
            {
                checks.Add(new PreflightCheck(
                    "pwsh",
                    PreflightSeverity.Warning,
                    "PowerShell 7 is not installed",
                    "Commerce SDK 9.59 and later run the POS proxy generator through pwsh, " +
                    "so the generated POS project will not build without it.",
                    "winget install --id Microsoft.PowerShell"));
            }

            if (dotnetInstalled && await HasDotNet10RuntimeAsync(cancellationToken) == false)
            {
                checks.Add(new PreflightCheck(
                    "dotnet-10",
                    PreflightSeverity.Warning,
                    ".NET 10 runtime not found",
                    $"Commerce SDK {configuration.Sdk.Version} ships a .NET 10 POS proxy generator. " +
                    "Without the .NET 10 runtime the build falls back to the .NET 8 generator, which " +
                    "fails with 'No types could be loaded ... IEdmModelFactory'.",
                    "winget install --id Microsoft.DotNet.AspNetCore.10"));
            }
        }

        return checks;
    }

    /// <summary>
    /// Describes the tools required to build a generated project. Missing tools are reported
    /// even when generation itself can continue, because the build runs outside the generator.
    /// </summary>
    public async Task<IReadOnlyList<BuildRequirement>> GetBuildRequirementsAsync(
        SdkVersion? sdk,
        string? outputDirectory,
        string? projectName,
        bool initializeGit = false,
        CancellationToken cancellationToken = default)
    {
        var requirements = new List<BuildRequirement>();

        var dotnetVersion = await GetToolVersionAsync(
            "dotnet",
            "--version",
            null,
            cancellationToken);
        requirements.Add(Requirement(
            "dotnet-sdk",
            ".NET SDK",
            dotnetVersion,
            ".NET SDK 8.0 or later. Builds the generated solution; the generator also uses " +
            "'dotnet sln add' to place the Scale Unit projects in it.",
            "winget install --id Microsoft.DotNet.SDK.8",
            "https://dotnet.microsoft.com/download/dotnet"));

        var nodeVersion = await GetToolVersionAsync(
            "node",
            "--version",
            "node.exe",
            cancellationToken);
        requirements.Add(Requirement(
            "node",
            "Node.js",
            nodeVersion,
            "Node.js 18 or later. Compiles the POS TypeScript (Microsoft.TypeScript.MSBuild) " +
            "when the generated solution builds. Generation works without it, the build does not.",
            "winget install --id OpenJS.NodeJS.LTS",
            "https://nodejs.org/en/download"));

        if (initializeGit)
        {
            var gitVersion = await GetToolVersionAsync(
                "git",
                "--version",
                "git.exe",
                cancellationToken);
            requirements.Add(Requirement(
                "git",
                "Git",
                gitVersion,
                "Git is used to initialise the repository in the generated project because " +
                "'Initialize a Git repository' is checked.",
                "winget install --id Git.Git",
                "https://git-scm.com/download/win"));
        }

        if (sdk is not null &&
            Version.TryParse(sdk.Version, out var sdkVersion) &&
            sdkVersion >= new Version(9, 59))
        {
            var pwshVersion = await GetToolVersionAsync(
                "pwsh",
                "--version",
                "pwsh.exe",
                cancellationToken);

            requirements.Add(Requirement(
                "pwsh",
                "PowerShell",
                pwshVersion,
                $"PowerShell 7.4 or later. Commerce SDK {sdk.Version} runs the POS proxy " +
                "generator through pwsh while the POS project builds.",
                "winget install --id Microsoft.PowerShell",
                "https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows"));

            if (dotnetVersion is not null)
            {
                var (known, net10Version) = await GetDotNet10Async(cancellationToken);

                if (known)
                {
                    requirements.Add(Requirement(
                        "dotnet-10",
                        ".NET 10 runtime",
                        net10Version,
                        $"The .NET 10 runtime (Microsoft.AspNetCore.App 10) is required by the POS " +
                        $"proxy generator that ships with Commerce SDK {sdk.Version}. Without it the " +
                        "build falls back to the .NET 8 generator and fails with 'No types could be " +
                        "loaded ... IEdmModelFactory'.",
                        "winget install --id Microsoft.DotNet.AspNetCore.10",
                        "https://dotnet.microsoft.com/download/dotnet/10.0"));
                }
            }
        }

        if (sdk is not null &&
            !string.IsNullOrWhiteSpace(outputDirectory) &&
            !string.IsNullOrWhiteSpace(projectName) &&
            ProjectRules.ValidateOutputDirectory(outputDirectory) is null &&
            ProjectRules.IsLongPathSupportEnabled() == false)
        {
            var projectDirectory = ProjectRules.GetProjectDirectory(outputDirectory, projectName);

            var longestPath = ProjectRules.GetLongestKnownPath(
                projectDirectory,
                projectName).Length;

            if (longestPath > LongPathWarningThreshold)
            {
                requirements.Add(new BuildRequirement(
                    "long-path",
                    "Long paths",
                    RequirementState.Missing,
                    $"{longestPath} chars",
                    $"Building this project creates a path of about {longestPath} characters, which " +
                    "exceeds the 260-character Windows limit. Use a shorter output folder such as " +
                    "C:\\D365 or enable long path support.",
                    "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\" /v LongPathsEnabled /t REG_DWORD /d 1 /f",
                    "https://learn.microsoft.com/windows/win32/fileio/maximum-file-path-limitation",
                    "Learn more"));
            }
        }

        return requirements;
    }

    private static BuildRequirement Requirement(
        string id,
        string name,
        string? version,
        string detail,
        string fix,
        string actionUrl,
        string actionLabel = "Download") =>
        new(
            id,
            name,
            version is null ? RequirementState.Missing : RequirementState.Present,
            version,
            detail,
            version is null ? fix : null,
            actionUrl,
            actionLabel);

    private async Task<bool> CheckDotNetSdkAsync(
        ICollection<PreflightCheck> checks,
        CancellationToken cancellationToken)
    {
        var version = await GetToolVersionAsync("dotnet", "--version", null, cancellationToken);

        if (version is not null)
        {
            return true;
        }

        checks.Add(new PreflightCheck(
            "dotnet-sdk",
            PreflightSeverity.Error,
            ".NET SDK not found",
            "The generator uses 'dotnet sln add' to place the Scale Unit projects in the " +
            "solution, so generation cannot continue without it.",
            "winget install --id Microsoft.DotNet.SDK.8"));

        return false;
    }

    private static void CheckLongPaths(
        ProjectConfiguration configuration,
        ICollection<PreflightCheck> checks)
    {
        if (ProjectRules.IsLongPathSupportEnabled() != false)
        {
            return;
        }

        var projectDirectory = ProjectRules.GetProjectDirectory(
            configuration.OutputDirectory,
            configuration.ProjectName);

        var longestPath = ProjectRules.GetLongestKnownPath(
            projectDirectory,
            configuration.ProjectName).Length;

        if (longestPath <= LongPathWarningThreshold)
        {
            return;
        }

        checks.Add(new PreflightCheck(
            "long-path",
            PreflightSeverity.Warning,
            "Windows long path support is disabled",
            $"Building this project creates a path of about {longestPath} characters, which " +
            "exceeds the 260-character Windows limit. The build fails while copying package " +
            "content. Generate into a shorter folder (C:\\D365) or enable long paths.",
            "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\" /v LongPathsEnabled /t REG_DWORD /d 1 /f"));
    }

    private async Task CheckNodeJsAsync(
        ICollection<PreflightCheck> checks,
        CancellationToken cancellationToken)
    {
        var version = await GetToolVersionAsync(
            "node",
            "--version",
            "node.exe",
            cancellationToken);

        if (version is not null)
        {
            return;
        }

        checks.Add(new PreflightCheck(
            "node",
            PreflightSeverity.Warning,
            "Node.js not found",
            "The POS project compiles TypeScript with Microsoft.TypeScript.MSBuild, which " +
            "needs node.exe. Generation works, but the generated solution will not build " +
            "until Node.js is installed.",
            "winget install --id OpenJS.NodeJS.LTS"));
    }

    /// <summary>
    /// Reads the version of an installed tool, or <see langword="null"/> when it is missing.
    /// Pass <paramref name="executableOnPath"/> when the tool should be probed through PATH.
    /// </summary>
    private async Task<string?> GetToolVersionAsync(
        string fileName,
        string arguments,
        string? executableOnPath,
        CancellationToken cancellationToken)
    {
        if (executableOnPath is not null &&
            !ProjectRules.IsExecutableOnPath(executableOnPath))
        {
            return null;
        }

        var key = $"{fileName}|{arguments}";

        if (versionCache.TryGetValue(key, out var cached) &&
            DateTime.UtcNow - cached.At < ProbeCacheLifetime)
        {
            return cached.Value;
        }

        var result = await ProcessRunner.RunAsync(
            fileName,
            arguments,
            Path.GetTempPath(),
            cancellationToken);

        var version = result.Succeeded
            ? ParseToolVersion(result.StandardOutput)
            : null;

        versionCache[key] = (DateTime.UtcNow, version);

        return version;
    }

    private static string? ParseToolVersion(string output)
    {
        var line = output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(candidate => candidate.Trim())
            .FirstOrDefault(candidate => candidate.Length > 0);

        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        // "9.0.319", "v22.14.0", "PowerShell 7.4.6", "git version 2.47.0.windows.1".
        var match = VersionPattern.Match(line);

        return match.Success ? match.Value : line;
    }

    private async Task<bool?> HasDotNet10RuntimeAsync(CancellationToken cancellationToken)
    {
        var (known, version) = await GetDotNet10Async(cancellationToken);

        return known ? version is not null : null;
    }

    /// <summary>
    /// Inspects the installed runtimes. <c>Known</c> is false when 'dotnet --list-runtimes'
    /// could not be read; <c>Version</c> is null when the .NET 10 runtime is missing.
    /// </summary>
    private async Task<(bool Known, string? Version)> GetDotNet10Async(
        CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - runtimeCache.At < ProbeCacheLifetime)
        {
            return (runtimeCache.Known, runtimeCache.Version);
        }

        var result = await ProcessRunner.RunAsync(
            "dotnet",
            "--list-runtimes",
            Path.GetTempPath(),
            cancellationToken);

        var known = result.Succeeded;
        string? version = null;

        if (known)
        {
            var runtimes = result.StandardOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseRuntime)
                .Where(runtime => runtime is not null)
                .Select(runtime => runtime!.Value)
                .ToList();

            var hasAspNetCore10 = runtimes.Any(runtime =>
                runtime.Name.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase) &&
                runtime.Version >= DotNet10);

            var netCore10 = runtimes
                .Where(runtime =>
                    runtime.Name.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase) &&
                    runtime.Version >= DotNet10)
                .Select(runtime => runtime.Version)
                .Max();

            if (hasAspNetCore10 && netCore10 is not null)
            {
                version = netCore10.ToString();
            }
        }

        runtimeCache = (DateTime.UtcNow, known, version);

        return (known, version);
    }

    private static (string Name, Version Version)? ParseRuntime(string line)
    {
        var parts = line.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2 ||
            !Version.TryParse(parts[1], out var version))
        {
            return null;
        }

        return (parts[0], version);
    }
}

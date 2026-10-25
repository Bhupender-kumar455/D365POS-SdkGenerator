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

/// <summary>
/// Catches the failures that are otherwise only discovered after a ten-minute build:
/// long paths, a missing TypeScript compiler, or the .NET 10 proxy-generator requirement
/// of Commerce SDK 9.59 and later.
/// </summary>
public sealed class PreflightService
{
    private const int LongPathWarningThreshold = 240;

    // The POS proxy generator of 9.59+ is a .NET 10 application; the build falls back to
    // the .NET 8 copy when no .NET 10 runtime is installed, and that copy cannot load the
    // .NET 10 RetailServerLibrary that ships in the package.
    private static readonly Version DotNet10 = new(10, 0);

    public async Task<IReadOnlyList<PreflightCheck>> RunAsync(
        ProjectConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var checks = new List<PreflightCheck>();

        var dotnetInstalled = await CheckDotNetSdkAsync(checks, cancellationToken);

        CheckLongPaths(configuration, checks);

        CheckNodeJs(checks);

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

            if (dotnetInstalled)
            {
                await CheckDotNet10Async(configuration, checks, cancellationToken);
            }
        }

        return checks;
    }

    private static async Task<bool> CheckDotNetSdkAsync(
        ICollection<PreflightCheck> checks,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            "dotnet",
            "--version",
            Path.GetTempPath(),
            cancellationToken);

        if (result.Succeeded)
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

    private static void CheckNodeJs(ICollection<PreflightCheck> checks)
    {
        if (ProjectRules.IsExecutableOnPath("node.exe"))
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

    private static async Task CheckDotNet10Async(
        ProjectConfiguration configuration,
        ICollection<PreflightCheck> checks,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            "dotnet",
            "--list-runtimes",
            Path.GetTempPath(),
            cancellationToken);

        if (!result.Succeeded)
        {
            return;
        }

        var runtimes = result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseRuntime)
            .Where(runtime => runtime is not null)
            .Select(runtime => runtime!.Value)
            .ToList();

        var hasAspNetCore10 = runtimes.Any(runtime =>
            runtime.Name.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase) &&
            runtime.Version >= DotNet10);

        var hasNetCore10 = runtimes.Any(runtime =>
            runtime.Name.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase) &&
            runtime.Version >= DotNet10);

        if (hasAspNetCore10 && hasNetCore10)
        {
            return;
        }

        checks.Add(new PreflightCheck(
            "dotnet-10",
            PreflightSeverity.Warning,
            ".NET 10 runtime not found",
            $"Commerce SDK {configuration.Sdk.Version} ships a .NET 10 POS proxy generator. " +
            "Without the .NET 10 runtime the build falls back to the .NET 8 generator, which " +
            "fails with 'No types could be loaded ... IEdmModelFactory'.",
            "winget install --id Microsoft.DotNet.AspNetCore.10"));
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

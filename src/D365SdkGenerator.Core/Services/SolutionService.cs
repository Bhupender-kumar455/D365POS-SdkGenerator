using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace D365SdkGenerator.Services;

public sealed class SolutionService
{
    private const string SdkCSharpProjectTypeGuid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";
    private const string LegacyCSharpProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

    private static readonly Regex ProjectEntryPattern = new(
        "^(?<indent>\\s*)Project\\(\"(?<type>\\{[0-9A-Fa-f-]+\\})\"\\)\\s*=\\s*\"(?<name>[^\"]*)\",\\s*\"(?<path>[^\"]*)\",\\s*\"(?<guid>\\{[0-9A-Fa-f-]+\\})\"\\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex NestedProjectPattern = new(
        "^(?<indent>\\s*)(?<project>\\{[0-9A-Fa-f-]+\\})\\s*=\\s*(?<folder>\\{[0-9A-Fa-f-]+\\})\\s*$",
        RegexOptions.IgnoreCase);

    public async Task AddProjectsAsync(
        string solutionFile,
        IEnumerable<string> projectFiles,
        CancellationToken cancellationToken = default)
    {
        var projects = projectFiles.ToList();
        if (projects.Count == 0)
        {
            return;
        }

        var solutionDirectory = Path.GetDirectoryName(solutionFile)!;

        // Without --in-root, "dotnet sln add" places the project in a solution folder named
        // after its parent directory. The generated sample keeps every project at the
        // solution root, so the option is requested explicitly when the SDK supports it.
        var useInRoot = await SupportsInRootOptionAsync(
            solutionDirectory,
            cancellationToken);

        var arguments = new StringBuilder()
            .Append("sln ")
            .Append(Quote(solutionFile))
            .Append(" add");

        if (useInRoot)
        {
            arguments.Append(" --in-root");
        }

        foreach (var project in projects)
        {
            arguments.Append(' ')
                .Append(Quote(project));
        }

        var addOutput = await RunAsync(
            "dotnet",
            arguments.ToString(),
            solutionDirectory,
            cancellationToken);

        // "dotnet sln add" reports invalid projects on the console but still exits with
        // code 0, so the solution is verified instead of trusting the exit code.
        var listed = await RunAsync(
            "dotnet",
            $"sln {Quote(solutionFile)} list",
            solutionDirectory,
            cancellationToken);

        var missing = projects
            .Where(project => !listed.Contains(
                Path.GetFileName(project),
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "The following project(s) were not added to the solution:" + Environment.NewLine +
                string.Join(Environment.NewLine, missing.Select(project => "  " + project)) +
                Environment.NewLine + Environment.NewLine +
                "dotnet sln output:" + Environment.NewLine + addOutput.TrimEnd());
        }

        await FlattenSolutionAsync(
            solutionFile,
            projects,
            cancellationToken);
    }

    private static async Task<bool> SupportsInRootOptionAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var help = await RunAsync(
                "dotnet",
                "sln add --help",
                workingDirectory,
                cancellationToken);

            return help.Contains("--in-root", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Older SDKs (or a solution folder that already exists) can leave the added projects
    /// nested below a solution folder. The generated solution keeps every project at the
    /// root, so those folder assignments are removed here. The project type GUID is also
    /// normalized to the SDK-style C# GUID used by the template projects.
    /// </summary>
    private static async Task FlattenSolutionAsync(
        string solutionFile,
        IReadOnlyList<string> addedProjects,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(solutionFile, cancellationToken);
        var hasPreamble = bytes.Length >= 3 &&
                          bytes[0] == 0xEF &&
                          bytes[1] == 0xBB &&
                          bytes[2] == 0xBF;

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(
            bytes,
            hasPreamble ? 3 : 0,
            bytes.Length - (hasPreamble ? 3 : 0));

        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hadTrailingNewLine = text.EndsWith("\n", StringComparison.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionFile))!;
        var targetPaths = addedProjects
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var projectGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var match = ProjectEntryPattern.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var projectPath = Path.GetFullPath(Path.Combine(
                solutionDirectory,
                NormalizeSeparators(match.Groups["path"].Value)));

            if (!targetPaths.Contains(projectPath))
            {
                continue;
            }

            projectGuids.Add(match.Groups["guid"].Value);

            if (match.Groups["type"].Value.Equals(
                    LegacyCSharpProjectTypeGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = lines[i].Replace(
                    match.Groups["type"].Value,
                    SdkCSharpProjectTypeGuid,
                    StringComparison.OrdinalIgnoreCase);

                changed = true;
            }
        }

        if (projectGuids.Count == 0)
        {
            return;
        }

        var removedIndexes = new HashSet<int>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Only GlobalSection(NestedProjects) maps projects to solution folders. Project
        // sections can also contain "{GUID} = {GUID}" lines (project dependencies), which
        // must be left untouched.
        var nestedSection = FindGlobalSection(lines, "NestedProjects");

        if (nestedSection is not null)
        {
            for (var i = nestedSection.Value.Start + 1; i < nestedSection.Value.End; i++)
            {
                var match = NestedProjectPattern.Match(lines[i]);
                if (!match.Success ||
                    !projectGuids.Contains(match.Groups["project"].Value))
                {
                    continue;
                }

                folders.Add(match.Groups["folder"].Value);
                removedIndexes.Add(i);
                changed = true;
            }
        }

        if (folders.Count > 0)
        {
            var foldersStillUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (nestedSection is not null)
            {
                for (var i = nestedSection.Value.Start + 1; i < nestedSection.Value.End; i++)
                {
                    if (removedIndexes.Contains(i))
                    {
                        continue;
                    }

                    var match = NestedProjectPattern.Match(lines[i]);
                    if (match.Success &&
                        folders.Contains(match.Groups["folder"].Value))
                    {
                        foldersStillUsed.Add(match.Groups["folder"].Value);
                    }
                }
            }

            // A folder that held only the projects added above is now empty; remove its
            // declaration so the solution does not show an unused folder in the IDE.
            for (var i = 0; i < lines.Count; i++)
            {
                var match = ProjectEntryPattern.Match(lines[i]);
                if (!match.Success ||
                    !folders.Contains(match.Groups["guid"].Value) ||
                    foldersStillUsed.Contains(match.Groups["guid"].Value))
                {
                    continue;
                }

                var end = i + 1;
                while (end < lines.Count &&
                       !lines[end].Trim().Equals("EndProject", StringComparison.OrdinalIgnoreCase))
                {
                    end++;
                }

                for (var index = i; index <= Math.Min(end, lines.Count - 1); index++)
                {
                    removedIndexes.Add(index);
                }

                i = end;
            }
        }

        if (!changed)
        {
            return;
        }

        var result = string.Join(
            newLine,
            lines.Where((_, index) => !removedIndexes.Contains(index)));

        if (hadTrailingNewLine && !result.EndsWith(newLine, StringComparison.Ordinal))
        {
            result += newLine;
        }

        await File.WriteAllTextAsync(
            solutionFile,
            result,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasPreamble),
            cancellationToken);
    }

    private static string NormalizeSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

    private static (int Start, int End)? FindGlobalSection(
        IReadOnlyList<string> lines,
        string sectionName)
    {
        var header = $"GlobalSection({sectionName})";

        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var end = i + 1;
            while (end < lines.Count &&
                   !lines[end].Trim().Equals("EndGlobalSection", StringComparison.OrdinalIgnoreCase))
            {
                end++;
            }

            return (i, end);
        }

        return null;
    }

    public async Task<bool> IsDotnetInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Run from a neutral directory: a global.json near the current directory
            // could make an otherwise healthy SDK installation look unavailable.
            await RunAsync(
                "dotnet",
                "--version",
                Path.GetTempPath(),
                cancellationToken);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The templates ship a global.json that pins an exact SDK. When that SDK is not
    /// installed, every later "dotnet" command inside the generated project fails.
    /// The file is aligned to the newest installed SDK before any of those commands run.
    /// </summary>
    /// <returns>The SDK version global.json was aligned to, or null when it was already usable.</returns>
    public async Task<string?> EnsureSdkCompatibleAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var globalJson = Path.Combine(projectDirectory, "global.json");
        if (!File.Exists(globalJson))
        {
            return null;
        }

        if (await TryGetSdkVersionAsync(projectDirectory, cancellationToken) is not null)
        {
            return null;
        }

        var installed = await GetInstalledSdkVersionsAsync(cancellationToken);

        if (installed.Count == 0)
        {
            throw new InvalidOperationException(
                "No .NET SDK was found in PATH. Install a .NET SDK before generating a project.");
        }

        var target = installed[0];
        var original = await File.ReadAllTextAsync(globalJson, cancellationToken);

        var updated = Regex.Replace(
            original,
            "(\"version\"\\s*:\\s*\")([^\"]*)(\")",
            "${1}" + target + "${3}");

        if (updated.Equals(original, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The template's global.json does not declare an SDK version that can be aligned. " +
                $"Installed SDKs: {string.Join(", ", installed)}.");
        }

        await File.WriteAllTextAsync(globalJson, updated, cancellationToken);

        if (await TryGetSdkVersionAsync(projectDirectory, cancellationToken) is null)
        {
            throw new InvalidOperationException(
                $"global.json was aligned to .NET SDK {target}, but the SDK still cannot be resolved. " +
                $"Installed SDKs: {string.Join(", ", installed)}.");
        }

        return target;
    }

    private static async Task<string?> TryGetSdkVersionAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunAsync("dotnet", "--version", directory, cancellationToken);

            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<string>> GetInstalledSdkVersionsAsync(
        CancellationToken cancellationToken)
    {
        var output = await RunAsync(
            "dotnet",
            "--list-sdks",
            Path.GetTempPath(),
            cancellationToken);

        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ')[0])
            .Select(version => Version.TryParse(version, out var parsed) ? parsed : null)
            .Where(version => version is not null)
            .Select(version => version!)
            .OrderByDescending(version => version)
            .Select(version => version.ToString())
            .ToList();
    }

    private static async Task<string> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed: {fileName} {arguments}{Environment.NewLine}" +
                stderrTask.Result + stdoutTask.Result);
        }

        return stdoutTask.Result;
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

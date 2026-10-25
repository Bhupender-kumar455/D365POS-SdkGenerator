using System.IO.Compression;
using D365SdkGenerator.Models;

namespace D365SdkGenerator.Services;

public sealed class TemplateSourceService
{
    // One shared client: creating a HttpClient per download exhausts sockets under
    // repeated runs and the generator never needs per-instance state.
    private static readonly HttpClient Client = CreateClient();

    private readonly IGeneratorLog log;

    public TemplateSourceService(IGeneratorLog? log = null) =>
        this.log = log ?? NullGeneratorLog.Instance;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("D365SdkGenerator/1.0");

        return client;
    }

    public async Task<string> DownloadAndExtractAsync(
        TemplateDefinition definition,
        string workingDirectory,
        string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.Repository))
        {
            throw new InvalidOperationException($"{label} repository is not configured.");
        }

        if (string.IsNullOrWhiteSpace(definition.Branch))
        {
            throw new InvalidOperationException($"{label} branch is not configured.");
        }

        var archiveUrl = await ResolveArchiveUrlAsync(definition, cancellationToken);

        var zipPath = Path.Combine(
            workingDirectory,
            $"{SanitizeFileName(label)}-{Guid.NewGuid():N}.zip");

        var extractDirectory = Path.Combine(
            workingDirectory,
            $"{SanitizeFileName(label)}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(extractDirectory);

        log.Info($"{label} download: {archiveUrl}");

        try
        {
            using var response = await Client.GetAsync(
                archiveUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(zipPath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            log.Info($"Extracting {label} template...");
            ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true);

            var sourceRoot = FindSourceRoot(extractDirectory, definition.SourceRoot);

            if (sourceRoot is null)
            {
                throw new DirectoryNotFoundException(
                    $"Could not find configured source root '{definition.SourceRoot}' in the {label} archive.");
            }

            return sourceRoot;
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    private async Task<string> ResolveArchiveUrlAsync(
        TemplateDefinition definition,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(definition.ArchiveUrl))
        {
            return definition.ArchiveUrl!;
        }

        var variants = GetBranchVariants(definition.Branch);

        foreach (var branch in variants)
        {
            var url = BuildGitHubArchiveUrl(definition.Repository, branch);

            using var request = new HttpRequestMessage(HttpMethod.Head, url);

            try
            {
                using var response = await Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return url;
                }
            }
            catch (HttpRequestException)
            {
                // Try the next branch variant.
            }
        }

        // Return the configured branch URL so the final GET produces a useful HTTP error.
        return BuildGitHubArchiveUrl(definition.Repository, definition.Branch);
    }

    private static IEnumerable<string> GetBranchVariants(string branch)
    {
        yield return branch;

        if (branch.StartsWith("release/", StringComparison.Ordinal))
        {
            yield return "Release/" + branch[8..];
        }
        else if (branch.StartsWith("Release/", StringComparison.Ordinal))
        {
            yield return "release/" + branch[8..];
        }
    }

    private static string BuildGitHubArchiveUrl(
        string repository,
        string branch)
    {
        var uri = new Uri(repository);
        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || segments.Length < 2)
        {
            throw new InvalidOperationException(
                $"Only GitHub repositories are supported by the ZIP downloader: {repository}");
        }

        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        return $"https://github.com/{segments[0]}/{repo}/archive/refs/heads/{Uri.EscapeDataString(branch).Replace("%2F", "/")}.zip";
    }

    private static string? FindSourceRoot(string extractedRoot, string relativePath)
    {
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        // GitHub archives wrap the repository in a single top-level folder, so the
        // shallowest match is the intended source root. Ordering makes the pick
        // deterministic instead of depending on directory enumeration order.
        return new[] { extractedRoot }
            .Concat(Directory.EnumerateDirectories(
                extractedRoot,
                "*",
                SearchOption.AllDirectories))
            .Select(directory => Path.Combine(directory, normalized))
            .Where(Directory.Exists)
            .OrderBy(candidate => candidate.Count(character =>
                character == Path.DirectorySeparatorChar))
            .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }
}

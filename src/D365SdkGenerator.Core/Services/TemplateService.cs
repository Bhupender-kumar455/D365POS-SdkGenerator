namespace D365SdkGenerator.Services;

public sealed class TemplateService
{
    public async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Template directory not found: {sourceDirectory}");
        }

        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(
                Path.Combine(destinationDirectory, relative));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(destinationDirectory, relative);

            Directory.CreateDirectory(
                Path.GetDirectoryName(targetFile)!);

            // Byte-for-byte copy. We intentionally do not rewrite template files here.
            // Project customization happens afterward and only touches known text files.
            await using var sourceStream = File.OpenRead(sourceFile);
            await using var targetStream = File.Create(targetFile);
            await sourceStream.CopyToAsync(targetStream, cancellationToken);
        }
    }
}

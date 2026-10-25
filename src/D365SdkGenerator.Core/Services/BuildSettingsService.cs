using System.Text;

namespace D365SdkGenerator.Services;

/// <summary>
/// Adjusts the build behaviour of the generated repository.
///
/// The Commerce SDK package imports a target (Microsoft.Dynamics.Commerce.Sdk.Installers
/// .StoreCommerce.targets) that runs the Store Commerce extensions installer after every
/// Debug build started from Visual Studio, because
/// $(InstallStoreCommerceExtensionsAfterBuild) defaults to true in that case. Building a
/// generated solution must not deploy anything to the machine, so the default is turned
/// off in repo.props. Deployment stays a separate, explicit step.
/// </summary>
public sealed class BuildSettingsService
{
    public const string InstallAfterBuildProperty = "InstallStoreCommerceExtensionsAfterBuild";

    private readonly IGeneratorLog log;

    public BuildSettingsService(IGeneratorLog? log = null) =>
        this.log = log ?? NullGeneratorLog.Instance;

    public async Task ApplyAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var repoProps = Path.Combine(projectDirectory, "repo.props");

        if (!File.Exists(repoProps))
        {
            throw new FileNotFoundException(
                $"Repository props file was not found: {repoProps}");
        }

        var bytes = await File.ReadAllBytesAsync(repoProps, cancellationToken);

        if (bytes.Length == 0)
        {
            throw new InvalidOperationException(
                $"Repository props file is empty: {repoProps}");
        }

        var encoding = DetectEncoding(bytes, out var preambleLength);
        var content = encoding.GetString(
            bytes,
            preambleLength,
            bytes.Length - preambleLength);

        if (content.Contains(
                $"<{InstallAfterBuildProperty}>",
                StringComparison.OrdinalIgnoreCase))
        {
            log.Info("Build installer  : already configured by the template");
            return;
        }

        var newLine = content.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";

        var block = string.Join(
            newLine,
            "  <!--",
            "    The Commerce SDK runs the Store Commerce extensions installer after every Debug",
            "    build started from Visual Studio ($(InstallStoreCommerceExtensionsAfterBuild)",
            "    defaults to true there). Building must not deploy, so it is disabled here.",
            "    Deploy explicitly when ready, or set this property to true to restore the",
            "    SDK default for local debugging.",
            "  -->",
            "  <PropertyGroup>",
            $"    <{InstallAfterBuildProperty}>false</{InstallAfterBuildProperty}>",
            "  </PropertyGroup>");

        var closingTag = content.LastIndexOf(
            "</Project>",
            StringComparison.OrdinalIgnoreCase);

        if (closingTag < 0)
        {
            throw new InvalidOperationException(
                $"The repository props file '{repoProps}' does not contain a closing </Project> element.");
        }

        var prefix = content[..closingTag];
        var separator = prefix.EndsWith(newLine, StringComparison.Ordinal)
            ? string.Empty
            : newLine;

        var updated = prefix + separator + block + newLine + content[closingTag..];

        var preamble = encoding.GetPreamble();
        var contentBytes = encoding.GetBytes(updated);
        var finalBytes = new byte[preamble.Length + contentBytes.Length];

        Buffer.BlockCopy(preamble, 0, finalBytes, 0, preamble.Length);
        Buffer.BlockCopy(contentBytes, 0, finalBytes, preamble.Length, contentBytes.Length);

        await File.WriteAllBytesAsync(repoProps, finalBytes, cancellationToken);

        log.Info("Build installer  : disabled (deploy manually)");
    }

    private static Encoding DetectEncoding(
        byte[] bytes,
        out int preambleLength)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            preambleLength = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE)
        {
            preambleLength = 2;
            return Encoding.Unicode;
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFE &&
            bytes[1] == 0xFF)
        {
            preambleLength = 2;
            return Encoding.BigEndianUnicode;
        }

        preambleLength = 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}

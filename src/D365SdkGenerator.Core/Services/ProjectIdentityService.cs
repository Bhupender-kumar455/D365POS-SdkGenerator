using System.Text;
using System.Text.RegularExpressions;
using D365SdkGenerator.Models;

namespace D365SdkGenerator.Services;

public sealed class ProjectIdentityService
{
    private const string StoreCommerceSolutionName = "Contoso.StoreCommercePackagingSamples";
    private const string StoreCommerceProjectPrefix = "Contoso.StoreCommercePackagingSample";

    // The Store Commerce and Scale Unit samples share one props file in the generated
    // layout, so the Scale Unit package identity is set explicitly in its own projects.
    private const string DefaultPublisher = "CN=Contoso Ltd.";
    private const string DefaultPublisherDisplayName = "Contoso Ltd.";

    private readonly IGeneratorLog log;

    public ProjectIdentityService(IGeneratorLog? log = null) =>
        this.log = log ?? NullGeneratorLog.Instance;

    public async Task ApplyAsync(
        string projectRoot,
        string storeCommerceRoot,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        log.Info("Applying project identity...");

        RenameStoreCommercePaths(
            storeCommerceRoot,
            configuration.ProjectName);

        RenameScaleUnitFiles(
            storeCommerceRoot,
            configuration.ProjectName,
            configuration.IncludeScaleUnit);

        await ReplaceKnownTextAsync(
            projectRoot,
            BuildReplacements(configuration),
            cancellationToken);

        // $(SolutionDir) is empty when MSBuild evaluates a project outside a solution
        // (dotnet sln add, dotnet build <project>, IDE load). The templates use it for
        // their CustomizationPackage.props import, so it is rewritten to a relative path.
        await NormalizeSolutionDirImportsAsync(
            storeCommerceRoot,
            cancellationToken);

        await ConfigurePosProjectAsync(
            storeCommerceRoot,
            configuration,
            cancellationToken);

        if (configuration.IncludeScaleUnit)
        {
            await ConfigureScaleUnitProjectsAsync(
                storeCommerceRoot,
                configuration,
                cancellationToken);
        }
    }

    private static readonly Regex SolutionDirImport = new(
        @"(<Import\s+Project\s*=\s*"")\$\(SolutionDir\)[\\/]?([^""]+)("")",
        RegexOptions.IgnoreCase);

    // A few template files carry stray whitespace inside the Import path (for example
    // "..\CustomizationPackage.props "). MSBuild tolerates it; we normalize it anyway.
    private static readonly Regex PaddedImportPath = new(
        @"(<Import\s+Project\s*=\s*"")\s*([^""]*?)\s*("")",
        RegexOptions.IgnoreCase);

    private static async Task NormalizeSolutionDirImportsAsync(
        string storeCommerceRoot,
        CancellationToken cancellationToken)
    {
        var projectFiles = Directory
            .EnumerateFiles(storeCommerceRoot, "*", SearchOption.AllDirectories)
            .Where(file =>
                file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .Where(file => !TextFileClassifier.IsGitPath(file))
            .ToList();

        foreach (var file in projectFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            if (bytes.Length == 0)
            {
                continue;
            }

            var encoding = DetectEncoding(bytes, out var preambleLength);
            var content = encoding.GetString(
                bytes,
                preambleLength,
                bytes.Length - preambleLength);

            var directory = Path.GetDirectoryName(file)!;

            var updated = SolutionDirImport.Replace(content, match =>
            {
                var target = match.Groups[2].Value
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);

                var absolute = Path.GetFullPath(Path.Combine(storeCommerceRoot, target));
                var relative = Path.GetRelativePath(directory, absolute);

                return match.Groups[1].Value + relative + match.Groups[3].Value;
            });

            updated = PaddedImportPath.Replace(
                updated,
                "${1}${2}${3}");

            if (updated.Equals(content, StringComparison.Ordinal))
            {
                continue;
            }

            var preamble = encoding.GetPreamble();
            var contentBytes = encoding.GetBytes(updated);
            var finalBytes = new byte[preamble.Length + contentBytes.Length];

            Buffer.BlockCopy(preamble, 0, finalBytes, 0, preamble.Length);
            Buffer.BlockCopy(contentBytes, 0, finalBytes, preamble.Length, contentBytes.Length);

            await File.WriteAllBytesAsync(file, finalBytes, cancellationToken);
        }
    }

    private static Dictionary<string, string> BuildReplacements(
        ProjectConfiguration configuration)
    {
        var projectName = configuration.ProjectName;
        var publisher = configuration.Publisher;
        var namespaceRoot = configuration.NamespaceRoot;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Replace the full solution/project identity before any shorter token.
            [StoreCommerceSolutionName] = projectName,
            [StoreCommerceProjectPrefix] = projectName,

            // Sample branding inside packaging metadata and POS manifests.
            ["Contoso Store Commerce Customization Packaging Sample"] =
                $"{projectName} Store Commerce Customization",
            ["Contoso Scale Unit Example Commerce Customization"] =
                $"{projectName} Scale Unit Customization",

            // Microsoft's repo metadata uses certificate-style publisher values.
            [DefaultPublisher] = $"CN={publisher}",
            [DefaultPublisherDisplayName] = publisher,

            // Sample namespaces follow the project's first segment (Contoso by default).
            ["Contoso.CommerceRuntime"] = $"{namespaceRoot}.CommerceRuntime",
            ["namespace Contoso"] = $"namespace {namespaceRoot}",

            // POS extension manifest publisher.
            ["\"publisher\": \"Contoso\""] = $"\"publisher\": \"{publisher}\"",

            // Extension property key used by the CommerceRuntime sample.
            ["ext.Contoso"] = $"ext.{namespaceRoot}",

            // Runtime extension package identity, which must match the packaged
            // customization package (see the comment in the template trigger).
            ["extensionPackageDefinition.Publisher = \"Contoso\""] =
                $"extensionPackageDefinition.Publisher = \"{publisher}\"",

            // Hardware Station sample writes to %ProgramData%\Contoso\FilePrinter.
            ["\"Contoso\", \"FilePrinter\""] = $"\"{namespaceRoot}\", \"FilePrinter\""
        };
    }

    private static void RenameStoreCommercePaths(
        string root,
        string projectName)
    {
        // Deepest first: renaming a parent directory would invalidate child paths.
        var directories = Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(directory => !TextFileClassifier.IsGitPath(directory))
            .OrderByDescending(directory => directory.Length)
            .ToList();

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var replacement = ReplaceStoreCommerceName(name, projectName);

            if (replacement.Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            MovePath(
                directory,
                Path.Combine(Path.GetDirectoryName(directory)!, replacement));
        }

        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !TextFileClassifier.IsGitPath(file))
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var replacement = ReplaceStoreCommerceName(fileName, projectName);

            if (replacement.Equals(fileName, StringComparison.Ordinal))
            {
                continue;
            }

            MovePath(
                file,
                Path.Combine(Path.GetDirectoryName(file)!, replacement));
        }
    }

    private void RenameScaleUnitFiles(
        string root,
        string projectName,
        bool includeScaleUnit)
    {
        if (!includeScaleUnit)
        {
            return;
        }

        var scaleUnitDirectory = Path.Combine(root, "ScaleUnit");
        var installerDirectory = Path.Combine(root, "ScaleUnit.Installer");

        var scaleUnitProject = FindTemplateProjectFile(
            scaleUnitDirectory,
            "Scale Unit");

        var installerProject = FindTemplateProjectFile(
            installerDirectory,
            "Scale Unit Installer");

        log.Info($"Scale Unit project : {Path.GetFileName(scaleUnitProject)}");
        log.Info($"Installer project  : {Path.GetFileName(installerProject)}");

        MovePath(
            scaleUnitProject,
            Path.Combine(scaleUnitDirectory, $"{projectName}.ScaleUnit.csproj"));

        MovePath(
            installerProject,
            Path.Combine(installerDirectory, $"{projectName}.ScaleUnit.Installer.csproj"));
    }

    private static string FindTemplateProjectFile(
        string directory,
        string description)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"{description} template directory was not found: {directory}");
        }

        var projectFiles = Directory
            .EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .Where(file => !TextFileClassifier.IsGitPath(file))
            .ToList();

        if (projectFiles.Count == 0)
        {
            throw new FileNotFoundException(
                $"No .csproj file was found inside the {description} template directory:{Environment.NewLine}" +
                directory);
        }

        if (projectFiles.Count == 1)
        {
            return projectFiles[0];
        }

        // Prefer the expected Microsoft names when more than one project is present.
        var preferredNames = description.Equals("Scale Unit", StringComparison.OrdinalIgnoreCase)
            ? new[] { "ScaleUnit.csproj" }
            : new[] { "ScaleUnit.Installer.csproj", "ScaleUnit.csproj" };

        foreach (var preferredName in preferredNames)
        {
            var match = projectFiles.FirstOrDefault(file =>
                Path.GetFileName(file).Equals(preferredName, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        throw new InvalidOperationException(
            $"Multiple .csproj files were found inside the {description} template directory " +
            "and none could be identified automatically:" + Environment.NewLine +
            string.Join(Environment.NewLine, projectFiles));
    }

    private static void MovePath(
        string source,
        string target)
    {
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException(
                $"Expected template path was not found: {source}");
        }

        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            // Case-only rename: Windows treats both names as the same entry.
            return;
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new InvalidOperationException(
                $"Cannot rename '{source}' to '{target}' because the target already exists. " +
                "The template layout is not what the generator expects.");
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }
    }

    private async Task ReplaceKnownTextAsync(
        string root,
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        var updatedCount = 0;

        foreach (var file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TextFileClassifier.IsGitPath(file) || !TextFileClassifier.IsText(file))
            {
                continue;
            }

            var original = await File.ReadAllBytesAsync(file, cancellationToken);

            if (original.Length == 0)
            {
                continue;
            }

            var encoding = DetectEncoding(original, out var preambleLength);

            string content;
            try
            {
                content = encoding.GetString(
                    original,
                    preambleLength,
                    original.Length - preambleLength);
            }
            catch
            {
                continue;
            }

            var updated = content;

            foreach (var replacement in replacements.OrderByDescending(x => x.Key.Length))
            {
                updated = updated.Replace(
                    replacement.Key,
                    replacement.Value,
                    StringComparison.Ordinal);
            }

            if (updated.Equals(content, StringComparison.Ordinal))
            {
                continue;
            }

            var preamble = encoding.GetPreamble();
            var contentBytes = encoding.GetBytes(updated);
            var finalBytes = new byte[preamble.Length + contentBytes.Length];

            Buffer.BlockCopy(preamble, 0, finalBytes, 0, preamble.Length);
            Buffer.BlockCopy(contentBytes, 0, finalBytes, preamble.Length, contentBytes.Length);

            await File.WriteAllBytesAsync(file, finalBytes, cancellationToken);

            updatedCount++;
        }

        log.Info($"Text files changed : {updatedCount}");
    }

    private const string PosPackagingTargetName = "PosTypeScriptForCommercePackaging";
    private const string PosQuotePathsTargetName = "PosQuoteTypeScriptPaths";

    /// <summary>
    /// The Commerce SDK POS targets pass the project directory to tsc unquoted
    /// ("--sourceRoot $(MSBuildProjectDirectory) --mapRoot $(MSBuildProjectDirectory)"), so an
    /// output path containing spaces makes tsc treat the remainder as source files and fail
    /// with TS5042. The same flags are re-declared with quoted paths.
    /// The installer projects also query the POS project through GetCommercePackagingOutput,
    /// which runs outside the normal build pipeline. Microsoft.TypeScript.MSBuild names its
    /// incremental output log with string.GetHashCode() of the tsconfig path, and that value is
    /// randomized per process on modern .NET, so the query cannot find the log written by the
    /// earlier compilation. The TypeScript targets then add a placeholder item named
    /// should_not_exist.file, which the installer tries to copy and fails. That target compiles
    /// the TypeScript output inside the packaging query and packages only files that exist.
    /// </summary>
    private static async Task ConfigurePosProjectAsync(
        string storeCommerceRoot,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var projectFile = Path.Combine(
            storeCommerceRoot,
            "Pos",
            $"{configuration.ProjectName}.Pos.csproj");

        if (!File.Exists(projectFile))
        {
            // POS is optional in some template variants.
            return;
        }

        var content = await File.ReadAllTextAsync(projectFile, cancellationToken);
        var hasPreamble = await HasUtf8BomAsync(projectFile, cancellationToken);

        if (!content.Contains("Microsoft.TypeScript.MSBuild", StringComparison.OrdinalIgnoreCase))
        {
            // No TypeScript integration: neither fix applies.
            return;
        }

        var newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var blocks = new List<string>();

        if (!content.Contains(PosQuotePathsTargetName, StringComparison.Ordinal))
        {
            blocks.Add(string.Join(
                newLine,
                "  <!--",
                "    Microsoft.Dynamics.Commerce.Sdk.Pos.targets passes the project directory to tsc",
                "    as the value of the sourceRoot and mapRoot options without surrounding quotes.",
                "    When the generated project lives in a folder whose name contains a space, tsc",
                "    reads the rest of the path as source files and fails with TS5042: Option",
                "    'project' cannot be mixed with source files on a command line.",
                "    Re-declare the same flags with quoted paths.",
                "  -->",
                "  <Target Name=\"" + PosQuotePathsTargetName + "\"",
                "          BeforeTargets=\"PreComputeCompileTypeScriptWithTSConfig;CompileTypeScriptWithTSConfig\">",
                "    <PropertyGroup>",
                "      <TypeScriptConfigFileAdditionalFlags>--sourceMap true --sourceRoot &quot;$(MSBuildProjectDirectory)&quot; --mapRoot &quot;$(MSBuildProjectDirectory)&quot;</TypeScriptConfigFileAdditionalFlags>",
                "    </PropertyGroup>",
                "  </Target>"));
        }

        if (!content.Contains(PosPackagingTargetName, StringComparison.Ordinal))
        {
            blocks.Add(string.Join(
                newLine,
                "  <!--",
                "    The installer projects query this project through GetCommercePackagingOutput,",
                "    which runs outside the normal build pipeline. Microsoft.TypeScript.MSBuild names",
                "    its incremental output log with string.GetHashCode(), and that value is randomized",
                "    per process on modern .NET, so the query cannot find the log written by an earlier",
                "    compilation. The TypeScript targets then add a placeholder item named",
                "    should_not_exist.file, which the installer tries to copy and fails.",
                "    Compile the TypeScript output inside the packaging query and package only files",
                "    that actually exist.",
                "  -->",
                "  <Target Name=\"PosEnableTypeScriptForPackaging\"",
                "          BeforeTargets=\"CompileTypeScriptWithTSConfig\"",
                "          Condition=\"'$(BuildingProject)' == 'false' AND '$(DesignTimeBuild)' != 'true'\">",
                "    <PropertyGroup>",
                "      <BuildingProject>true</BuildingProject>",
                "    </PropertyGroup>",
                "  </Target>",
                "  <Target Name=\"" + PosPackagingTargetName + "\"",
                "          BeforeTargets=\"GetPosExtensionPackageContent\"",
                "          DependsOnTargets=\"FindConfigFiles;PreComputeCompileTypeScriptWithTSConfig;CompileTypeScriptWithTSConfig\">",
                "    <ItemGroup>",
                "      <!-- Drop the TypeScript build sentinel and any other item that does not exist. -->",
                "      <ContentWithTargetPath Remove=\"@(ContentWithTargetPath)\" Condition=\"!Exists('%(ContentWithTargetPath.FullPath)')\" />",
                "      <!-- Replace compiler output that may already have been added by the TypeScript targets. -->",
                "      <ContentWithTargetPath Remove=\"@(GeneratedJavascript)\" />",
                "      <_PosTypeScriptOutput Include=\"@(GeneratedJavascript)\" Condition=\"Exists('%(GeneratedJavascript.FullPath)')\" />",
                "    </ItemGroup>",
                "    <AssignTargetPath Files=\"@(_PosTypeScriptOutput)\" RootFolder=\"$(MSBuildProjectDirectory)\">",
                "      <Output TaskParameter=\"AssignedFiles\" ItemName=\"_PosTypeScriptOutputWithTargetPath\" />",
                "    </AssignTargetPath>",
                "    <ItemGroup>",
                "      <ContentWithTargetPath Include=\"@(_PosTypeScriptOutputWithTargetPath)\" />",
                "    </ItemGroup>",
                "  </Target>"));
        }

        if (blocks.Count == 0)
        {
            return;
        }

        var closingTag = content.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);

        if (closingTag < 0)
        {
            throw new InvalidOperationException(
                $"The POS project '{projectFile}' does not contain a closing </Project> element.");
        }

        var prefix = content[..closingTag];
        var separator = prefix.EndsWith(newLine, StringComparison.Ordinal)
            ? newLine
            : newLine + newLine;

        var updated = prefix + separator + string.Join(newLine, blocks) + newLine + content[closingTag..];

        await File.WriteAllTextAsync(
            projectFile,
            updated,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasPreamble),
            cancellationToken);
    }

    private static async Task<bool> HasUtf8BomAsync(
        string file,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        await using var stream = File.OpenRead(file);
        var read = await stream.ReadAtLeastAsync(
            buffer,
            buffer.Length,
            throwOnEndOfStream: false,
            cancellationToken);

        return read == 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF;
    }

    private static async Task ConfigureScaleUnitProjectsAsync(
        string storeCommerceRoot,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var scaleUnitProject = Path.Combine(
            storeCommerceRoot,
            "ScaleUnit",
            $"{configuration.ProjectName}.ScaleUnit.csproj");

        var installerProject = Path.Combine(
            storeCommerceRoot,
            "ScaleUnit.Installer",
            $"{configuration.ProjectName}.ScaleUnit.Installer.csproj");

        await UpdateScaleUnitProjectAsync(
            scaleUnitProject,
            configuration,
            cancellationToken);

        await UpdateScaleUnitProjectAsync(
            installerProject,
            configuration,
            cancellationToken);
    }

    private static async Task UpdateScaleUnitProjectAsync(
        string projectFile,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(projectFile, cancellationToken);
        var newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // The Scale Unit sample imports its own props file. The generated project lives
        // beside the Store Commerce sample, so it shares the Store Commerce props file.
        // The import must stay relative: $(SolutionDir) is empty while the SDK evaluates
        // the project (dotnet sln add, IDE load), which silently prevents the project
        // from being added to the solution.
        content = Regex.Replace(
            content,
            @"(<Import\s+Project\s*=\s*"")\$\(SolutionDir\)[\\/]?CustomizationPackage\.props("")",
            "${1}..\\CustomizationPackage.props${2}",
            RegexOptions.IgnoreCase);

        if (Regex.IsMatch(content, @"<Import[^>]*\$\(SolutionDir\)", RegexOptions.IgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported $(SolutionDir) import in '{projectFile}'. " +
                "Generated projects must use relative imports.");
        }

        // Reuse the Store Commerce ChannelDatabase, CommerceRuntime and POS projects.
        content = ReplacePath(
            content,
            @"..\ChannelDatabase\ChannelDatabase.csproj",
            $@"..\ChannelDatabase\{configuration.ProjectName}.ChannelDatabase.csproj");

        content = ReplacePath(
            content,
            @"..\CommerceRuntime\CommerceRuntime.csproj",
            $@"..\CommerceRuntime\{configuration.ProjectName}.CommerceRuntime.csproj");

        content = ReplacePath(
            content,
            @"..\POS\POS.csproj",
            $@"..\Pos\{configuration.ProjectName}.Pos.csproj");

        content = AddScaleUnitPackageIdentity(
            content,
            configuration,
            newLine);

        content = AddMissingPackageVersions(content);

        await File.WriteAllTextAsync(
            projectFile,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    /// <summary>
    /// The Scale Unit templates rely on the Scale Unit repository's Directory.Packages.props
    /// (central package management) for their versionless PackageReference entries. The
    /// generated layout does not copy that file because it would also apply to the Store
    /// Commerce projects, which pin their own versions. The versions are therefore added
    /// explicitly, using the same property the central file uses.
    /// </summary>
    private static string AddMissingPackageVersions(string content)
    {
        return Regex.Replace(
            content,
            @"<PackageReference\b[^>]*?/>",
            match =>
            {
                if (match.Value.Contains("Version=", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                var close = match.Value.LastIndexOf("/>", StringComparison.Ordinal);

                return match.Value[..close].TrimEnd() +
                       " Version=\"$(CommerceSdkPackagesVersion)\" />";
            },
            RegexOptions.IgnoreCase);
    }

    private static string ReplacePath(
        string content,
        string relativePath,
        string replacement)
    {
        // Templates use either Windows or forward slash separators.
        content = content.Replace(relativePath, replacement, StringComparison.OrdinalIgnoreCase);

        return content.Replace(
            relativePath.Replace('\\', '/'),
            replacement.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string AddScaleUnitPackageIdentity(
        string content,
        ProjectConfiguration configuration,
        string newLine)
    {
        // The shared CustomizationPackage.props names the Store Commerce package after the
        // project. The Scale Unit is deployed as its own package, so it must not reuse that
        // identity. These properties win because they are evaluated after the import.
        var block = string.Join(
            newLine,
            "<PropertyGroup>",
            $"    <PackageName>{configuration.ScaleUnitPackageName}</PackageName>",
            $"    <PackageDisplayName>{configuration.ProjectName} Scale Unit Customization</PackageDisplayName>",
            $"    <PackageDescription>{configuration.ProjectName} Scale Unit Customization</PackageDescription>",
            "</PropertyGroup>");

        var import = Regex.Match(
            content,
            @"<Import\s+Project\s*=\s*""[^""]*""\s*/>",
            RegexOptions.IgnoreCase);

        if (!import.Success)
        {
            throw new InvalidOperationException(
                "The Scale Unit template project does not import CustomizationPackage.props.");
        }

        var insertionPoint = import.Index + import.Length;
        var indentedBlock = block.Replace(newLine, newLine + "    ");

        return content.Insert(
            insertionPoint,
            newLine + newLine + "    " + indentedBlock);
    }

    private static string ReplaceStoreCommerceName(
        string value,
        string projectName) =>
        value
            .Replace(StoreCommerceSolutionName, projectName, StringComparison.Ordinal)
            .Replace(StoreCommerceProjectPrefix, projectName, StringComparison.Ordinal);

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

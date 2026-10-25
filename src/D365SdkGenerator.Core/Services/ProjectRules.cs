namespace D365SdkGenerator.Services;

/// <summary>
/// Rules that are shared by the desktop UI and the generation pipeline, so a value can
/// never be accepted by the form and rejected (or silently mangled) later.
/// </summary>
public static class ProjectRules
{
    // Neutral defaults that match the Microsoft samples. Users replace them with their
    // own company identity; nothing here is tied to a specific customer.
    public const string DefaultProjectName = "Contoso.StoreCommercePackaging";
    public const string DefaultPublisher = "Contoso Ltd.";
    public const string PreferredShortOutputRoot = @"C:\D365";

    // These are the identities baked into the downloaded templates. Using one of them as
    // the project name would make the rename a no-op and fail validation.
    private static readonly string[] ReservedProjectNames =
    [
        "Contoso.StoreCommercePackagingSample",
        "Contoso.StoreCommercePackagingSamples"
    ];

    /// <summary>
    /// The longest path the generated layout is known to create. The Scale Unit package
    /// copies POS content into a deep folder structure; the installer projects add their
    /// own deep obj paths on top of it.
    /// </summary>
    private static readonly string[] LongestKnownPathSuffix =
    [
        "src",
        "PackagingSamples",
        "StoreCommerceApp",
        "ScaleUnit",
        "bin",
        "Debug",
        "netstandard2.0",
        "CloudScaleUnitExtensionPackage",
        "RetailCloudPOS",
        "Code",
        "Extensions",
        "{project}",
        "Controls",
        "Dialogs",
        "Create",
        "ExampleCreateDialogModule.js.map"
    ];

    public static string? ValidateProjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Project name is required.";
        }

        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            return "Project name must start with a letter or underscore.";
        }

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return "Project name cannot contain spaces.";
            }

            if (character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                return $"Project name cannot contain '{character}'.";
            }
        }

        if (ReservedProjectNames.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return $"'{value}' is the template identity and cannot be reused. Choose a different name.";
        }

        return null;
    }

    public static string? ValidatePublisher(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Publisher is required.";
        }

        return value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is ' ' or '.' or '-' or '_')
            ? null
            : "Publisher may only contain letters, digits, spaces, dots, hyphens and underscores.";
    }

    public static string? ValidateOutputDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Output folder is required.";
        }

        try
        {
            _ = Path.GetFullPath(value);
        }
        catch
        {
            return "The output folder path is not valid.";
        }

        return File.Exists(value)
            ? "The output path points to a file, not a folder."
            : null;
    }

    public static string GetProjectDirectory(
        string outputDirectory,
        string projectName) =>
        Path.Combine(Path.GetFullPath(outputDirectory), projectName);

    public static string GetSolutionFile(
        string projectDirectory,
        string projectName) =>
        Path.Combine(
            projectDirectory,
            "src",
            "PackagingSamples",
            "StoreCommerceApp",
            projectName + ".sln");

    public static string GetLongestKnownPath(
        string projectDirectory,
        string projectName) =>
        LongestKnownPathSuffix.Aggregate(
            projectDirectory,
            (current, segment) => Path.Combine(
                current,
                segment.Equals("{project}", StringComparison.Ordinal)
                    ? projectName
                    : segment));

    /// <summary>
    /// True when the current folder already holds a project the generator would refuse to
    /// overwrite. Checked up front so the form can disable Generate instead of failing
    /// after the templates have been downloaded.
    /// </summary>
    public static bool TargetDirectoryIsOccupied(
        string outputDirectory,
        string projectName)
    {
        try
        {
            var projectDirectory = GetProjectDirectory(outputDirectory, projectName);

            return Directory.Exists(projectDirectory) &&
                   Directory.EnumerateFileSystemEntries(projectDirectory).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Null when the setting cannot be read (for example on a non-Windows OS).</summary>
    public static bool? IsLongPathSupportEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\FileSystem");

            return key?.GetValue("LongPathsEnabled") is int value && value != 0;
        }
        catch
        {
            return null;
        }
    }

    public static string GetDefaultOutputDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (!string.IsNullOrWhiteSpace(documents))
        {
            return Path.Combine(documents, "D365Projects");
        }

        var root = Path.GetPathRoot(Environment.SystemDirectory);

        return Path.Combine(
            string.IsNullOrWhiteSpace(root) ? @"C:\" : root,
            "D365Projects");
    }

    public static bool IsExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);

                if (File.Exists(candidate))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return false;
    }
}

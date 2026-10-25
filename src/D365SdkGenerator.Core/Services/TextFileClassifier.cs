namespace D365SdkGenerator.Services;

/// <summary>
/// Single source of truth for "is this file text we are allowed to rewrite?".
/// Both the identity replacement and the validator use it, so a file can never be
/// rewritten by one and checked by the other under a different rule.
/// </summary>
public static class TextFileClassifier
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx", ".csproj", ".props", ".targets", ".cs", ".ts", ".tsx", ".js",
        ".json", ".xml", ".config", ".txt", ".md", ".yml", ".yaml", ".html", ".cshtml",
        ".css", ".scss", ".manifest", ".nuspec", ".resx", ".resjson", ".sql"
    };

    private static readonly HashSet<string> KnownTextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitignore", ".gitattributes", ".editorconfig", "Dockerfile"
    };

    public static bool IsText(string path)
    {
        var fileName = Path.GetFileName(path);

        return KnownTextFileNames.Contains(fileName) ||
               TextExtensions.Contains(Path.GetExtension(path));
    }

    public static bool IsGitPath(string path) =>
        SplitPath(path).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase));

    public static bool IsBuildOutputPath(string path) =>
        SplitPath(path).Any(part =>
            part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".vs", StringComparison.OrdinalIgnoreCase));

    private static string[] SplitPath(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

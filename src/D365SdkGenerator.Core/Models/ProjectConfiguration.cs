namespace D365SdkGenerator.Models;

public sealed record ProjectConfiguration
{
    private static readonly HashSet<string> ReservedNamespaceSegments = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
        "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
        "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while"
    };

    public required SdkVersion Sdk { get; init; }
    public required string ProjectName { get; init; }
    public required string Publisher { get; init; }
    public required string OutputDirectory { get; init; }
    public required bool IncludePos { get; init; }
    public required bool IncludeScaleUnit { get; init; }
    public required bool InitializeGit { get; init; }

    /// <summary>
    /// Root used for the sample C# namespaces. The template ships with
    /// <c>Contoso.CommerceRuntime</c>, so the first segment of the project name is a
    /// natural replacement. The publisher is only used as a fallback because it is
    /// often a display name such as "Contoso Ltd." that is not a valid identifier.
    /// </summary>
    public string NamespaceRoot
    {
        get
        {
            var candidate = ProjectName.Split('.')[0];

            if (IsValidIdentifier(candidate) && !ReservedNamespaceSegments.Contains(candidate))
            {
                return candidate;
            }

            var sanitized = new string(Publisher.Where(char.IsLetterOrDigit).ToArray());

            return IsValidIdentifier(sanitized) ? sanitized : "Contoso";
        }
    }

    public string ScaleUnitPackageName => $"{ProjectName}.ScaleUnit";

    private static bool IsValidIdentifier(string value) =>
        !string.IsNullOrEmpty(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsLetterOrDigit(character) || character == '_');
}

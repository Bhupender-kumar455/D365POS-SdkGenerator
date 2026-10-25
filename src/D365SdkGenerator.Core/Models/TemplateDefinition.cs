namespace D365SdkGenerator.Models;

public sealed class TemplateDefinition
{
    public string Repository { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string? ArchiveUrl { get; set; }
    public string SourceRoot { get; set; } = string.Empty;
}

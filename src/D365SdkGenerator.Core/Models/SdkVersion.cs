namespace D365SdkGenerator.Models;

public sealed class SdkVersion
{
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public TemplateDefinition StoreCommerce { get; set; } = new();
    public TemplateDefinition ScaleUnit { get; set; } = new();
}

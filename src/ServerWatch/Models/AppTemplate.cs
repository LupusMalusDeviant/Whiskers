namespace ServerWatch.Models;

public class AppTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Icon { get; set; } = ""; // MudBlazor icon name
    public string ComposeContent { get; set; } = "";
    public Dictionary<string, string> EnvDefaults { get; set; } = new();
    public List<string> RequiredEnvVars { get; set; } = new();
}

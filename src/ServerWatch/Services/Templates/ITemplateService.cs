using ServerWatch.Models;

namespace ServerWatch.Services.Templates;

/// <summary>Provides the built-in app deployment templates.</summary>
public interface ITemplateService
{
    List<AppTemplate> GetTemplates();
    AppTemplate? GetTemplate(string id);
    List<string> GetCategories();
}

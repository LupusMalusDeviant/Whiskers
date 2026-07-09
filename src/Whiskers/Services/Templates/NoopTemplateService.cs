using Whiskers.Models;

namespace Whiskers.Services.Templates;

/// <summary>The Core's default <see cref="ITemplateService"/> for when the Deployment module is off. The Core,
/// mixed <c>ContainerTools</c> resolves it for <c>deploy_app</c> (to look up a template by id), so this no-op
/// keeps that working — it advertises no templates, so a deploy-by-template attempt fails cleanly with
/// "template not found". The real <see cref="TemplateService"/> wins by last-registration when the module is
/// enabled. Soft-dependency-via-no-op-Core-contract pattern (RoadToSAP §2.1).</summary>
public sealed class NoopTemplateService : ITemplateService
{
    public List<AppTemplate> GetTemplates() => new();
    public AppTemplate? GetTemplate(string id) => null;
    public List<string> GetCategories() => new();
}

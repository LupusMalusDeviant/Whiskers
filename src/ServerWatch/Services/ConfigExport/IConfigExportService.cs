namespace ServerWatch.Services.ConfigExport;

/// <summary>Exports the non-secret app configuration (servers, roles, whitelist, MCP) as JSON.</summary>
public interface IConfigExportService
{
    string ExportJson();
}

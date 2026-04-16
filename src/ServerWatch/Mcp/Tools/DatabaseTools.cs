using ModelContextProtocol.Server;
using System.ComponentModel;
using ServerWatch.Models;
using ServerWatch.Services.Database;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace ServerWatch.Mcp.Tools;

[McpServerToolType]
public class DatabaseTools
{
    [McpServerTool, Description("Detect the database type of a container. Returns the database engine (PostgreSQL, MySQL, MongoDB, Redis, Neo4j) or 'None'.")]
    public static async Task<string> DetectDatabase(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "detect_database");
        if (denied != null) return denied;

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        if (container == null) return $"Container not found: {containerId}";

        if (!container.IsDatabase)
            return $"Container '{container.Name}' (Image: {container.Image}) is not a database container.";

        var env = await docker.GetContainerEnvAsync(container.Id, serverId);
        var creds = DatabaseDetector.ExtractCredentials(env, container.DatabaseType);

        return $"Database detected:\n  Type: {DatabaseDetector.GetLabel(container.DatabaseType)}\n  Image: {container.Image}\n  User: {creds.User}\n  Database: {creds.Database}\n  Port: {DatabaseDetector.GetDefaultPort(container.DatabaseType)}";
    }

    [McpServerTool, Description("List all databases in a database container.")]
    public static async Task<string> ListDatabases(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        IDatabaseService dbService,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_databases");
        if (denied != null) return denied;

        var (container, creds, error) = await ResolveContainer(docker, containerId, serverId);
        if (error != null) return error;

        var databases = await dbService.GetDatabasesAsync(container!.Id, container.DatabaseType, creds!, serverId);
        return databases.Any()
            ? $"Databases in {container.Name}:\n{string.Join('\n', databases.Select(d => $"  - {d}"))}"
            : $"No databases found in {container.Name}.";
    }

    [McpServerTool, Description("List tables in a database with row counts and sizes.")]
    public static async Task<string> ListTables(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        IDatabaseService dbService,
        [Description("Container ID or name")] string containerId,
        [Description("Database name")] string database,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_tables");
        if (denied != null) return denied;

        var (container, creds, error) = await ResolveContainer(docker, containerId, serverId);
        if (error != null) return error;

        var tables = await dbService.GetTablesAsync(container!.Id, database, container.DatabaseType, creds!, serverId);
        if (!tables.Any()) return $"No tables found in {database}.";

        var lines = tables.Select(t => $"  - {t.Name} ({t.RowCount} rows, {t.Size})");
        return $"Tables in {database} ({container.Name}):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Get the schema (columns, types, keys) of a table.")]
    public static async Task<string> GetSchema(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        IDatabaseService dbService,
        [Description("Container ID or name")] string containerId,
        [Description("Database name")] string database,
        [Description("Table name")] string table,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_schema");
        if (denied != null) return denied;

        var (container, creds, error) = await ResolveContainer(docker, containerId, serverId);
        if (error != null) return error;

        var columns = await dbService.GetSchemaAsync(container!.Id, database, table, container.DatabaseType, creds!, serverId);
        if (!columns.Any()) return $"No columns found for {table}.";

        var lines = columns.Select(c =>
            $"  {c.Name} {c.Type}{(c.Nullable ? " NULL" : " NOT NULL")}{(c.PrimaryKey ? " [PK]" : "")}{(c.Default != null ? $" DEFAULT {c.Default}" : "")}");
        return $"Schema for {table} in {database}:\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Execute a SQL query or database command and return the results.")]
    public static async Task<string> ExecuteQuery(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        IDatabaseService dbService,
        [Description("Container ID or name")] string containerId,
        [Description("SQL query or database command to execute")] string query,
        [Description("Database name (optional, uses default)")] string? database = null,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "execute_query");
        if (denied != null) return denied;

        var (container, creds, error) = await ResolveContainer(docker, containerId, serverId);
        if (error != null) return error;

        if (!string.IsNullOrEmpty(database))
            creds!.Database = database;

        var result = await dbService.ExecuteQueryAsync(container!.Id, query, container.DatabaseType, creds!, serverId);

        if (!result.Success)
            return $"Query failed: {result.Error}";

        if (!result.Columns.Any())
            return $"Query executed successfully ({result.DurationMs:F0}ms, {result.RowCount} rows).";

        // Format as table
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Query completed ({result.DurationMs:F0}ms, {result.RowCount} rows):");
        sb.AppendLine(string.Join(" | ", result.Columns));
        sb.AppendLine(new string('-', result.Columns.Sum(c => c.Length + 3)));
        foreach (var row in result.Rows.Take(50)) // Limit output
            sb.AppendLine(string.Join(" | ", row));
        if (result.RowCount > 50)
            sb.AppendLine($"... ({result.RowCount - 50} more rows)");
        return sb.ToString();
    }

    [McpServerTool, Description("Create a database backup (dump) for a database container.")]
    public static async Task<string> BackupDatabase(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        IDatabaseService dbService,
        [Description("Container ID or name")] string containerId,
        [Description("Database name to backup")] string database,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "backup_database");
        if (denied != null) return denied;

        var (container, creds, error) = await ResolveContainer(docker, containerId, serverId);
        if (error != null) return error;

        var (success, filePath, sizeBytes, backupError) = await dbService.BackupDatabaseAsync(
            container!.Id, database, container.DatabaseType, creds!, serverId);

        return success
            ? $"Backup created:\n  File: {filePath}\n  Size: {sizeBytes / 1024}KB"
            : $"Backup failed: {backupError}";
    }

    private static async Task<(ContainerInfo? Container, DatabaseCredentials? Creds, string? Error)> ResolveContainer(
        IDockerService docker, string containerId, string? serverId)
    {
        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        if (container == null) return (null, null, $"Container not found: {containerId}");
        if (!container.IsDatabase) return (null, null, $"Container '{container.Name}' is not a database.");

        var env = await docker.GetContainerEnvAsync(container.Id, serverId);
        var creds = DatabaseDetector.ExtractCredentials(env, container.DatabaseType);
        return (container, creds, null);
    }
}

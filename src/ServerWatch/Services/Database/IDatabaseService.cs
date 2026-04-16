using ServerWatch.Models;

namespace ServerWatch.Services.Database;

public interface IDatabaseService
{
    Task<QueryResult> ExecuteQueryAsync(string containerId, string query, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
    Task<List<string>> GetDatabasesAsync(string containerId, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
    Task<List<TableInfo>> GetTablesAsync(string containerId, string database, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
    Task<List<ColumnInfo>> GetSchemaAsync(string containerId, string database, string table, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
    Task<(bool Success, string FilePath, long SizeBytes, string Error)> BackupDatabaseAsync(string containerId, string database, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
}

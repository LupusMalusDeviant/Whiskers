using Whiskers.Services.Mutations;
using Whiskers.Models;

namespace Whiskers.Services.Database;

/// <summary>Database access through a container's own client. An omitted <c>serverId</c> means Whiskers' own host
/// (<see cref="OmittedServer.Local"/>); the implementation reads that from this declaration, as the scope guard does.</summary>
[OmittedServer(OmittedServer.Local)]
public interface IDatabaseService
{
    [MutatingOperation("db.query")]
    Task<QueryResult> ExecuteQueryAsync([MutationTarget] string containerId, string query, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
    Task<List<string>> GetDatabasesAsync(string containerId, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
    Task<List<TableInfo>> GetTablesAsync(string containerId, string database, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
    Task<List<ColumnInfo>> GetSchemaAsync(string containerId, string database, string table, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
    [MutatingOperation("db.backup")]
    Task<(bool Success, string FilePath, long SizeBytes, string Error)> BackupDatabaseAsync(string containerId, string database, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null);
}

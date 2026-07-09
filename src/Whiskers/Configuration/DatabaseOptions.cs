namespace Whiskers.Configuration;

/// <summary>
/// Database provider selection (stableDB.md). SQLite is the zero-config default; PostgreSQL is opt-in
/// for production / Kubernetes / multi-replica. A single <c>MetricsDbContext</c> is used either way —
/// the provider is chosen once at startup by <see cref="Provider"/>.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>"sqlite" (default) or "postgres". Also settable via <c>WHISKERS_DB_PROVIDER</c>.</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>
    /// Empty for SQLite → the default <c>metrics.db</c> path (from <c>DataPathOptions</c>) is used.
    /// For PostgreSQL, an Npgsql connection string (<c>Host=…;Database=…;Username=…;Password=…</c>).
    /// Also settable via <c>WHISKERS_DB_CONNECTION</c> or a secret file via <c>WHISKERS_DB_CONNECTION_FILE</c>.
    /// </summary>
    public string ConnectionString { get; set; } = "";
}

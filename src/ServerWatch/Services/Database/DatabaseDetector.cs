using ServerWatch.Models;

namespace ServerWatch.Services.Database;

public static class DatabaseDetector
{
    /// <summary>Detect database type from Docker image name.</summary>
    public static DatabaseType DetectType(string imageName)
    {
        var lower = imageName.ToLowerInvariant();

        if (lower.Contains("postgres") || lower.Contains("pgvector") || lower.Contains("timescaledb") || lower.Contains("postgis"))
            return DatabaseType.PostgreSQL;
        if (lower.Contains("mysql") || lower.Contains("mariadb") || lower.Contains("percona"))
            return DatabaseType.MySQL;
        if (lower.Contains("mongo"))
            return DatabaseType.MongoDB;
        if (lower.Contains("redis") || lower.Contains("valkey") || lower.Contains("keydb") || lower.Contains("dragonfly"))
            return DatabaseType.Redis;
        if (lower.Contains("neo4j"))
            return DatabaseType.Neo4j;

        return DatabaseType.None;
    }

    /// <summary>Extract DB credentials from container environment variables.</summary>
    public static DatabaseCredentials ExtractCredentials(List<KeyValuePair<string, string>> env, DatabaseType dbType)
    {
        var creds = new DatabaseCredentials();
        var dict = env.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

        switch (dbType)
        {
            case DatabaseType.PostgreSQL:
                creds.User = GetFirst(dict, "POSTGRES_USER", "PGUSER") ?? "postgres";
                creds.Password = GetFirst(dict, "POSTGRES_PASSWORD", "PGPASSWORD") ?? "";
                creds.Database = GetFirst(dict, "POSTGRES_DB", "PGDATABASE") ?? creds.User;
                break;

            case DatabaseType.MySQL:
                creds.User = GetFirst(dict, "MYSQL_USER", "MARIADB_USER") ?? "root";
                creds.Password = GetFirst(dict, "MYSQL_ROOT_PASSWORD", "MYSQL_PASSWORD", "MARIADB_ROOT_PASSWORD", "MARIADB_PASSWORD") ?? "";
                creds.Database = GetFirst(dict, "MYSQL_DATABASE", "MARIADB_DATABASE") ?? "";
                break;

            case DatabaseType.MongoDB:
                creds.User = GetFirst(dict, "MONGO_INITDB_ROOT_USERNAME") ?? "";
                creds.Password = GetFirst(dict, "MONGO_INITDB_ROOT_PASSWORD") ?? "";
                creds.Database = GetFirst(dict, "MONGO_INITDB_DATABASE") ?? "admin";
                break;

            case DatabaseType.Redis:
                creds.Password = GetFirst(dict, "REDIS_PASSWORD", "REQUIREPASS") ?? "";
                break;

            case DatabaseType.Neo4j:
                creds.User = "neo4j";
                creds.Password = GetFirst(dict, "NEO4J_AUTH")?.Split('/')?.LastOrDefault() ?? "";
                break;
        }

        return creds;
    }

    /// <summary>Get the DB CLI command name for this database type.</summary>
    public static string GetCliCommand(DatabaseType dbType) => dbType switch
    {
        DatabaseType.PostgreSQL => "psql",
        DatabaseType.MySQL => "mysql",
        DatabaseType.MongoDB => "mongosh",
        DatabaseType.Redis => "redis-cli",
        DatabaseType.Neo4j => "cypher-shell",
        _ => "sh"
    };

    /// <summary>Get the default internal port for this database type.</summary>
    public static int GetDefaultPort(DatabaseType dbType) => dbType switch
    {
        DatabaseType.PostgreSQL => 5432,
        DatabaseType.MySQL => 3306,
        DatabaseType.MongoDB => 27017,
        DatabaseType.Redis => 6379,
        DatabaseType.Neo4j => 7687,
        _ => 0
    };

    /// <summary>Get a human-readable label for the database type.</summary>
    public static string GetLabel(DatabaseType dbType) => dbType switch
    {
        DatabaseType.PostgreSQL => "PostgreSQL",
        DatabaseType.MySQL => "MySQL / MariaDB",
        DatabaseType.MongoDB => "MongoDB",
        DatabaseType.Redis => "Redis",
        DatabaseType.Neo4j => "Neo4j",
        _ => "Unbekannt"
    };

    private static string? GetFirst(Dictionary<string, string> dict, params string[] keys)
    {
        foreach (var key in keys)
            if (dict.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
                return val;
        return null;
    }
}

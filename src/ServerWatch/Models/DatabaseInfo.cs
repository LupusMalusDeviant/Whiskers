namespace ServerWatch.Models;

public enum DatabaseType
{
    None,
    PostgreSQL,
    MySQL,
    MongoDB,
    Redis,
    Neo4j
}

public class QueryResult
{
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public int RowCount { get; set; }
    public string? Error { get; set; }
    public double DurationMs { get; set; }
    public bool Success => Error == null;
}

public class TableInfo
{
    public string Name { get; set; } = "";
    public long RowCount { get; set; }
    public string Size { get; set; } = "";
}

public class ColumnInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Nullable { get; set; }
    public string? Default { get; set; }
    public bool PrimaryKey { get; set; }
}

public class DatabaseCredentials
{
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "";
}

namespace Whiskers.Components.Pages;

/// <summary>What the database tab of a container page holds: the loaded databases and tables, the query and its
/// result, the query builder, the chosen migration or seed file and the running flags. <see cref="ContainerDetail"/>
/// creates it once per page and passes it to <see cref="ContainerDatabasePanel"/>, because MudTabs disposes the
/// panel of an inactive tab: the state must survive a tab switch, and a running import must keep its button
/// disabled when the user comes back.</summary>
public sealed class ContainerDatabaseState
{
    // Database state
    public bool DbLoaded;
    public bool DbExecuting;
    public bool DbBackingUp;
    public bool DbMigrating;
    public bool DbSeeding;
    public bool ShowMigration;
    public bool ShowSeed;
    public string DbQuery = "";
    public string? DbSelectedDatabase;
    public string? DbSelectedTable;
    public Whiskers.Models.DatabaseCredentials? DbCreds;
    public Whiskers.Models.QueryResult? DbResult;
    public List<string> DbDatabases = new();
    public List<Whiskers.Models.TableInfo> DbTables = new();
    public List<Whiskers.Models.ColumnInfo> DbColumns = new();

    // Query Builder state
    public string QbAction = "SELECT";
    public string? QbTable;
    public List<string> QbTableColumns = new();
    public string QbColumnsText = "";
    public string QbWhere = "";
    public string? QbOrderBy;
    public bool QbOrderDesc;
    public int QbLimit = 50;
    public List<(string Key, string Value)> QbSetFields = new();

    // Migration/Seed state
    public string? MigrationFileName;
    public byte[]? MigrationFileData;
    public string? MigrationOutput;
    public bool MigrationSuccess;
    public string? SeedFileName;
    public byte[]? SeedFileData;
    public string? SeedOutput;
    public bool SeedSuccess;
}

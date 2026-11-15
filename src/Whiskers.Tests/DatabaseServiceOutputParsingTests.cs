using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Database;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>How <see cref="DatabaseService"/> reads the clients' output (table lists, schema, query results, database
/// lists) and splits a Redis command into arguments. Review of 1.0.0, area Fleet (database tool).</summary>
public class DatabaseServiceOutputParsingTests
{
    private sealed class ScriptedHost(Func<string, CommandResult> answer) : IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add(command);
            return Task.FromResult(answer(command));
        }
    }

    private static DatabaseService Db(ScriptedHost host) => new(host, NullLogger<DatabaseService>.Instance);

    private static readonly DatabaseCredentials Creds = new() { User = "app", Password = "", Database = "app" };

    [Fact]
    public async Task Postgres_table_listing_reports_rows_and_size_in_their_own_fields()
    {
        // psql -t -A prints the selected columns '|'-separated in the order of the query: name, rows, size.
        var host = new ScriptedHost(_ => new CommandResult { ExitCode = 0, Output = "users|42|16 kB\n" });

        var tables = await Db(host).GetTablesAsync("c0ffee", "app", DatabaseType.PostgreSQL, Creds, "local");

        var users = Assert.Single(tables);
        Assert.Equal("users", users.Name);
        Assert.True(users.RowCount == 42 && users.Size == "16 kB",
            $"PostgreSQL table 'users' has 42 rows and 16 kB, reported: RowCount={users.RowCount}, Size='{users.Size}'");
        // The query itself selects the row count before the size, the order the parser reads.
        var command = Assert.Single(host.Commands);
        Assert.True(command.IndexOf("n_live_tup", StringComparison.Ordinal) < command.IndexOf("pg_size_pretty", StringComparison.Ordinal),
            $"the query must select the row count before the size: {command}");
    }

    [Fact]
    public async Task MySql_table_listing_reads_the_tab_separated_batch_output()
    {
        // `mysql -e` without a terminal (docker exec without -t) prints batch output: a header line and tab-separated rows.
        var host = new ScriptedHost(_ => new CommandResult
        {
            ExitCode = 0,
            Output = "TABLE_NAME\tTABLE_ROWS\tSize_MB\nusers\t42\t0.02\nTABLES_old\t3\t0.01\n",
        });

        var tables = await Db(host).GetTablesAsync("c0ffee", "app", DatabaseType.MySQL, Creds, "local");

        Assert.Equal(2, tables.Count);
        var users = tables[0];
        Assert.True(users.Name == "users" && users.RowCount == 42,
            $"MySQL table 'users' (42 rows) reported as Name='{users.Name.Replace("\t", "\\t")}', RowCount={users.RowCount}");
        Assert.Equal("0.02 MB", users.Size);
        Assert.Equal("TABLES_old", tables[1].Name);   // only the header line is skipped, not a name starting with TABLE
    }

    [Fact]
    public async Task MySql_schema_reads_the_tab_separated_describe_output()
    {
        var host = new ScriptedHost(_ => new CommandResult
        {
            ExitCode = 0,
            Output = "Field\tType\tNull\tKey\tDefault\tExtra\nid\tint\tNO\tPRI\tNULL\tauto_increment\nname\tvarchar(50)\tYES\t\tNULL\t\n",
        });

        var columns = await Db(host).GetSchemaAsync("c0ffee", "app", "users", DatabaseType.MySQL, Creds, "local");

        Assert.True(columns.Count == 2, $"DESCRIBE listed 2 columns, the schema shows {columns.Count}");
        Assert.True(columns[0].PrimaryKey, "column 'id' has Key=PRI and is not shown as primary key");
        Assert.False(columns[0].Nullable);
        Assert.Null(columns[0].Default);
        Assert.Equal("varchar(50)", columns[1].Type);
        Assert.True(columns[1].Nullable);
        Assert.False(columns[1].PrimaryKey);
    }

    [Fact]
    public async Task Postgres_schema_reads_the_pipe_separated_output()
    {
        var host = new ScriptedHost(_ => new CommandResult
        {
            ExitCode = 0,
            Output = "id|integer|NO|nextval('users_id_seq'::regclass)|YES\nname|text|YES||NO\n",
        });

        var columns = await Db(host).GetSchemaAsync("c0ffee", "app", "users", DatabaseType.PostgreSQL, Creds, "local");

        Assert.Equal(2, columns.Count);
        Assert.True(columns[0].PrimaryKey);
        Assert.Equal("nextval('users_id_seq'::regclass)", columns[0].Default);
        Assert.True(columns[1].Nullable);
        Assert.Null(columns[1].Default);
    }

    [Fact]
    public async Task A_query_result_keeps_rows_whose_first_value_starts_with_a_parenthesis_or_two_dashes()
    {
        const string psql =
            "   phone    | name\n" +
            "------------+------\n" +
            " (555) 1234 | bob\n" +
            " --none--   | al\n" +
            " 030 1234   | eve\n" +
            "(3 rows)\n";
        var host = new ScriptedHost(_ => new CommandResult { ExitCode = 0, Output = psql });

        var result = await Db(host).ExecuteQueryAsync("c0ffee", "select phone, name from people", DatabaseType.PostgreSQL, Creds, "local");

        Assert.True(result.RowCount == 3, $"psql returned 3 rows, the result holds {result.RowCount}: " +
                                         string.Join("; ", result.Rows.Select(r => string.Join(",", r))));
        Assert.Equal(["phone", "name"], result.Columns);
        Assert.Equal(["(555) 1234", "bob"], result.Rows[0]);
        Assert.Equal(["--none--", "al"], result.Rows[1]);
    }

    [Fact]
    public async Task A_mysql_query_result_splits_on_tabs_only()
    {
        var host = new ScriptedHost(_ => new CommandResult { ExitCode = 0, Output = "a\tb\nx|y\t(z)\n" });

        var result = await Db(host).ExecuteQueryAsync("c0ffee", "select a, b from t", DatabaseType.MySQL, Creds, "local");

        Assert.Equal(["a", "b"], result.Columns);
        Assert.Equal(["x|y", "(z)"], Assert.Single(result.Rows));
    }

    [Fact]
    public async Task A_postgres_database_whose_name_starts_with_databases_is_listed()
    {
        var host = new ScriptedHost(_ => new CommandResult { ExitCode = 0, Output = "app\ndatabases_archive\npostgres\n" });

        var databases = await Db(host).GetDatabasesAsync("c0ffee", DatabaseType.PostgreSQL, Creds, "local");

        Assert.Contains("databases_archive", databases);
    }

    [Fact]
    public async Task The_mysql_database_list_skips_only_the_header_line()
    {
        var host = new ScriptedHost(_ => new CommandResult { ExitCode = 0, Output = "Database\nDatabase_old\napp\n" });

        var databases = await Db(host).GetDatabasesAsync("c0ffee", DatabaseType.MySQL, Creds, "local");

        Assert.Equal(["Database_old", "app"], databases);
    }

    [Fact]
    public async Task A_redis_command_keeps_a_quoted_argument_with_a_space_as_one_argument()
    {
        var host = new ScriptedHost(_ => new CommandResult { ExitCode = 0, Output = "OK\n" });

        await Db(host).ExecuteQueryAsync("c0ffee", "SET greeting \"hello world\"", DatabaseType.Redis, new DatabaseCredentials(), "local");

        var command = Assert.Single(host.Commands);
        Assert.True(command.Contains("'hello world'", StringComparison.Ordinal),
            $"redis-cli must get the value as one argument, the host command is: {command}");
    }

    [Fact]
    public async Task A_redis_argument_with_shell_metacharacters_reaches_redis_cli_single_quoted()
    {
        var host = new ScriptedHost(_ => new CommandResult { ExitCode = 0, Output = "OK\n" });

        await Db(host).ExecuteQueryAsync("c0ffee", "SET k \"$(id); echo 'x'\" ", DatabaseType.Redis, new DatabaseCredentials(), "local");

        var command = Assert.Single(host.Commands);
        Assert.EndsWith("'redis-cli' 'SET' 'k' '$(id); echo '\\''x'\\'''", command);
    }

    [Theory]
    [InlineData("GET  key", new[] { "GET", "key" })]
    [InlineData("SET k 'it\\'s'", new[] { "SET", "k", "it's" })]
    [InlineData("SET k \"a\\\"b\\\\c\\n\"", new[] { "SET", "k", "a\"b\\c\n" })]
    [InlineData("SET k \"\"", new[] { "SET", "k", "" })]
    [InlineData("SET pro\"tected\" v", new[] { "SET", "pro\"tected\"", "v" })]
    public void Redis_arguments_are_split_like_the_redis_cli_prompt(string line, string[] expected)
    {
        Assert.True(DatabaseService.TryParseRedisArguments(line, out var args, out var error), error);
        Assert.Equal(expected, args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SET k \"unterminated")]
    [InlineData("SET k \"a\"b")]
    [InlineData("SET k \"\\x41\"")]
    [InlineData("SET k \"\\p\"")]
    [InlineData("SET k 'a\\n'")]
    public async Task An_unparseable_redis_command_is_refused_and_nothing_runs(string line)
    {
        var host = new ScriptedHost(_ => new CommandResult { ExitCode = 0, Output = "OK\n" });

        var result = await Db(host).ExecuteQueryAsync("c0ffee", line, DatabaseType.Redis, new DatabaseCredentials(), "local");

        Assert.False(result.Success);
        Assert.Empty(host.Commands);
    }
}

using Whiskers.Utils;

namespace Whiskers.Tests;

// Review of main: the container page's .env editor shows only the variables; writing the file back must keep every
// other line where it was.
public class DotEnvDocumentTests
{
    private const string File = "# database\nDB_HOST=db\n\n# secrets below\nDB_PASSWORD=s3cret\nnot a variable\nPORT=8080\n";

    private static IEnumerable<DotEnvEdit> Unchanged(IReadOnlyList<DotEnvLine> lines) =>
        lines.Select((l, i) => (l, i)).Where(x => x.l.Key is not null).Select(x => new DotEnvEdit(x.i, x.l.Key!, x.l.Value!));

    [Fact]
    public void Writing_back_unchanged_variables_gives_the_file_as_it_was()
    {
        var lines = DotEnvDocument.Parse(File);
        Assert.Equal(File, DotEnvDocument.Render(lines, Unchanged(lines)));
    }

    [Fact]
    public void Only_variables_are_parsed_as_variables()
    {
        var lines = DotEnvDocument.Parse(File);
        Assert.Equal(["DB_HOST", "DB_PASSWORD", "PORT"], lines.Where(l => l.Key is not null).Select(l => l.Key));
        Assert.Equal(7, lines.Count);   // the final line break is not a blank line
    }

    [Fact]
    public void A_changed_value_stays_on_its_line_and_comments_stay_on_theirs()
    {
        var lines = DotEnvDocument.Parse(File);
        var edits = Unchanged(lines).Select(e => e.Key == "DB_HOST" ? e with { Value = "db2" } : e);
        Assert.Equal(File.Replace("DB_HOST=db\n", "DB_HOST=db2\n"), DotEnvDocument.Render(lines, edits));
    }

    [Fact]
    public void A_removed_variable_drops_its_line_and_an_added_one_goes_at_the_end()
    {
        var lines = DotEnvDocument.Parse(File);
        var edits = Unchanged(lines).Where(e => e.Key != "PORT").Append(new DotEnvEdit(null, "NEW", "1"));
        Assert.Equal(File.Replace("PORT=8080\n", "") + "NEW=1\n", DotEnvDocument.Render(lines, edits));
    }

    [Fact]
    public void Crlf_lines_and_a_missing_final_line_break_are_read()
    {
        var lines = DotEnvDocument.Parse("# c\r\nA=1\r\nB=2");
        Assert.Equal(["# c", "A=1", "B=2"], lines.Select(l => l.Raw));
        Assert.Equal("2", lines[2].Value);
    }

    [Fact]
    public void A_failed_read_is_not_an_empty_file()
    {
        // A read that failed (host unreachable, permission, timeout) must not look like a missing or empty file: the
        // page blocks saving on null, or it would replace the real file with only the variables added in the editor.
        Assert.Null(DotEnvDocument.FromRead(false, "present\nA=1\n", out var missing));
        Assert.False(missing);
        Assert.Null(DotEnvDocument.FromRead(true, "", out _));
        Assert.Null(DotEnvDocument.FromRead(true, null, out _));
        Assert.Null(DotEnvDocument.FromRead(true, "cat: /x/.env: Permission denied\n", out _));
    }

    [Fact]
    public void A_missing_file_and_an_existing_one_are_told_apart()
    {
        Assert.Empty(DotEnvDocument.FromRead(true, "absent\n", out var missing)!);
        Assert.True(missing);

        var lines = DotEnvDocument.FromRead(true, "present\n" + File, out missing);
        Assert.False(missing);
        Assert.Equal(File, DotEnvDocument.Render(lines!, Unchanged(lines!)));

        Assert.Empty(DotEnvDocument.FromRead(true, "present\n", out missing)!);   // an empty file that exists
        Assert.False(missing);
    }

    [Fact]
    public void The_read_command_reports_presence_on_its_first_line() =>
        Assert.Equal("if [ -e '/opt/a b/.env' ]; then echo present && cat '/opt/a b/.env'; else echo absent; fi",
            DotEnvDocument.ReadCommand("'/opt/a b/.env'"));

    [Fact]
    public void An_empty_file_gives_only_the_added_variables()
    {
        var lines = DotEnvDocument.Parse("");
        Assert.Empty(lines);
        Assert.Equal("A=1\n", DotEnvDocument.Render(lines, [new DotEnvEdit(null, "A", "1"), new DotEnvEdit(null, " ", "x")]));
    }
}

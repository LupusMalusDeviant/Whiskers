using Whiskers.Utils;

namespace Whiskers.Tests;

// Secret-hygiene regression for Bean Whiskers-b9qw. DB command-builders now pass passwords via env
// vars (out of the process argv) and HostCommandExecutor redacts commands before logging — these tests
// prove the redaction those debug logs rely on actually hides DB passwords and keyed secrets.
public class SecretHygieneTests
{
    [Fact]
    public void Redact_hides_db_password_in_argv_and_env_forms()
    {
        var argv = SecretRedactor.Redact("mysql -uroot -psup3rs3cret -e 'select 1'");
        Assert.DoesNotContain("sup3rs3cret", argv);
        Assert.Contains("-p***", argv);

        var mysqlEnv = SecretRedactor.Redact("MYSQL_PWD=sup3rs3cret mysqldump db > /tmp/x.sql");
        Assert.DoesNotContain("sup3rs3cret", mysqlEnv);
        Assert.Contains("MYSQL_PWD=***", mysqlEnv);

        var pgEnv = SecretRedactor.Redact("PGPASSWORD=sup3rs3cret pg_dump -U u db");
        Assert.DoesNotContain("sup3rs3cret", pgEnv);
        Assert.Contains("PGPASSWORD=***", pgEnv);
    }

    [Fact]
    public void Redact_hides_keyed_secrets()
    {
        var s = SecretRedactor.Redact("deploy --token=abc123def --password=hunter2");
        Assert.DoesNotContain("abc123def", s);
        Assert.DoesNotContain("hunter2", s);
    }
}

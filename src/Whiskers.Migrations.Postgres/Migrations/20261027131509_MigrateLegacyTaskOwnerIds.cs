using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class MigrateLegacyTaskOwnerIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // R1-03 (INV-01): owners recorded before the principal id scheme were bare emails. Migrate exactly those
            // to user:<email>; every other value (empty = no owner, key:<id> = already in the scheme, anything
            // ambiguous) is left as it is, and the owner resolver keeps an unrecognised value suspended.
            migrationBuilder.Sql(@"UPDATE ""ScheduledTasks""
SET ""CreatedBy"" = 'user:' || lower(trim(""CreatedBy""))
WHERE ""CreatedBy"" LIKE '_%@_%'
  AND ""CreatedBy"" NOT LIKE '%@%@%'
  AND ""CreatedBy"" NOT LIKE '%:%'
  AND ""CreatedBy"" NOT LIKE '% %';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"UPDATE ""ScheduledTasks""
SET ""CreatedBy"" = substr(""CreatedBy"", 6)
WHERE ""CreatedBy"" LIKE 'user:_%';");
        }
    }
}

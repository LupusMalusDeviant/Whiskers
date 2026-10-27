using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations
{
    /// <inheritdoc />
    public partial class PrincipalIdsForTaskOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedVia",
                table: "ScheduledTasks",
                type: "TEXT",
                nullable: true);

            // R1-03 (INV-01): owners recorded before the principal id scheme were bare emails. Migrate exactly the
            // unambiguous ones to user:<email>: one '@', something on both sides, and nothing but ASCII letters, digits
            // and . _ % + - (so lower() means the same on every provider and in AutomationOwners.MigrateLegacy).
            // Every other value (empty = no owner, key:<id>, anything ambiguous) is left as it is and the owner
            // resolver keeps an unrecognised value suspended.
            migrationBuilder.Sql(@"UPDATE ""ScheduledTasks""
SET ""CreatedBy"" = 'user:' || lower(""CreatedBy"")
WHERE ""CreatedBy"" LIKE '_%@_%'
  AND ""CreatedBy"" NOT LIKE '%@%@%'
  AND ""CreatedBy"" NOT GLOB '*[^A-Za-z0-9._%+@-]*';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"UPDATE ""ScheduledTasks""
SET ""CreatedBy"" = substr(""CreatedBy"", 6)
WHERE ""CreatedBy"" LIKE 'user:_%';");

            migrationBuilder.DropColumn(
                name: "CreatedVia",
                table: "ScheduledTasks");
        }
    }
}

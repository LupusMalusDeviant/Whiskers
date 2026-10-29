using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations
{
    /// <inheritdoc />
    public partial class AuditTypedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Arguments",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DelegatedBy",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                table: "AuditLog",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OnBehalfOfId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OnBehalfOfKind",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperationId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentOperationId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrincipalId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrincipalKind",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrincipalSessionId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Surface",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriggerId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Verification",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_EventId",
                table: "AuditLog",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_OperationId",
                table: "AuditLog",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Phase_Timestamp",
                table: "AuditLog",
                columns: new[] { "Phase", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLog_EventId",
                table: "AuditLog");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_OperationId",
                table: "AuditLog");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_Phase_Timestamp",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "ApprovalId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Arguments",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "CredentialId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "DecisionId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "DelegatedBy",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "OnBehalfOfId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "OnBehalfOfKind",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "ParentOperationId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "PrincipalId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "PrincipalKind",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "PrincipalSessionId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Surface",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "TriggerId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Verification",
                table: "AuditLog");
        }
    }
}

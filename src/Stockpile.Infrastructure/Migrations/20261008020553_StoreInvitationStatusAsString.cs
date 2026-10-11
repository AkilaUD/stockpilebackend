using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockpile.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StoreInvitationStatusAsString : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column is referenced by the name-literal CHECK, and SQL Server
            // refuses ALTER COLUMN while a dependent object exists: drop the
            // constraint, switch int → nvarchar(40) member name (domain-model
            // §1; architecture §9.1 — every enum column stores its name), then
            // re-add the same constraint. OrganizationMembers is empty (no row
            // was ever written before task 1.43), so the type change is safe.
            migrationBuilder.DropCheckConstraint(
                name: "CK_OrganizationMembers_InvitationStatus",
                table: "OrganizationMembers");

            migrationBuilder.AlterColumn<string>(
                name: "InvitationStatus",
                table: "OrganizationMembers",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrganizationMembers_InvitationStatus",
                table: "OrganizationMembers",
                sql: "[InvitationStatus] IN ('Pending', 'Accepted', 'Declined', 'Revoked')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OrganizationMembers_InvitationStatus",
                table: "OrganizationMembers");

            migrationBuilder.AlterColumn<int>(
                name: "InvitationStatus",
                table: "OrganizationMembers",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrganizationMembers_InvitationStatus",
                table: "OrganizationMembers",
                sql: "[InvitationStatus] IN ('Pending', 'Accepted', 'Declined', 'Revoked')");
        }
    }
}

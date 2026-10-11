using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockpile.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLockoutEpisode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LockoutEpisode",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_LockoutEpisode",
                table: "Users",
                sql: "[LockoutEpisode] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_LockoutEpisode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockoutEpisode",
                table: "Users");
        }
    }
}

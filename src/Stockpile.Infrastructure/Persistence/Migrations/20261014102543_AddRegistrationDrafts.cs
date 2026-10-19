using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockpile.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegistrationDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RegistrationNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistrationDrafts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationDraftCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationDraftCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistrationDraftCategories_RegistrationDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "RegistrationDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationDraftCategories_DraftId_Slug",
                table: "RegistrationDraftCategories",
                columns: new[] { "DraftId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationDrafts_ExpiresAtUtc",
                table: "RegistrationDrafts",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationDrafts_TokenHash",
                table: "RegistrationDrafts",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationDrafts_UserId",
                table: "RegistrationDrafts",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistrationDraftCategories");

            migrationBuilder.DropTable(
                name: "RegistrationDrafts");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockpile.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentCategories",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    NameSi = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentCategories", x => x.PublicId);
                });

            migrationBuilder.CreateTable(
                name: "ContentItems",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Excerpt = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HeroImageMediaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EditorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ScheduledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MetaTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MetaDescription = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CanonicalUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Locale = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentItems", x => x.PublicId);
                    table.CheckConstraint("CK_ContentItems_Kind", "[Kind] IN ('NewsArticle', 'ProjectCaseStudy', 'SupplierStory', 'CategoryGuide', 'MaterialGuide', 'ProcurementEducation', 'SupplierSpotlight', 'TechnicalArticle')");
                    table.CheckConstraint("CK_ContentItems_Status", "[Status] IN ('Draft', 'Scheduled', 'Published', 'Archived')");
                    table.ForeignKey(
                        name: "FK_ContentItems_MediaAssets_HeroImageMediaId",
                        column: x => x.HeroImageMediaId,
                        principalTable: "MediaAssets",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ContentTags",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentTags", x => x.PublicId);
                });

            migrationBuilder.CreateTable(
                name: "ContentItemCategories",
                columns: table => new
                {
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentItemCategories", x => new { x.ContentId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_ContentItemCategories_ContentCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "ContentCategories",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentItemCategories_ContentItems_ContentId",
                        column: x => x.ContentId,
                        principalTable: "ContentItems",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentItemTags",
                columns: table => new
                {
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentItemTags", x => new { x.ContentId, x.TagId });
                    table.ForeignKey(
                        name: "FK_ContentItemTags_ContentItems_ContentId",
                        column: x => x.ContentId,
                        principalTable: "ContentItems",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentItemTags_ContentTags_TagId",
                        column: x => x.TagId,
                        principalTable: "ContentTags",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentCategories_Slug",
                table: "ContentCategories",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentItemCategories_CategoryId",
                table: "ContentItemCategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentItems_HeroImageMediaId",
                table: "ContentItems",
                column: "HeroImageMediaId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentItems_Locale_Slug",
                table: "ContentItems",
                columns: new[] { "Locale", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentItems_Status_Kind_PublishedAt",
                table: "ContentItems",
                columns: new[] { "Status", "Kind", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentItemTags_TagId",
                table: "ContentItemTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentTags_Slug",
                table: "ContentTags",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentItemCategories");

            migrationBuilder.DropTable(
                name: "ContentItemTags");

            migrationBuilder.DropTable(
                name: "ContentCategories");

            migrationBuilder.DropTable(
                name: "ContentItems");

            migrationBuilder.DropTable(
                name: "ContentTags");
        }
    }
}

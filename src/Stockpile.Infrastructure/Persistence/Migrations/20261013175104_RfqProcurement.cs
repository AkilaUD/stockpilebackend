using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockpile.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RfqProcurement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RFQs",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReferenceCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DeliveryAddressId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryLocationText = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RequestedDeliveryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BudgetAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    SubstitutionsAllowed = table.Column<bool>(type: "bit", nullable: false),
                    SiteConditions = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SpecialInstructions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Visibility = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ExpiryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RFQs", x => x.PublicId);
                    table.CheckConstraint("CK_RFQs_Source", "[Source] IN ('Manual', 'ProductSelection', 'DocumentImport', 'ProcurementList')");
                    table.CheckConstraint("CK_RFQs_Status", "[Status] IN ('Draft', 'Submitted', 'Matching', 'Dispatched', 'ReceivingQuotes', 'Negotiating', 'Awarded', 'PartiallyAwarded', 'Ordered', 'Cancelled', 'Closed', 'Expired')");
                    table.CheckConstraint("CK_RFQs_Visibility", "[Visibility] IN ('Public', 'MatchedOnly')");
                    table.ForeignKey(
                        name: "FK_RFQs_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RFQItems",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Specification = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitOfMeasureId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RFQItems", x => x.PublicId);
                    table.ForeignKey(
                        name: "FK_RFQItems_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RFQItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RFQItems_RFQs_RfqId",
                        column: x => x.RfqId,
                        principalTable: "RFQs",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RFQItems_UnitOfMeasures_UnitOfMeasureId",
                        column: x => x.UnitOfMeasureId,
                        principalTable: "UnitOfMeasures",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RFQMatches",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MatchScore = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    MatchReasons = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    NotifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ViewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeclineReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ExpiryAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RFQMatches", x => x.PublicId);
                    table.CheckConstraint("CK_RFQMatches_Status", "[Status] IN ('Matched', 'Notified', 'Viewed', 'Quoting', 'Declined', 'Expired', 'Blocked')");
                    table.ForeignKey(
                        name: "FK_RFQMatches_RFQs_RfqId",
                        column: x => x.RfqId,
                        principalTable: "RFQs",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RFQMatches_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RFQStatusHistory",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ToState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RFQStatusHistory", x => x.PublicId);
                    table.CheckConstraint("CK_RFQStatusHistory_FromState", "[FromState] IN ('Draft', 'Submitted', 'Matching', 'Dispatched', 'ReceivingQuotes', 'Negotiating', 'Awarded', 'PartiallyAwarded', 'Ordered', 'Cancelled', 'Closed', 'Expired')");
                    table.CheckConstraint("CK_RFQStatusHistory_ToState", "[ToState] IN ('Draft', 'Submitted', 'Matching', 'Dispatched', 'ReceivingQuotes', 'Negotiating', 'Awarded', 'PartiallyAwarded', 'Ordered', 'Cancelled', 'Closed', 'Expired')");
                    table.ForeignKey(
                        name: "FK_RFQStatusHistory_RFQs_RfqId",
                        column: x => x.RfqId,
                        principalTable: "RFQs",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_CategoryId",
                table: "RFQItems",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_ProductId",
                table: "RFQItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_RfqId_LineNumber",
                table: "RFQItems",
                columns: new[] { "RfqId", "LineNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_UnitOfMeasureId",
                table: "RFQItems",
                column: "UnitOfMeasureId");

            migrationBuilder.CreateIndex(
                name: "IX_RFQMatches_RfqId_SupplierId",
                table: "RFQMatches",
                columns: new[] { "RfqId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RFQMatches_SupplierId",
                table: "RFQMatches",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_RFQs_OrganizationId_Status",
                table: "RFQs",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RFQs_ReferenceCode",
                table: "RFQs",
                column: "ReferenceCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RFQStatusHistory_RfqId_OccurredAt",
                table: "RFQStatusHistory",
                columns: new[] { "RfqId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RFQItems");

            migrationBuilder.DropTable(
                name: "RFQMatches");

            migrationBuilder.DropTable(
                name: "RFQStatusHistory");

            migrationBuilder.DropTable(
                name: "RFQs");
        }
    }
}

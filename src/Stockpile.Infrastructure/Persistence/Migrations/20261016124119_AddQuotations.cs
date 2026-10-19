using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockpile.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Quotes",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqMatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CurrentRevisionNumber = table.Column<int>(type: "int", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeclinedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeclineReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AcceptedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.PublicId);
                    table.CheckConstraint("CK_Quotes_Status", "[Status] IN ('Draft', 'Submitted', 'UnderReview', 'RevisionRequested', 'Revised', 'Accepted', 'Rejected', 'Withdrawn', 'Expired')");
                    table.ForeignKey(
                        name: "FK_Quotes_RFQMatches_RfqMatchId",
                        column: x => x.RfqMatchId,
                        principalTable: "RFQMatches",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Quotes_RFQs_RfqId",
                        column: x => x.RfqId,
                        principalTable: "RFQs",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Quotes_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteRevisions",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    PreviousRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionKind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RequestedChangeRef = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscountTotal = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    DeliveryCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    GrandTotal = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LeadTimeDays = table.Column<int>(type: "int", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaymentTerms = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DeliveryTerms = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Inclusions = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    Exclusions = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    PackSizeNote = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MoqNote = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TechnicalNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SupplierNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteRevisions", x => x.PublicId);
                    table.CheckConstraint("CK_QuoteRevisions_Kind", "[RevisionKind] IN ('Initial', 'PriceRevision', 'LeadTimeRevision', 'ScopeRevision', 'ResponseToRequest')");
                    table.CheckConstraint("CK_QuoteRevisions_Status", "[Status] IN ('Active', 'Superseded', 'Withdrawn')");
                    table.CheckConstraint("CK_QuoteRevisions_TaxState", "[TaxState] IN ('TaxIncluded', 'TaxExcluded', 'TaxExempt')");
                    table.ForeignKey(
                        name: "FK_QuoteRevisions_QuoteRevisions_PreviousRevisionId",
                        column: x => x.PreviousRevisionId,
                        principalTable: "QuoteRevisions",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteRevisions_Quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "Quotes",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuoteRevisionItems",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductNameSnapshot = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SkuSnapshot = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitOfMeasureId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineDiscountPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    LineTotal = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    SubstitutionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteRevisionItems", x => x.PublicId);
                    table.ForeignKey(
                        name: "FK_QuoteRevisionItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteRevisionItems_QuoteRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "QuoteRevisions",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuoteRevisionItems_RFQItems_RfqItemId",
                        column: x => x.RfqItemId,
                        principalTable: "RFQItems",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteRevisionItems_UnitOfMeasures_UnitOfMeasureId",
                        column: x => x.UnitOfMeasureId,
                        principalTable: "UnitOfMeasures",
                        principalColumn: "PublicId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRevisionItems_ProductId",
                table: "QuoteRevisionItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRevisionItems_RevisionId",
                table: "QuoteRevisionItems",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRevisionItems_RfqItemId",
                table: "QuoteRevisionItems",
                column: "RfqItemId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRevisionItems_UnitOfMeasureId",
                table: "QuoteRevisionItems",
                column: "UnitOfMeasureId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRevisions_PreviousRevisionId",
                table: "QuoteRevisions",
                column: "PreviousRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRevisions_QuoteId_RevisionNumber",
                table: "QuoteRevisions",
                columns: new[] { "QuoteId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_QuoteNumber",
                table: "Quotes",
                column: "QuoteNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_RfqId_SupplierId",
                table: "Quotes",
                columns: new[] { "RfqId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_RfqMatchId",
                table: "Quotes",
                column: "RfqMatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_SupplierId",
                table: "Quotes",
                column: "SupplierId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuoteRevisionItems");

            migrationBuilder.DropTable(
                name: "QuoteRevisions");

            migrationBuilder.DropTable(
                name: "Quotes");
        }
    }
}

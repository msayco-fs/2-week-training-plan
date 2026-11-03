using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceReporting.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuarterlyReportEmailDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuarterlyReportEmailDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Quarter = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuarterlyReportEmailDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuarterlyReportEmailDeliveries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuarterlyReportEmailDeliveries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuarterlyReportEmailDeliveries_TenantId_UserId_Quarter",
                table: "QuarterlyReportEmailDeliveries",
                columns: new[] { "TenantId", "UserId", "Quarter" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuarterlyReportEmailDeliveries_UserId",
                table: "QuarterlyReportEmailDeliveries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuarterlyReportEmailDeliveries");
        }
    }
}

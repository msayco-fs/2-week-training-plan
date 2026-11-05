using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceReporting.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueQuarterlyReportGeneratedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_QuarterlyReportGenerated",
                table: "AuditLogs",
                columns: new[] { "TenantId", "Action", "Detail" },
                unique: true,
                filter: "[Action] = 'GenerateQuarterlyInvoiceReport'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_QuarterlyReportGenerated",
                table: "AuditLogs");
        }
    }
}

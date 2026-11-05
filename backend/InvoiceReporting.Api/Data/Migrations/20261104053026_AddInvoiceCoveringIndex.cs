using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceReporting.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_TenantId_IssuedDate",
                table: "Invoices");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId_IssuedDate",
                table: "Invoices",
                columns: new[] { "TenantId", "IssuedDate" })
                .Annotation("SqlServer:Include", new[] { "CustomerName", "Amount" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_TenantId_IssuedDate",
                table: "Invoices");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId_IssuedDate",
                table: "Invoices",
                columns: new[] { "TenantId", "IssuedDate" });
        }
    }
}

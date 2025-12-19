using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBaseAmountAndTaxAmountToSearchHires : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ STRIPE TAX: Agregar campo BaseAmount (base sin IVA/tax)
            migrationBuilder.AddColumn<decimal>(
                name: "BaseAmount",
                table: "SearchHires",
                type: "numeric",
                nullable: true);

            // ✅ STRIPE TAX: Agregar campo TaxAmount (IVA/tax calculado)
            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "SearchHires",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revertir: Eliminar campo TaxAmount de SearchHires
            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "SearchHires");

            // Revertir: Eliminar campo BaseAmount de SearchHires
            migrationBuilder.DropColumn(
                name: "BaseAmount",
                table: "SearchHires");
        }
    }
}

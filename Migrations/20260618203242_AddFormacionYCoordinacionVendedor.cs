using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddFormacionYCoordinacionVendedor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoordinationMode",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SellerBookingMaxDays",
                table: "SearchHires",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerBookingToken",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerEmail",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerListingUrl",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerPhone",
                table: "SearchHires",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoordinationMode",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "SellerBookingMaxDays",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "SellerBookingToken",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "SellerEmail",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "SellerListingUrl",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "SellerPhone",
                table: "SearchHires");
        }
    }
}

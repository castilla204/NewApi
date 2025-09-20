using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAdditionalFieldsToAppointment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DoorNumber",
                table: "Appointments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerPhone",
                table: "Appointments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SiteDetails",
                table: "Appointments",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DoorNumber",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "OwnerPhone",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "SiteDetails",
                table: "Appointments");
        }
    }
}

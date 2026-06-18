using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionTemplateToServiceAndHire : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InspectionTemplateConfig",
                table: "SearchServices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InspectionTemplatePdfUrl",
                table: "SearchServices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpertWorkLocationDetailsSnapshot",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpertWorkLocationDoorSnapshot",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpertWorkLocationFloorSnapshot",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InspectionTemplatePdfUrlSnapshot",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Formacion",
                table: "ExpertProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkLocationDetails",
                table: "ExpertProfiles",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkLocationDoor",
                table: "ExpertProfiles",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkLocationFloor",
                table: "ExpertProfiles",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InspectionTemplateConfig",
                table: "SearchServices");

            migrationBuilder.DropColumn(
                name: "InspectionTemplatePdfUrl",
                table: "SearchServices");

            migrationBuilder.DropColumn(
                name: "ExpertWorkLocationDetailsSnapshot",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "ExpertWorkLocationDoorSnapshot",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "ExpertWorkLocationFloorSnapshot",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "InspectionTemplatePdfUrlSnapshot",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "Formacion",
                table: "ExpertProfiles");

            migrationBuilder.DropColumn(
                name: "WorkLocationDetails",
                table: "ExpertProfiles");

            migrationBuilder.DropColumn(
                name: "WorkLocationDoor",
                table: "ExpertProfiles");

            migrationBuilder.DropColumn(
                name: "WorkLocationFloor",
                table: "ExpertProfiles");
        }
    }
}

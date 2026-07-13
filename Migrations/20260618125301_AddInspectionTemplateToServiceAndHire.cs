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
            // 🛡️ IDEMPOTENTE: en PROD estas columnas ya existían físicamente (drift de historial),
            // así que el AddColumn normal abortaba MigrateAsync con 42701 (duplicate_column) y
            // bloqueaba TODA la cadena (incl. AddDeviceToken). ADD COLUMN IF NOT EXISTS lo hace
            // no-op donde ya existen y las crea donde falten. Mismo patrón defensivo que otras
            // migraciones del repo ante el drift conocido.
            migrationBuilder.Sql(@"
                ALTER TABLE ""SearchServices"" ADD COLUMN IF NOT EXISTS ""InspectionTemplateConfig"" text;
                ALTER TABLE ""SearchServices"" ADD COLUMN IF NOT EXISTS ""InspectionTemplatePdfUrl"" text;
                ALTER TABLE ""SearchHires"" ADD COLUMN IF NOT EXISTS ""ExpertWorkLocationDetailsSnapshot"" text;
                ALTER TABLE ""SearchHires"" ADD COLUMN IF NOT EXISTS ""ExpertWorkLocationDoorSnapshot"" text;
                ALTER TABLE ""SearchHires"" ADD COLUMN IF NOT EXISTS ""ExpertWorkLocationFloorSnapshot"" text;
                ALTER TABLE ""SearchHires"" ADD COLUMN IF NOT EXISTS ""InspectionTemplatePdfUrlSnapshot"" text;
                ALTER TABLE ""ExpertProfiles"" ADD COLUMN IF NOT EXISTS ""Formacion"" text;
                ALTER TABLE ""ExpertProfiles"" ADD COLUMN IF NOT EXISTS ""WorkLocationDetails"" character varying(300);
                ALTER TABLE ""ExpertProfiles"" ADD COLUMN IF NOT EXISTS ""WorkLocationDoor"" character varying(60);
                ALTER TABLE ""ExpertProfiles"" ADD COLUMN IF NOT EXISTS ""WorkLocationFloor"" character varying(40);
            ");
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

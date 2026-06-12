using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// Radio de trabajo configurable por experto: ExpertProfiles.WorkRadiusKm.
    /// 0 = solo trabaja en su taller/punto fijo (el cliente se desplaza);
    /// máx 200 km (validado en servicio). Default 100 = la cobertura fija que
    /// el frontend anunciaba antes de hacer el radio configurable, para que los
    /// expertos existentes no cambien de comportamiento.
    ///
    /// Idempotente con ADD COLUMN IF NOT EXISTS para tolerar el drift de
    /// migraciones del proyecto (DDL aplicado a mano en prod vía MCP;
    /// EnsureCreated en tests ya crea la columna desde el modelo).
    /// </summary>
    public partial class AddExpertWorkRadiusKm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""ExpertProfiles""
                ADD COLUMN IF NOT EXISTS ""WorkRadiusKm"" integer NOT NULL DEFAULT 100;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""ExpertProfiles"" DROP COLUMN IF EXISTS ""WorkRadiusKm"";");
        }
    }
}

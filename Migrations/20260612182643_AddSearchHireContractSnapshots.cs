using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 🛡️ SNAPSHOT CONTRACTUAL: condiciones del servicio y ubicación del experto AL
    /// CONTRATAR, congeladas en el hire. El experto puede editar servicio/perfil
    /// después (para futuros clientes) sin afectar a lo ya contratado: el detalle del
    /// hire muestra el snapshot y la validación de la cita usa estas coordenadas.
    /// Nullable: hires anteriores hacen fallback al dato vivo.
    ///
    /// Idempotente con ADD COLUMN IF NOT EXISTS (drift de migraciones: DDL se aplica
    /// a mano en prod; EnsureCreated en tests ya crea las columnas desde el modelo).
    /// </summary>
    public partial class AddSearchHireContractSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""SearchHires"" ADD COLUMN IF NOT EXISTS ""ConditionsSnapshot"" text NULL;
                ALTER TABLE ""SearchHires"" ADD COLUMN IF NOT EXISTS ""DurationInHoursSnapshot"" integer NULL;
                ALTER TABLE ""SearchHires"" ADD COLUMN IF NOT EXISTS ""ExpertLatitudeSnapshot"" text NULL;
                ALTER TABLE ""SearchHires"" ADD COLUMN IF NOT EXISTS ""ExpertLongitudeSnapshot"" text NULL;
                ALTER TABLE ""SearchHires"" ADD COLUMN IF NOT EXISTS ""ExpertWorkRadiusKmSnapshot"" integer NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""SearchHires"" DROP COLUMN IF EXISTS ""ConditionsSnapshot"";
                ALTER TABLE ""SearchHires"" DROP COLUMN IF EXISTS ""DurationInHoursSnapshot"";
                ALTER TABLE ""SearchHires"" DROP COLUMN IF EXISTS ""ExpertLatitudeSnapshot"";
                ALTER TABLE ""SearchHires"" DROP COLUMN IF EXISTS ""ExpertLongitudeSnapshot"";
                ALTER TABLE ""SearchHires"" DROP COLUMN IF EXISTS ""ExpertWorkRadiusKmSnapshot"";
            ");
        }
    }
}

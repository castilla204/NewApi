using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// Seguridad (defensa en profundidad): índice ÚNICO filtrado sobre
    /// ExpertProfiles.StripeAccountId. Impide a nivel de datos que dos
    /// ExpertProfiles compartan la misma cuenta de payout de Stripe.
    ///
    /// Verificado vía MCP Postgres antes de aplicar:
    ///   - prod NO tiene índice sobre StripeAccountId (solo PK + UserId único).
    ///   - 0 filas duplicadas con el mismo StripeAccountId → la creación es segura.
    ///
    /// Idempotente con CREATE UNIQUE INDEX IF NOT EXISTS para tolerar el drift de
    /// migraciones del proyecto (re-aplicaciones / EnsureCreated en tests).
    /// </summary>
    public partial class AddUniqueIndexExpertProfileStripeAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF NOT EXISTS: no-op si el índice ya existe (BD frescas vía EnsureCreated
            // ya lo crean desde el modelo). Filtrado a NOT NULL: los expertos sin
            // onboarding (StripeAccountId=null) no colisionan entre sí.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ExpertProfiles_StripeAccountId""
                ON ""ExpertProfiles"" (""StripeAccountId"")
                WHERE ""StripeAccountId"" IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_ExpertProfiles_StripeAccountId"";");
        }
    }
}

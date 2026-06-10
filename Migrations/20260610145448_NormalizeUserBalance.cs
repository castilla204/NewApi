using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// Normaliza la columna <c>Users.Balance</c> a nivel de modelo EF.
    ///
    /// Contexto del bug histórico:
    ///   - 20250119000002_RemoveBalanceFieldFromUsers dropeaba la columna.
    ///   - 20250927104428_AddBalanceNonNegativeConstraint añadía un CHECK sobre la
    ///     columna ya removida, sin re-añadirla. La columna sigue existiendo en
    ///     producción (alguien la restauró fuera de EF), con datos todos a 0. El
    ///     modelo C# no la exponía → EnsureCreatedAsync fallaba en cualquier BD
    ///     nueva (tests, dev fresca).
    ///
    /// Esta migración:
    ///   - Añade la columna SOLO si no existe (idempotente: no-op en prod, crea
    ///     en BDs frescas que no la tengan).
    ///   - El snapshot ya queda alineado con el modelo (User.Balance), por lo que
    ///     futuras migraciones no detectarán falsos cambios.
    /// </summary>
    public partial class NormalizeUserBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADD COLUMN IF NOT EXISTS: idempotente. En prod no hace nada (la
            // columna ya existe). En BDs frescas crea la columna que el CHECK
            // constraint posterior necesita.
            migrationBuilder.Sql(@"
                ALTER TABLE ""Users""
                ADD COLUMN IF NOT EXISTS ""Balance"" numeric NOT NULL DEFAULT 0;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Balance",
                table: "Users");
        }
    }
}

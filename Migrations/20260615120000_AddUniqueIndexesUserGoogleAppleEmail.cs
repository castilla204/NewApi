using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 🛡️ Integridad de datos (race check-then-insert en login social/email):
    /// Antes NO existía constraint único sobre Users.GoogleId / Users.AppleId / Users.Email.
    /// La unicidad se "validaba" solo en el service layer con check-then-insert SIN transacción
    /// ni constraint → dos requests concurrentes podían fallar ambas el lookup y ambas INSERT,
    /// creando filas duplicadas con el mismo sub/email. Lookups posteriores por email resolvían
    /// de forma no determinista.
    ///
    /// Esta migración convierte los índices a ÚNICOS:
    ///   - GoogleId / AppleId: índice único PARCIAL (WHERE col IS NOT NULL). Se DROP+CREATE
    ///     porque ya existían como índices no-únicos. Los placeholders email:{guid} y los
    ///     anonimizados deleted-{id} son únicos por id → no colisionan.
    ///   - Email: índice único FUNCIONAL sobre lower("Email") excluyendo filas soft-deleted
    ///     (IsDeleted = false). Nombre nuevo IX_Users_Email_lower_uq; se MANTIENE el índice
    ///     no-único IX_Users_Email para el lookup existente. La anonimización reescribe el email
    ///     de los borrados a deleted-{id}@deleted.local (único por id) y además quedan fuera del
    ///     filtro, por lo que reactivar/recrear un email previamente borrado no colisiona.
    ///
    /// Verificado vía MCP Postgres contra la réplica dev (inspecciono_dev) antes de aplicar:
    ///   - Los 3 índices existían como CREATE INDEX (no únicos).
    ///   - 0 filas con GoogleId/AppleId duplicado.
    ///   - 0 emails (lower, no borrados) duplicados → la creación de los 3 índices es segura.
    ///
    /// Idempotente (CREATE UNIQUE INDEX IF NOT EXISTS / DROP INDEX IF EXISTS) para tolerar el
    /// drift de migraciones de prod (__EFMigrationsHistory llega solo a 20260530; esquema físico
    /// aplicado a mano). Si esta migración no auto-aplica en prod, ejecutar el mismo SQL a mano.
    /// El paso de Email va al final y puede fallar si hay duplicados preexistentes: en ese caso
    /// resolver los duplicados primero; NO bloquea el fix de GoogleId/AppleId.
    /// </summary>
    public partial class AddUniqueIndexesUserGoogleAppleEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── GoogleId: no-único → único parcial ──────────────────────────────────────
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Users_GoogleId"";");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Users_GoogleId""
                ON ""Users"" (""GoogleId"")
                WHERE ""GoogleId"" IS NOT NULL;
            ");

            // ── AppleId: no-único → único parcial ───────────────────────────────────────
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Users_AppleId"";");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Users_AppleId""
                ON ""Users"" (""AppleId"")
                WHERE ""AppleId"" IS NOT NULL;
            ");

            // ── Email: índice único FUNCIONAL sobre lower(email), excluyendo soft-deleted ─
            // (paso final; puede fallar si existen duplicados case-insensitive no borrados).
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Users_Email_lower_uq""
                ON ""Users"" (lower(""Email""))
                WHERE ""IsDeleted"" = false;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Quitar el índice funcional de email.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Users_Email_lower_uq"";");

            // Revertir AppleId a índice NO único.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Users_AppleId"";");
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_Users_AppleId""
                ON ""Users"" (""AppleId"");
            ");

            // Revertir GoogleId a índice NO único.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Users_GoogleId"";");
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_Users_GoogleId""
                ON ""Users"" (""GoogleId"");
            ");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 🛡️ Fiabilidad del registro email/password (registro pendiente durable):
    /// El flujo de registro es en dos fases. POST /auth/register NO crea el User: hashea la
    /// contraseña (BCrypt) y guardaba (email, name, passwordHash) en un MemoryCache process-local
    /// (AuthController._registrationCache), keyado por el mismo VerificationToken que el OTP. El
    /// User se creaba en /auth/verify-email tras confirmar el OTP.
    ///
    /// Como el stash era en memoria y single-process, si el proceso de Render se
    /// reiniciaba/redesplegaba (o crasheaba) entre register y verify-email, el stash se perdía: el
    /// usuario enviaba un OTP CORRECTO pero pending==null → caía en "registration_expired" y la
    /// cuenta NUNCA se creaba. Tampoco funcionaba multi-réplica.
    ///
    /// Fix: persistir el registro pendiente en la propia fila del OTP (EmailVerificationCodes),
    /// añadiendo dos columnas nullable: PendingName y PendingPasswordHash (hash BCrypt, NUNCA
    /// plano). Se escriben al emitir el OTP de registro y se leen en verify-email. Reusa el ciclo
    /// de vida / cleanup de 15 min ya existente del OTP. El MemoryCache se mantiene solo como
    /// fast-path; la BD es la fuente de verdad, así que un reinicio ya no deja registros colgados.
    ///
    /// Idempotente (ADD COLUMN IF NOT EXISTS) para tolerar el drift de migraciones de prod
    /// (__EFMigrationsHistory llega solo a 20260530; esquema físico aplicado a mano; 42P07 en boot
    /// es inofensivo). Si esta migración no auto-aplica en prod, ejecutar el mismo SQL a mano.
    /// </summary>
    public partial class AddPendingRegistrationToEmailVerificationCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""EmailVerificationCodes""
                ADD COLUMN IF NOT EXISTS ""PendingName"" character varying(200) NULL;
            ");
            migrationBuilder.Sql(@"
                ALTER TABLE ""EmailVerificationCodes""
                ADD COLUMN IF NOT EXISTS ""PendingPasswordHash"" character varying(255) NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""EmailVerificationCodes"" DROP COLUMN IF EXISTS ""PendingPasswordHash"";");
            migrationBuilder.Sql(@"ALTER TABLE ""EmailVerificationCodes"" DROP COLUMN IF EXISTS ""PendingName"";");
        }
    }
}

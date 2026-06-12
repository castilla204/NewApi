using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 📱 SMS-CENTRAL: clasificación del teléfono del usuario.
    /// - PhoneLineType: "mobile" | "landline" | "voip" | "unknown" (Twilio Lookup).
    ///   Un FIJO no recibe SMS → el panel del experto lo marca como no válido y pide
    ///   cargar un móvil verificado por OTP.
    /// - PhoneVerificationSource: "stripe_kyc" | "checkout" | "otp".
    /// Idempotente (drift de migraciones: DDL a mano en prod; EnsureCreated en tests).
    /// </summary>
    public partial class AddUserPhoneLineType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""PhoneLineType"" text NULL;
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""PhoneVerificationSource"" text NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""Users"" DROP COLUMN IF EXISTS ""PhoneLineType"";
                ALTER TABLE ""Users"" DROP COLUMN IF EXISTS ""PhoneVerificationSource"";
            ");
        }
    }
}

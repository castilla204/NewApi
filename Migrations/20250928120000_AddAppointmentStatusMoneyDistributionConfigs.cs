using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentStatusMoneyDistributionConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Obtener los IDs de los estados de AppointmentStatus
            var appointmentCompletedStatusId = migrationBuilder.Sql("SELECT Id FROM \"SystemStatuses\" WHERE \"StatusType\" = 'AppointmentStatus' AND \"StatusValue\" = 'appointment_completed'").ToString();
            var appointmentCancelledStatusId = migrationBuilder.Sql("SELECT Id FROM \"SystemStatuses\" WHERE \"StatusType\" = 'AppointmentStatus' AND \"StatusValue\" = 'appointment_cancelled'").ToString();
            var appointmentRejectedStatusId = migrationBuilder.Sql("SELECT Id FROM \"SystemStatuses\" WHERE \"StatusType\" = 'AppointmentStatus' AND \"StatusValue\" = 'appointment_rejected'").ToString();

            // Insertar configuraciones de distribución de dinero para estados de AppointmentStatus
            // Estas configuraciones son principalmente para referencia, ya que los estados de AppointmentStatus
            // no suelen generar transferencias directas de dinero, pero pueden ser útiles para el panel de admin
            
            // Configuración global para appointment_completed (si se usa para transferencias)
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    s.""Id"",
                    NULL,
                    NULL,
                    0,
                    95,
                    5,
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus' 
                AND s.""StatusValue"" = 'appointment_completed'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // Configuración global para appointment_cancelled (refund completo al cliente)
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    s.""Id"",
                    NULL,
                    NULL,
                    100,
                    0,
                    0,
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus' 
                AND s.""StatusValue"" = 'appointment_cancelled'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // Configuración global para appointment_rejected (refund completo al cliente)
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    s.""Id"",
                    NULL,
                    NULL,
                    100,
                    0,
                    0,
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus' 
                AND s.""StatusValue"" = 'appointment_rejected'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Eliminar las configuraciones agregadas
            migrationBuilder.Sql(@"
                DELETE FROM ""StatusConfigurations"" 
                WHERE ""StatusId"" IN (
                    SELECT s.""Id"" FROM ""SystemStatuses"" s
                    WHERE s.""StatusType"" = 'AppointmentStatus' 
                    AND s.""StatusValue"" IN ('appointment_completed', 'appointment_cancelled', 'appointment_rejected')
                )
                AND ""CategoryId"" IS NULL 
                AND ""ServiceTypeCategoryId"" IS NULL;
            ");
        }
    }
}


using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class InsertAppointmentStatusConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Insertar configuraciones de distribución de dinero para estados de AppointmentStatus
            // Basado en los porcentajes que habíamos acordado anteriormente
            
            // 1. APPOINTMENT_COMPLETED - Cuando la cita se completa exitosamente
            // Cliente: 0%, Experto: 95%, Plataforma: 5%
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

            // 2. APPOINTMENT_CANCELLED - Cuando se cancela (refund completo al cliente)
            // Cliente: 100%, Experto: 0%, Plataforma: 0%
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

            // 3. APPOINTMENT_REJECTED - Cuando se rechaza (refund completo al cliente)
            // Cliente: 100%, Experto: 0%, Plataforma: 0%
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

            // 4. APPOINTMENT_CANCELLED_BY_CLIENT - Primera cancelación del cliente
            // Cliente: 100%, Experto: 0%, Plataforma: 0%
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
                AND s.""StatusValue"" = 'appointment_cancelled_by_client'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 5. APPOINTMENT_CANCELLED_BY_CLIENT_SECOND - Segunda cancelación del cliente
            // Cliente: 100%, Experto: 0%, Plataforma: 0%
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
                AND s.""StatusValue"" = 'appointment_cancelled_by_client_second'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 6. APPOINTMENT_CANCELLED_BY_EXPERT - Experto cancela voluntariamente
            // Cliente: 100%, Experto: 0%, Plataforma: 0%
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
                AND s.""StatusValue"" = 'appointment_cancelled_by_expert'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 7. APPOINTMENT_CANCELLED_BY_NO_RESPONSE - Cliente no propuso en tiempo
            // Cliente: 100%, Experto: 0%, Plataforma: 0%
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
                AND s.""StatusValue"" = 'appointment_cancelled_by_no_response'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 8. APPOINTMENT_CANCELLED_BY_EXPERT_REJECTION - Experto rechazó 2 veces
            // Cliente: 100%, Experto: 0%, Plataforma: 0%
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
                AND s.""StatusValue"" = 'appointment_cancelled_by_expert_rejection'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 9. AWAITING_APPOINTMENT - Esperando propuesta del cliente (48h)
            // No hay transferencia de dinero, pero configuramos por completitud
            // Cliente: 0%, Experto: 0%, Plataforma: 0%
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    s.""Id"",
                    NULL,
                    NULL,
                    0,
                    0,
                    0,
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus' 
                AND s.""StatusValue"" = 'awaiting_appointment'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 10. APPOINTMENT_PROPOSED - Cliente propuso cita
            // No hay transferencia de dinero, pero configuramos por completitud
            // Cliente: 0%, Experto: 0%, Plataforma: 0%
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    s.""Id"",
                    NULL,
                    NULL,
                    0,
                    0,
                    0,
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus' 
                AND s.""StatusValue"" = 'appointment_proposed'
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
            // Eliminar todas las configuraciones de AppointmentStatus
            migrationBuilder.Sql(@"
                DELETE FROM ""StatusConfigurations"" 
                WHERE ""StatusId"" IN (
                    SELECT s.""Id"" FROM ""SystemStatuses"" s
                    WHERE s.""StatusType"" = 'AppointmentStatus'
                );
            ");
        }
    }
}

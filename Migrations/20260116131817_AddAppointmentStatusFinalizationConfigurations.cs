using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentStatusFinalizationConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<TimeSpan>(
                name: "ProposedTime",
                table: "Appointments",
                type: "interval",
                nullable: true,
                oldClrType: typeof(TimeSpan),
                oldType: "interval");

            migrationBuilder.AlterColumn<DateTime>(
                name: "ProposedDate",
                table: "Appointments",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<string>(
                name: "Location",
                table: "Appointments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            // ✅ AGREGAR CONFIGURACIONES DE DISTRIBUCIÓN DE DINERO PARA ESTADOS DE FINALIZACIÓN
            // Estos estados son críticos para el procesamiento de dinero cuando se ejecutan los timers
            
            // 1. appointment_cancelled_by_client_no_proposal - Cliente no propuso en 24h
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT s.""Id"", NULL, NULL, 0, 100, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus'
                AND s.""StatusValue"" = 'appointment_cancelled_by_client_no_proposal'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 2. appointment_cancelled_by_expert_no_response - Experto no respondió en 24h
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT s.""Id"", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus' 
                AND s.""StatusValue"" = 'appointment_cancelled_by_expert_no_response'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 3. appointment_cancelled_by_no_report - Experto no envió reporte en 24h
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT s.""Id"", NULL, NULL, 95, 0, 5, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus' 
                AND s.""StatusValue"" = 'appointment_cancelled_by_no_report'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 4. appointment_completed_without_client_approval - Cliente no decidió en 24h (a favor del experto)
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT s.""Id"", NULL, NULL, 0, 95, 5, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus'
                AND s.""StatusValue"" = 'appointment_completed_without_client_approval'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 5. appointment_cancelled_by_expert_second - Segunda cancelación del experto
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT s.""Id"", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" s
                WHERE s.""StatusType"" = 'AppointmentStatus' 
                AND s.""StatusValue"" = 'appointment_cancelled_by_expert_second'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusConfigurations"" sc 
                    WHERE sc.""StatusId"" = s.""Id"" 
                    AND sc.""CategoryId"" IS NULL 
                    AND sc.""ServiceTypeCategoryId"" IS NULL
                );
            ");

            // 6. appointment_cancelled_by_client_second - Segunda cancelación del cliente
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT s.""Id"", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
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

            // 7. appointment_cancelled_by_expert_rejection - Experto rechazó 2 veces
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT s.""Id"", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
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

            // 8. appointment_cancelled_by_no_response - [DEPRECATED] Mantener por compatibilidad
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT s.""Id"", NULL, NULL, 100, 0, 0, true, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<TimeSpan>(
                name: "ProposedTime",
                table: "Appointments",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0),
                oldClrType: typeof(TimeSpan),
                oldType: "interval",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ProposedDate",
                table: "Appointments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Location",
                table: "Appointments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            // Eliminar configuraciones agregadas en Up()
            migrationBuilder.Sql(@"
                DELETE FROM ""StatusConfigurations"" 
                WHERE ""StatusId"" IN (
                    SELECT s.""Id"" FROM ""SystemStatuses"" s
                    WHERE s.""StatusType"" = 'AppointmentStatus' 
                    AND s.""StatusValue"" IN (
                        'appointment_cancelled_by_client_no_proposal',
                        'appointment_cancelled_by_expert_no_response',
                        'appointment_cancelled_by_no_report',
                        'appointment_completed_without_client_approval',
                        'appointment_cancelled_by_expert_second',
                        'appointment_cancelled_by_client_second',
                        'appointment_cancelled_by_expert_rejection',
                        'appointment_cancelled_by_no_response'
                    )
                )
                AND ""CategoryId"" IS NULL 
                AND ""ServiceTypeCategoryId"" IS NULL;
            ");
        }
    }
}

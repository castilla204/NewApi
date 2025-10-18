using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentReportSentStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Crear el estado appointment_report_sent si no existe
            migrationBuilder.Sql(@"
                INSERT INTO ""SystemStatuses"" (""StatusType"", ""StatusName"", ""StatusValue"", ""DisplayName"", ""Description"", ""IsActive"", ""SortOrder"", ""IsFinalizationStatus"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'AppointmentStatus', 'AppointmentReportSent', 'appointment_report_sent', 'Reporte Enviado', 'Experto envió el reporte', true, 11, false, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                WHERE NOT EXISTS (
                    SELECT 1 FROM ""SystemStatuses"" 
                    WHERE ""StatusType"" = 'AppointmentStatus' 
                    AND ""StatusValue"" = 'appointment_report_sent'
                )
            ");

            // 2. Crear el mapeo appointment_report_sent -> awaiting_client_decision si no existe
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusMappings"" (""SourceStatusId"", ""TargetStatusId"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    source.""Id"",
                    target.""Id"",
                    true,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM ""SystemStatuses"" source
                CROSS JOIN ""SystemStatuses"" target
                WHERE source.""StatusType"" = 'AppointmentStatus' 
                AND source.""StatusValue"" = 'appointment_report_sent'
                AND target.""StatusType"" = 'SearchHireStatus' 
                AND target.""StatusValue"" = 'awaiting_client_decision'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusMappings"" sm
                    WHERE sm.""SourceStatusId"" = source.""Id""
                    AND sm.""TargetStatusId"" = target.""Id""
                )
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Eliminar el mapeo
            migrationBuilder.Sql(@"
                DELETE FROM ""StatusMappings"" 
                WHERE ""SourceStatusId"" IN (
                    SELECT ""Id"" FROM ""SystemStatuses"" 
                    WHERE ""StatusType"" = 'AppointmentStatus' 
                    AND ""StatusValue"" = 'appointment_report_sent'
                )
            ");

            // Eliminar el estado
            migrationBuilder.Sql(@"
                DELETE FROM ""SystemStatuses"" 
                WHERE ""StatusType"" = 'AppointmentStatus' 
                AND ""StatusValue"" = 'appointment_report_sent'
            ");
        }
    }
}

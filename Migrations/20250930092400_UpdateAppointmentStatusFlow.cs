using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class UpdateAppointmentStatusFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Primero eliminar los mapeos que referencian appointment_completed
            migrationBuilder.Sql(@"
                DELETE FROM ""StatusMappings"" 
                WHERE ""SourceStatusId"" IN (
                    SELECT ""Id"" FROM ""SystemStatuses"" 
                    WHERE ""StatusType"" = 'AppointmentStatus' 
                    AND ""StatusValue"" = 'appointment_completed'
                )
            ");

            // 2. Ahora eliminar el estado appointment_completed
            migrationBuilder.Sql(@"
                DELETE FROM ""SystemStatuses"" 
                WHERE ""StatusType"" = 'AppointmentStatus' 
                AND ""StatusValue"" = 'appointment_completed'
            ");

            // 3. Verificar si appointment_awaiting_report ya existe, si no, crearlo
            migrationBuilder.Sql(@"
                INSERT INTO ""SystemStatuses"" (""StatusType"", ""StatusName"", ""StatusValue"", ""DisplayName"", ""Description"", ""IsActive"", ""SortOrder"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'AppointmentStatus', 'AppointmentAwaitingReport', 'appointment_awaiting_report', 'Esperando Reporte', 'Esperando reporte/archivos del experto (3h después de la cita)', true, 10, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                WHERE NOT EXISTS (
                    SELECT 1 FROM ""SystemStatuses"" 
                    WHERE ""StatusType"" = 'AppointmentStatus' 
                    AND ""StatusValue"" = 'appointment_awaiting_report'
                )
            ");

            // 4. Agregar mapeo de appointment_awaiting_report -> awaiting_client_decision (solo si no existe)
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusMappings"" (""SourceStatusId"", ""TargetStatusId"", ""IsActive"", ""CreatedAt"")
                SELECT 
                    s1.""Id"" as SourceStatusId,
                    s2.""Id"" as TargetStatusId,
                    true as IsActive,
                    CURRENT_TIMESTAMP as CreatedAt
                FROM ""SystemStatuses"" s1
                CROSS JOIN ""SystemStatuses"" s2
                WHERE s1.""StatusValue"" = 'appointment_awaiting_report' 
                AND s2.""StatusValue"" = 'awaiting_client_decision'
                AND NOT EXISTS (
                    SELECT 1 FROM ""StatusMappings"" sm
                    WHERE sm.""SourceStatusId"" = s1.""Id""
                    AND sm.""TargetStatusId"" = s2.""Id""
                )
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1. Eliminar el mapeo de appointment_awaiting_report -> awaiting_client_decision
            migrationBuilder.Sql(@"
                DELETE FROM ""StatusMappings"" 
                WHERE ""SourceStatusId"" IN (
                    SELECT ""Id"" FROM ""SystemStatuses"" 
                    WHERE ""StatusType"" = 'AppointmentStatus' 
                    AND ""StatusValue"" = 'appointment_awaiting_report'
                )
            ");

            // 2. Eliminar el estado appointment_awaiting_report
            migrationBuilder.Sql(@"
                DELETE FROM ""SystemStatuses"" 
                WHERE ""StatusType"" = 'AppointmentStatus' 
                AND ""StatusValue"" = 'appointment_awaiting_report'
            ");

            // 3. Restaurar el estado appointment_completed
            migrationBuilder.Sql(@"
                INSERT INTO ""SystemStatuses"" (""StatusType"", ""StatusName"", ""StatusValue"", ""DisplayName"", ""Description"", ""IsActive"", ""SortOrder"", ""CreatedAt"", ""UpdatedAt"")
                VALUES ('AppointmentStatus', 'AppointmentCompleted', 'appointment_completed', 'Cita Completada', 'Cita realizada exitosamente', true, 10, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ");

            // 4. Restaurar el mapeo de appointment_completed -> awaiting_client_decision
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusMappings"" (""SourceStatusId"", ""TargetStatusId"", ""IsActive"", ""CreatedAt"")
                SELECT 
                    s1.""Id"" as SourceStatusId,
                    s2.""Id"" as TargetStatusId,
                    true as IsActive,
                    CURRENT_TIMESTAMP as CreatedAt
                FROM ""SystemStatuses"" s1
                CROSS JOIN ""SystemStatuses"" s2
                WHERE s1.""StatusValue"" = 'appointment_completed' 
                AND s2.""StatusValue"" = 'awaiting_client_decision'
            ");
        }
    }
}

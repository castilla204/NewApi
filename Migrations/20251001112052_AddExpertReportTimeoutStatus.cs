using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddExpertReportTimeoutStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Agregar el estado para cuando el experto no envía reporte en 24h
            migrationBuilder.Sql(@"
                INSERT INTO ""SystemStatuses"" (""StatusType"", ""StatusName"", ""StatusValue"", ""DisplayName"", ""Description"", ""IsActive"", ""SortOrder"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'AppointmentStatus', 'AppointmentCancelledByNoReport', 'appointment_cancelled_by_no_report', 'Cancelada por No Reporte', 'Cita cancelada porque el experto no envió reporte en 24h', true, 15, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                WHERE NOT EXISTS (
                    SELECT 1 FROM ""SystemStatuses"" 
                    WHERE ""StatusType"" = 'AppointmentStatus' 
                    AND ""StatusValue"" = 'appointment_cancelled_by_no_report'
                )
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Eliminar el estado de cancelación por no reporte
            migrationBuilder.Sql(@"
                DELETE FROM ""SystemStatuses"" 
                WHERE ""StatusType"" = 'AppointmentStatus' 
                AND ""StatusValue"" = 'appointment_cancelled_by_no_report'
            ");
        }
    }
}

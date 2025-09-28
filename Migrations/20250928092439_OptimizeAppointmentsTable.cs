using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeAppointmentsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== OPTIMIZAR TABLA APPOINTMENTS =====
            
            // 1. Agregar columna StatusId que referencia a SystemStatuses
            migrationBuilder.AddColumn<int>(
                name: "StatusId",
                table: "Appointments",
                type: "integer",
                nullable: true);

            // 2. Crear índice para la nueva columna
            migrationBuilder.CreateIndex(
                name: "IX_Appointments_StatusId",
                table: "Appointments",
                column: "StatusId");

            // 3. Migrar datos existentes: mapear Status string a StatusId
            migrationBuilder.Sql(@"
                UPDATE ""Appointments"" 
                SET ""StatusId"" = (
                    SELECT ss.""Id"" 
                    FROM ""SystemStatuses"" ss 
                    WHERE ss.""StatusType"" = 'AppointmentStatus' 
                    AND ss.""StatusValue"" = ""Appointments"".""Status""
                )
            ");

            // 4. Hacer StatusId NOT NULL después de migrar datos
            migrationBuilder.AlterColumn<int>(
                name: "StatusId",
                table: "Appointments",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            // 5. Crear foreign key constraint
            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_SystemStatuses_StatusId",
                table: "Appointments",
                column: "StatusId",
                principalTable: "SystemStatuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // 6. Eliminar el campo Status antiguo
            migrationBuilder.DropColumn(
                name: "Status",
                table: "Appointments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ===== REVERTIR CAMBIOS =====
            
            // 1. Agregar de vuelta el campo Status
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Appointments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "awaiting_appointment");

            // 2. Migrar datos de vuelta: StatusId a Status string
            migrationBuilder.Sql(@"
                UPDATE ""Appointments"" 
                SET ""Status"" = (
                    SELECT ss.""StatusValue"" 
                    FROM ""SystemStatuses"" ss 
                    WHERE ss.""Id"" = ""Appointments"".""StatusId""
                )
            ");

            // 3. Eliminar foreign key constraint
            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_SystemStatuses_StatusId",
                table: "Appointments");

            // 4. Eliminar índice
            migrationBuilder.DropIndex(
                name: "IX_Appointments_StatusId",
                table: "Appointments");

            // 5. Eliminar columna StatusId
            migrationBuilder.DropColumn(
                name: "StatusId",
                table: "Appointments");
        }
    }
}

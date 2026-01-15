using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class MakeAppointmentFieldsNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hacer nullable ProposedDate, ProposedTime y Location
            // Estos campos se asignan cuando el cliente propone la cita, no al crearla
            migrationBuilder.AlterColumn<DateTime>(
                name: "ProposedDate",
                table: "Appointments",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<TimeSpan>(
                name: "ProposedTime",
                table: "Appointments",
                type: "interval",
                nullable: true,
                oldClrType: typeof(TimeSpan),
                oldType: "interval");

            migrationBuilder.AlterColumn<string>(
                name: "Location",
                table: "Appointments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revertir a NOT NULL (requiere valores por defecto para registros existentes)
            migrationBuilder.AlterColumn<DateTime>(
                name: "ProposedDate",
                table: "Appointments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTime.UtcNow.Date,
                oldClrType: typeof(DateTime),
                oldNullable: true);

            migrationBuilder.AlterColumn<TimeSpan>(
                name: "ProposedTime",
                table: "Appointments",
                type: "interval",
                nullable: false,
                defaultValue: TimeSpan.Zero,
                oldClrType: typeof(TimeSpan),
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Location",
                table: "Appointments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldNullable: true,
                oldMaxLength: 500);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class ChangeSearchHireStatusToStatusId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Primero agregar la nueva columna StatusId
            migrationBuilder.AddColumn<int>(
                name: "StatusId",
                table: "SearchHires",
                type: "integer",
                nullable: false,
                defaultValue: 1); // Default a "pending" (ID = 1)

            // Mapear los valores existentes de Status a StatusId
            migrationBuilder.Sql(@"
                UPDATE ""SearchHires"" 
                SET ""StatusId"" = CASE 
                    WHEN ""Status"" = 'pending' THEN 1
                    WHEN ""Status"" = 'awaiting_client_decision' THEN 2
                    WHEN ""Status"" = 'disputed' THEN 3
                    WHEN ""Status"" = 'completed' THEN 4
                    WHEN ""Status"" = 'cancelled' THEN 5
                    WHEN ""Status"" = 'transfer_failed' THEN 6
                    WHEN ""Status"" = 'dispute_resolved_client' THEN 7
                    WHEN ""Status"" = 'dispute_resolved_expert' THEN 8
                    ELSE 1 -- Default a pending si no coincide
                END
            ");

            // Agregar foreign key constraint
            migrationBuilder.AddForeignKey(
                name: "FK_SearchHires_SystemStatuses_StatusId",
                table: "SearchHires",
                column: "StatusId",
                principalTable: "SystemStatuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Eliminar la columna Status antigua
            migrationBuilder.DropColumn(
                name: "Status",
                table: "SearchHires");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Agregar de vuelta la columna Status
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "SearchHires",
                type: "text",
                nullable: false,
                defaultValue: "pending");

            // Mapear de vuelta StatusId a Status
            migrationBuilder.Sql(@"
                UPDATE ""SearchHires"" 
                SET ""Status"" = CASE 
                    WHEN ""StatusId"" = 1 THEN 'pending'
                    WHEN ""StatusId"" = 2 THEN 'awaiting_client_decision'
                    WHEN ""StatusId"" = 3 THEN 'disputed'
                    WHEN ""StatusId"" = 4 THEN 'completed'
                    WHEN ""StatusId"" = 5 THEN 'cancelled'
                    WHEN ""StatusId"" = 6 THEN 'transfer_failed'
                    WHEN ""StatusId"" = 7 THEN 'dispute_resolved_client'
                    WHEN ""StatusId"" = 8 THEN 'dispute_resolved_expert'
                    ELSE 'pending'
                END
            ");

            // Eliminar foreign key constraint
            migrationBuilder.DropForeignKey(
                name: "FK_SearchHires_SystemStatuses_StatusId",
                table: "SearchHires");

            // Eliminar la columna StatusId
            migrationBuilder.DropColumn(
                name: "StatusId",
                table: "SearchHires");
        }
    }
}

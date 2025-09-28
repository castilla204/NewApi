using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class CreateCentralizedStatusSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StatusType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusValue = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StatusConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StatusId = table.Column<int>(type: "integer", nullable: false),
                    CategoryId = table.Column<int>(type: "integer", nullable: true),
                    ServiceTypeCategoryId = table.Column<int>(type: "integer", nullable: true),
                    ClientPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    ExpertPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    PlatformPercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusConfigurations_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StatusConfigurations_ServiceTypeCategories_ServiceTypeCateg~",
                        column: x => x.ServiceTypeCategoryId,
                        principalTable: "ServiceTypeCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StatusConfigurations_SystemStatuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "SystemStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceStatusId = table.Column<int>(type: "integer", nullable: false),
                    TargetStatusId = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusMappings_SystemStatuses_SourceStatusId",
                        column: x => x.SourceStatusId,
                        principalTable: "SystemStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StatusMappings_SystemStatuses_TargetStatusId",
                        column: x => x.TargetStatusId,
                        principalTable: "SystemStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatusConfigurations_CategoryId",
                table: "StatusConfigurations",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusConfigurations_IsActive",
                table: "StatusConfigurations",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_StatusConfigurations_ServiceTypeCategoryId",
                table: "StatusConfigurations",
                column: "ServiceTypeCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusConfigurations_StatusId_CategoryId_ServiceTypeCategor~",
                table: "StatusConfigurations",
                columns: new[] { "StatusId", "CategoryId", "ServiceTypeCategoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusMappings_IsActive",
                table: "StatusMappings",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_StatusMappings_SourceStatusId_TargetStatusId",
                table: "StatusMappings",
                columns: new[] { "SourceStatusId", "TargetStatusId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusMappings_TargetStatusId",
                table: "StatusMappings",
                column: "TargetStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemStatuses_IsActive",
                table: "SystemStatuses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_SystemStatuses_StatusType",
                table: "SystemStatuses",
                column: "StatusType");

            migrationBuilder.CreateIndex(
                name: "IX_SystemStatuses_StatusType_StatusValue",
                table: "SystemStatuses",
                columns: new[] { "StatusType", "StatusValue" },
                unique: true);

            // ===== INSERTAR DATOS INICIALES =====
            
            // 1. Insertar todos los estados de SearchHireStatus
            migrationBuilder.InsertData(
                table: "SystemStatuses",
                columns: new[] { "StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { "SearchHireStatus", "Pending", "pending", "Pendiente", "Contratación pendiente", true, 1, DateTime.UtcNow, DateTime.UtcNow },
                    { "SearchHireStatus", "AwaitingClientDecision", "awaiting_client_decision", "Esperando Decisión del Cliente", "Esperando decisión del cliente", true, 2, DateTime.UtcNow, DateTime.UtcNow },
                    { "SearchHireStatus", "Disputed", "disputed", "En Disputa", "En disputa", true, 3, DateTime.UtcNow, DateTime.UtcNow },
                    { "SearchHireStatus", "Completed", "completed", "Completado", "Servicio completado", true, 4, DateTime.UtcNow, DateTime.UtcNow },
                    { "SearchHireStatus", "Cancelled", "cancelled", "Cancelado", "Cancelado (genérico)", true, 5, DateTime.UtcNow, DateTime.UtcNow },
                    { "SearchHireStatus", "TransferFailed", "transfer_failed", "Transferencia Fallida", "Transferencia fallida", true, 6, DateTime.UtcNow, DateTime.UtcNow },
                    { "SearchHireStatus", "DisputeResolvedClient", "dispute_resolved_client", "Disputa Resuelta a Favor del Cliente", "Disputa resuelta a favor del cliente", true, 7, DateTime.UtcNow, DateTime.UtcNow },
                    { "SearchHireStatus", "DisputeResolvedExpert", "dispute_resolved_expert", "Disputa Resuelta a Favor del Experto", "Disputa resuelta a favor del experto", true, 8, DateTime.UtcNow, DateTime.UtcNow }
                });

            // 2. Insertar todos los estados de AppointmentStatus
            migrationBuilder.InsertData(
                table: "SystemStatuses",
                columns: new[] { "StatusType", "StatusName", "StatusValue", "DisplayName", "Description", "IsActive", "SortOrder", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { "AppointmentStatus", "AwaitingAppointment", "awaiting_appointment", "Esperando Cita", "Esperando propuesta del cliente (48h)", true, 1, DateTime.UtcNow, DateTime.UtcNow },
                    { "AppointmentStatus", "AppointmentProposed", "appointment_proposed", "Cita Propuesta", "Cliente propuso cita", true, 2, DateTime.UtcNow, DateTime.UtcNow },
                    { "AppointmentStatus", "AppointmentConfirmed", "appointment_confirmed", "Cita Confirmada", "Experto confirmó", true, 3, DateTime.UtcNow, DateTime.UtcNow },
                    { "AppointmentStatus", "AppointmentRejected", "appointment_rejected", "Cita Rechazada", "Experto rechazó", true, 4, DateTime.UtcNow, DateTime.UtcNow },
                    { "AppointmentStatus", "AppointmentCancelledByClient", "appointment_cancelled_by_client", "Cancelado por Cliente", "Primera cancelación del cliente", true, 5, DateTime.UtcNow, DateTime.UtcNow },
                    { "AppointmentStatus", "AppointmentCancelledByClientSecond", "appointment_cancelled_by_client_second", "Cancelado por Cliente (Segunda)", "Segunda cancelación del cliente", true, 6, DateTime.UtcNow, DateTime.UtcNow },
                    { "AppointmentStatus", "AppointmentCancelledByExpert", "appointment_cancelled_by_expert", "Cancelado por Experto", "Experto cancela voluntariamente", true, 7, DateTime.UtcNow, DateTime.UtcNow },
                    { "AppointmentStatus", "AppointmentCancelledByNoResponse", "appointment_cancelled_by_no_response", "Cancelado por Falta de Respuesta", "Cliente no propuso en tiempo", true, 8, DateTime.UtcNow, DateTime.UtcNow },
                    { "AppointmentStatus", "AppointmentCancelledByExpertRejection", "appointment_cancelled_by_expert_rejection", "Cancelado por Rechazo del Experto", "Experto rechazó 2 veces", true, 9, DateTime.UtcNow, DateTime.UtcNow },
                    { "AppointmentStatus", "AppointmentCompleted", "appointment_completed", "Cita Completada", "Cita realizada exitosamente", true, 10, DateTime.UtcNow, DateTime.UtcNow }
                });

            // 3. Insertar mapeos de AppointmentStatus → SearchHireStatus
            // Nota: Usamos subconsultas para obtener los IDs correctos
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
                
                UNION ALL
                
                SELECT 
                    s1.""Id"" as SourceStatusId,
                    s2.""Id"" as TargetStatusId,
                    true as IsActive,
                    CURRENT_TIMESTAMP as CreatedAt
                FROM ""SystemStatuses"" s1
                CROSS JOIN ""SystemStatuses"" s2
                WHERE s1.""StatusValue"" IN ('appointment_cancelled_by_client', 'appointment_cancelled_by_client_second', 
                                           'appointment_cancelled_by_expert', 'appointment_cancelled_by_no_response', 
                                           'appointment_cancelled_by_expert_rejection')
                AND s2.""StatusValue"" = 'cancelled'
            ");

            // 4. Migrar configuraciones existentes de CategoryServiceTypeConfigs
            // Primero insertamos las configuraciones para estados principales
            migrationBuilder.Sql(@"
                INSERT INTO ""StatusConfigurations"" (""StatusId"", ""CategoryId"", ""ServiceTypeCategoryId"", 
                                                   ""ClientPercentage"", ""ExpertPercentage"", ""PlatformPercentage"", 
                                                   ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 
                    ss.""Id"" as StatusId,
                    cstc.""CategoryId"",
                    cstc.""ServiceTypeCategoryId"",
                    cstc.""ClientPercentage"",
                    cstc.""ExpertPercentage"",
                    cstc.""PlatformPercentage"",
                    cstc.""IsActive"",
                    cstc.""CreatedAt"",
                    cstc.""UpdatedAt""
                FROM ""CategoryServiceTypeConfigs"" cstc
                JOIN ""SystemStatuses"" ss ON ss.""StatusValue"" = cstc.""Status""
                WHERE cstc.""Status"" IN ('completed', 'dispute-resolved-client', 'dispute-resolved-expert', 'cancelled')
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatusConfigurations");

            migrationBuilder.DropTable(
                name: "StatusMappings");

            migrationBuilder.DropTable(
                name: "SystemStatuses");
        }
    }
}

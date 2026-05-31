using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddIsFinalizationStatusToSystemStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFinalizationStatus",
                table: "SystemStatuses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill IsFinalizationStatus for terminal statuses that may already
            // exist (inserted by prior migrations or seeds). The column was just
            // added with default=false, so without this UPDATE existing rows would
            // silently report non-finalization, breaking money-distribution logic
            // (RefundService / ProcessMoneyDistributionAsync rely on this flag).
            // The seed script SEED_ESTADOS_COMPLETO.sql uses INSERT WHERE NOT EXISTS
            // and does NOT update existing rows, so it cannot self-heal this case.
            migrationBuilder.Sql(@"
                UPDATE ""SystemStatuses""
                SET ""IsFinalizationStatus"" = true, ""UpdatedAt"" = now()
                WHERE (""StatusType"" = 'SearchHireStatus' AND ""StatusValue"" IN (
                    'disputed',
                    'completed',
                    'cancelled',
                    'transfer_failed',
                    'dispute_resolved_client',
                    'dispute_resolved_expert',
                    'cancelled_by_client_no_proposal',
                    'cancelled_by_expert_no_response',
                    'cancelled_by_expert_no_report'
                ))
                OR (""StatusType"" = 'AppointmentStatus' AND ""StatusValue"" IN (
                    'appointment_cancelled_by_client_second',
                    'appointment_cancelled_by_no_response',
                    'appointment_cancelled_by_expert_rejection',
                    'appointment_completed',
                    'appointment_cancelled_by_client_no_proposal',
                    'appointment_cancelled_by_expert_no_response',
                    'appointment_cancelled_by_no_report',
                    'appointment_completed_without_client_approval',
                    'appointment_cancelled_by_expert_second',
                    'appointment_cancelled_by_client_account_delete',
                    'appointment_cancelled_by_expert_account_delete',
                    'appointment_completed_auto'
                ));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFinalizationStatus",
                table: "SystemStatuses");
        }
    }
}

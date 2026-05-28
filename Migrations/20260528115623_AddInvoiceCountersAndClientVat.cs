using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceCountersAndClientVat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Users",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "CaptureStatus",
                table: "SearchHires",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundFailedAt",
                table: "SearchHires",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresManualReview",
                table: "SearchHires",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "SearchHires",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<long>(
                name: "AmountCents",
                table: "FinancialTransactions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "FinancialTransactions",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "ExpertProfiles",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "StripeDisputeId",
                table: "Disputes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Appointments",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_RelatedEntityType_RelatedEntityId",
                table: "FinancialTransactions",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" },
                unique: true,
                filter: "\"TransactionType\" = 'ServicePayment'");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_StripeRefundId",
                table: "FinancialTransactions",
                column: "StripeRefundId",
                unique: true,
                filter: "\"StripeRefundId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_StripeTransferId_TransactionType",
                table: "FinancialTransactions",
                columns: new[] { "StripeTransferId", "TransactionType" },
                unique: true,
                filter: "\"StripeTransferId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FT_StripePaymentIntent_Chargeback_uq",
                table: "FinancialTransactions",
                column: "StripePaymentIntentId",
                unique: true,
                filter: "\"StripePaymentIntentId\" IS NOT NULL AND \"TransactionType\" = 'Chargeback'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FinancialTransactions_RelatedEntityType_RelatedEntityId",
                table: "FinancialTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FinancialTransactions_StripeRefundId",
                table: "FinancialTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FinancialTransactions_StripeTransferId_TransactionType",
                table: "FinancialTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FT_StripePaymentIntent_Chargeback_uq",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CaptureStatus",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "RefundFailedAt",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "RequiresManualReview",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "AmountCents",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "ExpertProfiles");

            migrationBuilder.DropColumn(
                name: "StripeDisputeId",
                table: "Disputes");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Appointments");
        }
    }
}

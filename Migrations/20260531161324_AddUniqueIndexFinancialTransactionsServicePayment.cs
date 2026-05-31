using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// 🛡️ Belt-and-suspenders defense against duplicate ledger entries for ServicePayment.
    ///
    /// El código en SubscriptionController/PaymentService ya comprueba si existe una
    /// FinancialTransaction previa con el mismo StripePaymentIntentId antes de insertar
    /// un ServicePayment, pero esa comprobación es susceptible a condiciones de carrera
    /// si dos webhooks de Stripe (o un retry) entran simultáneamente. Este índice único
    /// PARCIAL a nivel de Postgres convierte el segundo INSERT en un error
    /// (`23505 unique_violation`), garantizando que NUNCA pueda haber dos asientos
    /// contables de tipo ServicePayment para el mismo PaymentIntent.
    ///
    /// Complementa los otros índices parciales existentes:
    ///  - IX_FT_StripePaymentIntent_Chargeback_uq (un Chargeback por PaymentIntent)
    ///  - IX_FinancialTransactions_StripeRefundId (un Refund por PaymentIntent)
    ///  - IX_FinancialTransactions_StripeTransferId_TransactionType (Payout/Reversal)
    ///  - IX_FinancialTransactions_RelatedEntityType_RelatedEntityId (1 ServicePayment por contratación)
    ///
    /// Si el índice ya existe en la BD (aplicado manualmente vía Postgres MCP por el
    /// parent workflow), CREATE INDEX IF NOT EXISTS lo deja idempotente.
    /// </summary>
    public partial class AddUniqueIndexFinancialTransactionsServicePayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotente: si la herramienta MCP ya creó el índice, no falla.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_FT_StripePaymentIntent_ServicePayment_uq""
                ON ""FinancialTransactions"" (""StripePaymentIntentId"")
                WHERE ""StripePaymentIntentId"" IS NOT NULL
                  AND ""TransactionType"" = 'ServicePayment';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FT_StripePaymentIntent_ServicePayment_uq",
                table: "FinancialTransactions");
        }
    }
}

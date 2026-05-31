using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <summary>
    /// Round 24 — Schema fix: la tabla "ExchangeRateSnapshots" existía con (Id, FetchedAt,
    /// BaseCurrency, RatesJson) pero el modelo (Round 21) añadió Source + RateDate sin
    /// migración. Resultado: ExchangeRateService.PersistSnapshotAsync fallaba silenciosamente
    /// (catch genérico log warning) → el job Hangfire de refresh no podía persistir audit trail
    /// del proveedor que sirvió cada snapshot (fawazahmed0 / -cdn / frankfurter / static).
    ///
    /// Migración idempotente (IF NOT EXISTS) por si las columnas se aplicaron manualmente vía
    /// MCP de Postgres en un entorno anterior. Defaults pragmáticos para retro-compat:
    ///  - Source defaults a 'fawazahmed0' (el proveedor primario).
    ///  - RateDate defaults a CURRENT_DATE (snapshot del día de la migración).
    /// </summary>
    public partial class AddSourceAndRateDateToExchangeRateSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""ExchangeRateSnapshots""
                ADD COLUMN IF NOT EXISTS ""Source"" varchar(64) NOT NULL DEFAULT 'fawazahmed0';

                ALTER TABLE ""ExchangeRateSnapshots""
                ADD COLUMN IF NOT EXISTS ""RateDate"" date NOT NULL DEFAULT CURRENT_DATE;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""ExchangeRateSnapshots"" DROP COLUMN IF EXISTS ""RateDate"";
                ALTER TABLE ""ExchangeRateSnapshots"" DROP COLUMN IF EXISTS ""Source"";
            ");
        }
    }
}

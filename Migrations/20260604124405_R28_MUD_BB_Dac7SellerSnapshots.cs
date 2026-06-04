using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class R28_MUD_BB_Dac7SellerSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FiscalCountry",
                table: "Users",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FiscalCountryChangedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredCurrency",
                table: "Users",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PrivacyAcceptedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrivacyVersion",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxId",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxIdCountry",
                table: "Users",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TermsAcceptedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "SearchServices",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "EUR");

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "SearchHires",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "EUR");

            migrationBuilder.AddColumn<string>(
                name: "ReceivedInCountry",
                table: "Reviews",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "FinancialTransactions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "EUR");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPayoutDate",
                table: "ExpertProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RelocatedAt",
                table: "ExpertProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelocatedFromCountry",
                table: "ExpertProfiles",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Timezone",
                table: "ExpertAvailabilities",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposerTimezone",
                table: "Appointments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Dac7SellerSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExpertProfileId = table.Column<int>(type: "integer", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    LegalFirstName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LegalLastName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    TinCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    TinValue = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FiscalCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    VatNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AddressLine = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AddressCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AddressPostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IbanLast4 = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    GrossSalesQ1 = table.Column<decimal>(type: "numeric", nullable: false),
                    GrossSalesQ2 = table.Column<decimal>(type: "numeric", nullable: false),
                    GrossSalesQ3 = table.Column<decimal>(type: "numeric", nullable: false),
                    GrossSalesQ4 = table.Column<decimal>(type: "numeric", nullable: false),
                    PlatformFeesQ1 = table.Column<decimal>(type: "numeric", nullable: false),
                    PlatformFeesQ2 = table.Column<decimal>(type: "numeric", nullable: false),
                    PlatformFeesQ3 = table.Column<decimal>(type: "numeric", nullable: false),
                    PlatformFeesQ4 = table.Column<decimal>(type: "numeric", nullable: false),
                    TransactionCountQ1 = table.Column<int>(type: "integer", nullable: false),
                    TransactionCountQ2 = table.Column<int>(type: "integer", nullable: false),
                    TransactionCountQ3 = table.Column<int>(type: "integer", nullable: false),
                    TransactionCountQ4 = table.Column<int>(type: "integer", nullable: false),
                    ReportingCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SnapshotCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReportedToAeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AeatSubmissionReference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dac7SellerSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dac7SellerSnapshots_ExpertProfiles_ExpertProfileId",
                        column: x => x.ExpertProfileId,
                        principalTable: "ExpertProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeRateSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaseCurrency = table.Column<string>(type: "char(3)", maxLength: 3, nullable: false),
                    RatesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dac7SellerSnapshot_Expert_Year_uq",
                table: "Dac7SellerSnapshots",
                columns: new[] { "ExpertProfileId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateSnapshots_Base_Fetched",
                table: "ExchangeRateSnapshots",
                columns: new[] { "BaseCurrency", "FetchedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dac7SellerSnapshots");

            migrationBuilder.DropTable(
                name: "ExchangeRateSnapshots");

            migrationBuilder.DropColumn(
                name: "FiscalCountry",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FiscalCountryChangedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PreferredCurrency",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PrivacyAcceptedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PrivacyVersion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TaxId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TaxIdCountry",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TermsVersion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "SearchServices");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "SearchHires");

            migrationBuilder.DropColumn(
                name: "ReceivedInCountry",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "LastPayoutDate",
                table: "ExpertProfiles");

            migrationBuilder.DropColumn(
                name: "RelocatedAt",
                table: "ExpertProfiles");

            migrationBuilder.DropColumn(
                name: "RelocatedFromCountry",
                table: "ExpertProfiles");

            migrationBuilder.DropColumn(
                name: "Timezone",
                table: "ExpertAvailabilities");

            migrationBuilder.DropColumn(
                name: "ProposerTimezone",
                table: "Appointments");
        }
    }
}

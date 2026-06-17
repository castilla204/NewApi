using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddExpertAvailabilityException : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""ExpertAvailabilityExceptions"" (
                    ""Id"" serial PRIMARY KEY,
                    ""ExpertId"" integer NOT NULL,
                    ""Date"" date NOT NULL,
                    ""IsWorking"" boolean NOT NULL,
                    ""StartLocal"" interval NULL,
                    ""EndLocal"" interval NULL,
                    ""Timezone"" varchar(64) NULL,
                    ""CreatedAt"" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    ""UpdatedAt"" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    CONSTRAINT ""FK_ExpertAvailabilityExceptions_ExpertProfiles_ExpertId""
                        FOREIGN KEY (""ExpertId"") REFERENCES ""ExpertProfiles"" (""Id"") ON DELETE CASCADE
                );
            ");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ExpertAvailabilityExceptions_ExpertId_Date"" ON ""ExpertAvailabilityExceptions"" (""ExpertId"", ""Date"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""ExpertAvailabilityExceptions"";");
        }
    }
}

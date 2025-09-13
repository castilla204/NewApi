using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class stripedetailstatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StripeStatusDetails",
                table: "ExpertProfiles",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StripeStatusDetails",
                table: "ExpertProfiles");
        }
    }
}

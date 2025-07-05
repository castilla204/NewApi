using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace newApi.Migrations
{
    public partial class fixingerror : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add AIId column to SearchServices
            migrationBuilder.AddColumn<int>(
                name: "AIId",
                table: "SearchServices",
                type: "integer",
                nullable: true);

            // Add foreign key for AIId
            migrationBuilder.AddForeignKey(
                name: "FK_SearchServices_AIs_AIId",
                table: "SearchServices",
                column: "AIId",
                principalTable: "AIs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Add index for AIId
            migrationBuilder.CreateIndex(
                name: "IX_SearchServices_AIId",
                table: "SearchServices",
                column: "AIId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove AIId column and related constraints/index
            migrationBuilder.DropForeignKey(
                name: "FK_SearchServices_AIs_AIId",
                table: "SearchServices");

            migrationBuilder.DropIndex(
                name: "IX_SearchServices_AIId",
                table: "SearchServices");

            migrationBuilder.DropColumn(
                name: "AIId",
                table: "SearchServices");
        }
    }
}
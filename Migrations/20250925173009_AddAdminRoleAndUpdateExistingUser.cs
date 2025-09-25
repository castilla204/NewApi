using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace newApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminRoleAndUpdateExistingUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 🔐 SEGURIDAD: Actualizar usuario existente con email dcastillaa@gmail.com a rol Admin
            migrationBuilder.Sql(@"
                UPDATE ""Users"" 
                SET ""Role"" = 2 
                WHERE ""Email"" = 'dcastillaa@gmail.com';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revertir usuario admin a rol Client
            migrationBuilder.Sql(@"
                UPDATE ""Users"" 
                SET ""Role"" = 0 
                WHERE ""Email"" = 'dcastillaa@gmail.com';
            ");
        }
    }
}

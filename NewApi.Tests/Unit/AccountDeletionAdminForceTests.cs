using FluentAssertions;
using newApi.DataLayer.Models.DTOs;
using Xunit;

namespace NewApi.Tests.Unit
{
    /// <summary>
    /// 🛡️ B3 — anti-abuso: el bypass de guards (adminForce) DEBE ser un parámetro de método que
    /// solo el endpoint admin puede setear, NUNCA un campo del DTO que un usuario normal pueda enviar
    /// en el body de POST /AccountDeletion/delete. Este test falla si alguien añade tal campo al DTO.
    /// </summary>
    public class AccountDeletionAdminForceTests
    {
        [Theory]
        [InlineData("AdminForce")]
        [InlineData("Force")]
        [InlineData("Bypass")]
        [InlineData("SkipGuards")]
        [InlineData("AdminUserId")]
        public void RequestDto_must_not_expose_force_or_admin_fields(string forbiddenProperty)
        {
            typeof(AccountDeletionRequestDto)
                .GetProperty(forbiddenProperty)
                .Should().BeNull(
                    $"un usuario NO debe poder forzar el borrado vía el body; '{forbiddenProperty}' debe vivir solo como parámetro de método del endpoint admin");
        }
    }
}

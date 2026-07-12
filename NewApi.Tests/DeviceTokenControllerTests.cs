using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using newApi.Controllers;
using Xunit;

namespace NewApi.Tests
{
    public class DeviceTokenControllerTests
    {
        private static DeviceTokenController BuildController(bool withUser)
        {
            // AppDbContext no se usa en las ramas testeadas (claim inválido / token vacío),
            // que retornan antes de tocarlo, así que pasamos null deliberadamente.
            var controller = new DeviceTokenController(null!);
            var httpContext = new DefaultHttpContext();
            if (withUser)
            {
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "42") }, "test"));
            }
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        [Fact]
        public async Task Register_sin_claim_de_usuario_devuelve_401()
        {
            var controller = BuildController(withUser: false);
            var result = await controller.Register(new DeviceTokenController.RegisterDeviceTokenDto("tok", "android"));
            result.Should().BeOfType<UnauthorizedResult>();
        }

        [Fact]
        public async Task Register_con_token_vacio_devuelve_400()
        {
            var controller = BuildController(withUser: true);
            var result = await controller.Register(new DeviceTokenController.RegisterDeviceTokenDto("  ", "android"));
            result.Should().BeOfType<BadRequestObjectResult>();
        }
    }
}

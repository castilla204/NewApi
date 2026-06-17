using FluentAssertions;
using newApi.Services;
using Xunit;

namespace NewApi.Tests.Unit
{
    public class ContactInfoFilterTests
    {
        [Theory]
        [InlineData("llámame al 666 777 888")]
        [InlineData("mi numero es 666777888")]
        [InlineData("escríbeme a +34 600 11 22 33")]
        [InlineData("seis seis seis siete siete siete ocho ocho ocho")]
        public void Detect_flags_phone_numbers(string content)
        {
            var result = ContactInfoFilter.Detect(content);
            result.HasViolation.Should().BeTrue();
            result.Types.Should().Contain(ContactType.Phone);
        }

        [Theory]
        [InlineData("mi correo es juan@gmail.com")]
        [InlineData("escríbeme a juan arroba gmail punto com")]
        public void Detect_flags_emails(string content)
        {
            var result = ContactInfoFilter.Detect(content);
            result.HasViolation.Should().BeTrue();
            result.Types.Should().Contain(ContactType.Email);
        }

        [Theory]
        [InlineData("mira https://misitio.com")]
        [InlineData("entra en www.misitio.com")]
        [InlineData("mi web es misitio.es")]
        public void Detect_flags_urls(string content)
        {
            var result = ContactInfoFilter.Detect(content);
            result.HasViolation.Should().BeTrue();
            result.Types.Should().Contain(ContactType.Url);
        }

        [Theory]
        [InlineData("hablamos por whatsapp")]
        [InlineData("te paso mi wasap")]
        [InlineData("sígueme en instagram @juanperez")]
        [InlineData("mejor por telegram")]
        public void Detect_flags_social_or_apps(string content)
        {
            var result = ContactInfoFilter.Detect(content);
            result.HasViolation.Should().BeTrue();
            result.Types.Should().Contain(ContactType.SocialOrApp);
        }

        [Theory]
        [InlineData("el servicio cuesta 150 euros")]
        [InlineData("reviso el modelo iPhone 14")]
        [InlineData("quedamos el 12/12/2024 a las 18:30")]
        [InlineData("tengo 25 años de experiencia")]
        [InlineData("incluye 3 o 4 visitas a tu domicilio")]
        [InlineData("")]
        [InlineData("quedamos el 12.12.2024 18:30")]
        [InlineData("compré 2 arrobas de queso")]
        public void Detect_does_not_flag_normal_text(string content)
        {
            var result = ContactInfoFilter.Detect(content);
            result.HasViolation.Should().BeFalse();
        }

        [Fact]
        public void BuildBlockMessage_returns_spanish_message()
        {
            var msg = ContactInfoFilter.BuildBlockMessage(new[] { ContactType.Phone });
            msg.Should().Contain("contratar");
            msg.Should().NotBeNullOrWhiteSpace();
        }
    }
}

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
        [InlineData("compré 2 arrobas de queso")]
        public void Detect_does_not_flag_normal_text(string content)
        {
            var result = ContactInfoFilter.Detect(content);
            result.HasViolation.Should().BeFalse();
        }

        // SOBRE-BLOQUEO CONSERVADOR CONOCIDO (documentado, no regresión de W26): desde que el fix
        // CHAT-FILTER añadió `.` como separador de "digit run" (para cazar "600.123.456"), una fecha+hora
        // escrita junta con puntos ("12.12.2024 18:30" → "12.12.2024 18" = 10 dígitos, el `:` corta) se
        // marca como teléfono SOLO en precontratación. Se prefiere bloquear de más (protege comisión) a
        // debilitar la detección de teléfonos. La misma fecha con palabras que cortan la tirada
        // ("12/12/2024 a las 18:30" = 8 díg.) NO se marca (test de arriba).
        [Fact]
        public void Detect_conservatively_flags_dotted_date_time_in_prehire()
        {
            var result = ContactInfoFilter.Detect("quedamos el 12.12.2024 18:30");
            result.HasViolation.Should().BeTrue();
            result.Types.Should().Contain(ContactType.Phone);
        }

        [Fact]
        public void BuildBlockMessage_returns_spanish_message()
        {
            var msg = ContactInfoFilter.BuildBlockMessage(new[] { ContactType.Phone });
            msg.Should().Contain("contratar");
            msg.Should().NotBeNullOrWhiteSpace();
        }

        // ── W26: bypass con caracteres invisibles ──────────────────────────────────────────────
        // Un teléfono con un separador invisible entre cada dígito rompía la "digit run" y evadía
        // el filtro. Se construyen con (char)0xXXXX para no meter invisibles en el fuente del test.
        [Theory]
        [InlineData(0x200B)] // ZERO WIDTH SPACE
        [InlineData(0x200C)] // ZERO WIDTH NON-JOINER
        [InlineData(0x200D)] // ZERO WIDTH JOINER
        [InlineData(0xFEFF)] // ZERO WIDTH NO-BREAK SPACE / BOM
        [InlineData(0x00AD)] // SOFT HYPHEN
        [InlineData(0x2060)] // WORD JOINER
        public void Detect_flags_phone_hidden_with_zero_width_chars(int codePoint)
        {
            var sep = ((char)codePoint).ToString();
            var digits = "600123456";
            var obfuscated = string.Join(sep, digits.ToCharArray());
            var content = "mi numero es " + obfuscated;

            var result = ContactInfoFilter.Detect(content);
            result.HasViolation.Should().BeTrue();
            result.Types.Should().Contain(ContactType.Phone);
        }

        [Fact]
        public void Detect_flags_email_hidden_with_zero_width_chars()
        {
            var zwsp = ((char)0x200B).ToString();
            var content = "escribeme a juan" + zwsp + "@" + zwsp + "gmail.com";

            var result = ContactInfoFilter.Detect(content);
            result.HasViolation.Should().BeTrue();
            result.Types.Should().Contain(ContactType.Email);
        }

        // ── W26: ofuscación de email deletreada en inglés ──────────────────────────────────────
        [Theory]
        [InlineData("escribeme a juan at gmail dot com")]
        [InlineData("mi correo juan (at) gmail (dot) com")]
        [InlineData("juan [at] gmail [dot] com")]
        public void Detect_flags_english_obfuscated_email(string content)
        {
            var result = ContactInfoFilter.Detect(content);
            result.HasViolation.Should().BeTrue();
            result.Types.Should().Contain(ContactType.Email);
        }

        // Prosa normal en inglés/español no debe marcarse como email por contener "at".
        [Theory]
        [InlineData("nos vemos at the shop tomorrow")]
        [InlineData("quedamos at 5 en la puerta")]
        public void Detect_does_not_flag_prose_with_at(string content)
        {
            var result = ContactInfoFilter.Detect(content);
            result.Types.Should().NotContain(ContactType.Email);
        }
    }
}

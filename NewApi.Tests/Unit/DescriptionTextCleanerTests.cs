using FluentAssertions;
using newApi.Services;
using Xunit;

namespace NewApi.Tests.Unit
{
    public class DescriptionTextCleanerTests
    {
        [Fact]
        public void Clean_removes_emojis()
        {
            var result = DescriptionTextCleaner.Clean("Soy mecánico 🚗✨ con experiencia 👍", 1000);
            result.Should().NotContain("🚗");
            result.Should().NotContain("✨");
            result.Should().NotContain("👍");
            result.Should().Contain("mecánico");
        }

        [Fact]
        public void Clean_removes_markdown_and_bullets()
        {
            var input = "## Sobre mí\n- **Reviso** coches\n* Hago informes";
            var result = DescriptionTextCleaner.Clean(input, 1000);
            result.Should().NotContain("#");
            result.Should().NotContain("**");
            result.Should().NotContain("- ");
            result.Should().NotContain("* ");
            result.Should().Contain("Reviso");
        }

        [Fact]
        public void Clean_truncates_to_max_length_without_cutting_word()
        {
            var input = "palabra ".PadRight(200, 'x');
            var result = DescriptionTextCleaner.Clean(input, 60);
            result.Length.Should().BeLessThanOrEqualTo(60);
        }

        [Fact]
        public void Clean_handles_null_or_empty()
        {
            DescriptionTextCleaner.Clean(null, 60).Should().BeEmpty();
            DescriptionTextCleaner.Clean("   ", 60).Should().BeEmpty();
        }
    }
}

using Stripe;

namespace newApi.Services.StripeBranding;

/// <summary>
/// Única fuente de verdad del branding de Inspecciono para las páginas hospedadas de Stripe.
/// Tokens en sync con ReactWeb/src/index.css (--brand: 210 100% 40% = #0066CC) y el acento ámbar.
/// </summary>
public static class InspeccionoBranding
{
    public const string PrimaryColor = "#0066CC";   // azul de marca
    public const string SecondaryColor = "#F59E0B"; // acento ámbar
    public const string DisplayName = "Inspecciono";

    /// <summary>Branding para la cuenta Connect (onboarding + Express Dashboard).</summary>
    public static AccountSettingsBrandingOptions BuildAccountBranding(string? iconFileId, string? logoFileId)
    {
        return new AccountSettingsBrandingOptions
        {
            Icon = string.IsNullOrWhiteSpace(iconFileId) ? null : iconFileId,
            Logo = string.IsNullOrWhiteSpace(logoFileId) ? null : logoFileId,
            PrimaryColor = PrimaryColor,
            SecondaryColor = SecondaryColor
        };
    }
}

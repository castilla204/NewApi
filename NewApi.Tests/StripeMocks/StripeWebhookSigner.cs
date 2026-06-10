using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace NewApi.Tests.StripeMocks;

/// <summary>
/// Firma payloads JSON con el formato HMAC-SHA256 que usa Stripe en
/// <c>Stripe-Signature: t=&lt;ts&gt;,v1=&lt;hmac&gt;</c>.
///
/// El backend valida la firma con <c>EventUtility.ConstructEvent(json, header, secret)</c>;
/// este helper construye un header válido para cualquier secret de test.
/// </summary>
public static class StripeWebhookSigner
{
    /// <summary>
    /// Genera el valor del header <c>Stripe-Signature</c> firmando
    /// <c>"{timestamp}.{payload}"</c> con HMAC-SHA256(secret).
    /// </summary>
    public static string SignHeader(string jsonPayload, string webhookSecret, DateTimeOffset? when = null)
    {
        var timestamp = (when ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signedPayload = $"{timestamp}.{jsonPayload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        var v1 = Convert.ToHexString(hash).ToLowerInvariant();

        return $"t={timestamp},v1={v1}";
    }

    /// <summary>
    /// Construye un <see cref="HttpRequestMessage"/> POST listo para enviar
    /// al endpoint webhook del backend. Útil para tests sobre HttpClient
    /// del WebApplicationFactory.
    /// </summary>
    public static HttpRequestMessage BuildSignedPost(
        string url,
        string jsonPayload,
        string webhookSecret,
        DateTimeOffset? when = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Stripe-Signature", SignHeader(jsonPayload, webhookSecret, when));
        return req;
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NewApi.Tests.StripeMocks;

/// <summary>
/// Stub HTTP mínimo de la API de Stripe (patrón stripe-mock) para que el backend REAL
/// pueda ejecutar sus llamadas salientes (capturas, transfers, refunds) durante los
/// tests de WebApplicationFactory sin tocar Stripe de verdad.
///
/// Se activa apuntando <c>StripeConfiguration.ApiBase</c> (estático de Stripe.net, el
/// backend nunca lo toca — verificado en Program.cs:1623-1726, solo setea ApiKey) a
/// <see cref="BaseUrl"/>. Todas las llamadas <c>new PaymentIntentService()</c> etc.
/// del código de producción usan el cliente global → van a este stub.
///
/// Rutas cubiertas (las que usan los flujos checkout/money-distribution auditados):
///   GET  /v1/checkout/sessions/{id}        → sesión mínima (tax 0, sin customer_details)
///   GET  /v1/payment_intents/{id}          → estado por-PI (default requires_capture)
///   POST /v1/payment_intents/{id}/capture  → marca succeeded (stateful)
///   POST /v1/payment_intents/{id}/cancel   → marca canceled
///   GET  /v1/balance                       → saldo EUR holgado
///   GET  /v1/accounts/{id}                 → cuenta con payouts habilitados
///   POST /v1/transfers                     → tr_fake_*
///   POST /v1/refunds                       → re_fake_*
///   POST /v1/transfers/{id}/reversals      → trr_fake_*
///   resto                                  → 404 con error Stripe estándar
/// </summary>
public sealed class FakeStripeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Estado por PaymentIntent: requires_capture → succeeded/canceled.</summary>
    private readonly ConcurrentDictionary<string, string> _piStatus = new();

    /// <summary>Registro de requests recibidas (método + ruta) para aserciones.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>
    /// Fija el estado de un PaymentIntent directamente. Permite a los tests reproducir el
    /// HUECO 1b (captura diferida del vendedor): un PI YA capturado en Stripe (succeeded)
    /// pero cuya cita no se persistió, sin tener que ejecutar el flujo de capture completo.
    /// </summary>
    public void SetPaymentIntentStatus(string paymentIntentId, string status)
        => _piStatus[paymentIntentId] = status;

    public string BaseUrl { get; private set; } = null!;

    /// <summary>Céntimos que devuelven los PI del stub (los tests usan 110 EUR).</summary>
    public long DefaultAmountCents { get; set; } = 11000;

    /// <summary>
    /// Inyección de fallos: los próximos N POST /v1/refunds devuelven 500 (api_error).
    /// Permite reproducir el escenario TX-6 (transfer OK + refund falla → retry).
    /// </summary>
    public int RefundFailuresRemaining;

    /// <summary>
    /// Inyección de fallos: los próximos N POST /v1/transfers devuelven 500.
    /// Reproduce el escenario TX-8 (finalización de usuario + dinero fallido → retry).
    /// </summary>
    public int TransferFailuresRemaining;

    /// <summary>
    /// Inyección de fallos por-PI: un POST /v1/payment_intents/{id}/capture sobre un id
    /// presente en este set devuelve 402 (card_error). Permite reproducir el escenario
    /// "captura falla al confirmar el vendedor" sin afectar a otros PIs en paralelo.
    /// </summary>
    public ConcurrentDictionary<string, byte> CaptureFailurePaymentIntents { get; } = new();

    public void Start()
    {
        // Puerto libre: probar hasta enganchar uno (HttpListener sobre localhost no
        // necesita ACL de admin en Windows).
        var rnd = new Random();
        for (var attempt = 0; ; attempt++)
        {
            var port = rnd.Next(20000, 59000);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                BaseUrl = $"http://localhost:{port}";
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                // puerto ocupado → reintentar
            }
        }

        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        });
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var method = ctx.Request.HttpMethod;
        var path = ctx.Request.Url!.AbsolutePath;
        Requests.Enqueue($"{method} {path}");

        try
        {
            var (status, body) = Route(method, path);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        catch
        {
            // Stub: nunca dejar la conexión colgada.
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* ignorar */ }
        }
    }

    private (int Status, string Body) Route(string method, string path)
    {
        Match m;

        if (method == "GET" && (m = Regex.Match(path, "^/v1/checkout/sessions/([^/]+)$")).Success)
        {
            var id = m.Groups[1].Value;
            return (200, $$$"""
                {"id":"{{{id}}}","object":"checkout.session","mode":"payment","status":"complete",
                 "payment_status":"paid","amount_total":{{{DefaultAmountCents}}},"currency":"eur",
                 "total_details":{"amount_tax":0,"amount_discount":0,"amount_shipping":0},
                 "customer_details":null,"metadata":{}}
                """);
        }

        if (method == "GET" && (m = Regex.Match(path, "^/v1/payment_intents/([^/]+)$")).Success)
        {
            var id = m.Groups[1].Value;
            var st = _piStatus.GetOrAdd(id, "requires_capture");
            return (200, PiJson(id, st));
        }

        if (method == "POST" && (m = Regex.Match(path, "^/v1/payment_intents/([^/]+)/capture$")).Success)
        {
            var id = m.Groups[1].Value;
            // Inyección de fallo por-PI: la captura de este PI falla (card_error → 402).
            if (CaptureFailurePaymentIntents.ContainsKey(id))
            {
                return (402, """
                    {"error":{"type":"card_error","code":"card_declined","message":"FakeStripeServer: fallo inyectado en capture (seller confirm)"}}
                    """);
            }
            _piStatus[id] = "succeeded";
            return (200, PiJson(id, "succeeded"));
        }

        if (method == "POST" && (m = Regex.Match(path, "^/v1/payment_intents/([^/]+)/cancel$")).Success)
        {
            var id = m.Groups[1].Value;
            _piStatus[id] = "canceled";
            return (200, PiJson(id, "canceled"));
        }

        if (method == "GET" && path == "/v1/balance")
        {
            return (200, """
                {"object":"balance",
                 "available":[{"amount":100000000,"currency":"eur","source_types":{"card":100000000}}],
                 "pending":[{"amount":0,"currency":"eur"}]}
                """);
        }

        if (method == "GET" && (m = Regex.Match(path, "^/v1/accounts/([^/]+)$")).Success)
        {
            var id = m.Groups[1].Value;
            return (200, $$$"""
                {"id":"{{{id}}}","object":"account","type":"express",
                 "charges_enabled":true,"payouts_enabled":true,"details_submitted":true,
                 "capabilities":{"transfers":"active"}}
                """);
        }

        if (method == "POST" && path == "/v1/transfers")
        {
            if (Interlocked.CompareExchange(ref TransferFailuresRemaining, 0, 0) > 0)
            {
                Interlocked.Decrement(ref TransferFailuresRemaining);
                return (500, """
                    {"error":{"type":"api_error","message":"FakeStripeServer: fallo inyectado en transfer (test TX-8)"}}
                    """);
            }
            var id = "tr_fake_" + Guid.NewGuid().ToString("N")[..16];
            return (200, $$$"""
                {"id":"{{{id}}}","object":"transfer","amount":{{{DefaultAmountCents}}},"currency":"eur",
                 "destination":"acct_fake","reversed":false,"amount_reversed":0}
                """);
        }

        if (method == "GET" && (m = Regex.Match(path, "^/v1/transfers/([^/]+)$")).Success)
        {
            // El RefundService verifica el transfer recién creado (GET tras el POST).
            var id = m.Groups[1].Value;
            return (200, $$$"""
                {"id":"{{{id}}}","object":"transfer","amount":{{{DefaultAmountCents}}},"currency":"eur",
                 "destination":"acct_fake","reversed":false,"amount_reversed":0}
                """);
        }

        if (method == "GET" && (m = Regex.Match(path, "^/v1/refunds/([^/]+)$")).Success)
        {
            var id = m.Groups[1].Value;
            return (200, $$$"""{"id":"{{{id}}}","object":"refund","status":"succeeded","amount":{{{DefaultAmountCents}}},"currency":"eur"}""");
        }

        if (method == "POST" && (m = Regex.Match(path, "^/v1/transfers/([^/]+)/reversals$")).Success)
        {
            var id = "trr_fake_" + Guid.NewGuid().ToString("N")[..16];
            return (200, $$$"""{"id":"{{{id}}}","object":"transfer_reversal","amount":{{{DefaultAmountCents}}},"currency":"eur"}""");
        }

        if (method == "POST" && path == "/v1/refunds")
        {
            // Inyección de fallos (escenario TX-6): N próximos refunds devuelven 500.
            if (Interlocked.CompareExchange(ref RefundFailuresRemaining, 0, 0) > 0)
            {
                Interlocked.Decrement(ref RefundFailuresRemaining);
                return (500, """
                    {"error":{"type":"api_error","message":"FakeStripeServer: fallo inyectado en refund (test TX-6)"}}
                    """);
            }
            var id = "re_fake_" + Guid.NewGuid().ToString("N")[..16];
            return (200, $$$"""
                {"id":"{{{id}}}","object":"refund","status":"succeeded",
                 "amount":{{{DefaultAmountCents}}},"currency":"eur"}
                """);
        }

        return (404, """
            {"error":{"type":"invalid_request_error","message":"FakeStripeServer: ruta no implementada"}}
            """);
    }

    private static string PiJson(string id, string status) => $$$"""
        {"id":"{{{id}}}","object":"payment_intent","status":"{{{status}}}",
         "amount":11000,"currency":"eur","latest_charge":"ch_fake_{{{id}}}",
         "capture_method":"manual"}
        """;

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignorar */ }
        try { _listener.Close(); } catch { /* ignorar */ }
    }
}

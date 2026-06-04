using System;
using System.Threading.Tasks;

namespace newApi.Common
{
    /// <summary>
    /// 🛡️ Round 28 MUD-BA: helper para fire-and-forget seguro.
    ///
    /// El patrón `_ = Task.Run(async () => { ... })` extendido por el código de
    /// auth/logging pierde excepciones silenciosamente: si la lambda falla
    /// (Db down, scope ya disposed, OOM kill durante rolling restart), la
    /// excepción muere en el finalizer del Task. El log que motivó el Task.Run
    /// nunca se emite y nadie se entera.
    ///
    /// Este helper añade `.ContinueWith(..., OnlyOnFaulted)` que vuelca el
    /// stack trace a Console.Error (capturado por Render/Docker stderr) cuando
    /// la tarea termina con excepción. La pérdida del log original sigue
    /// ocurriendo (es fire-and-forget por diseño — usar el helper en hot path
    /// donde latencia importa más que durabilidad), pero al menos sabemos
    /// que un log se perdió y por qué.
    ///
    /// Uso típico:
    ///   BackgroundLogger.FireAndForget(async () => {
    ///       using var scope = _serviceScopeFactory.CreateScope();
    ///       var logSvc = scope.ServiceProvider.GetRequiredService&lt;ILoggingService&gt;();
    ///       await logSvc.LogInfoAsync(...);
    ///   });
    /// </summary>
    public static class BackgroundLogger
    {
        public static void FireAndForget(Func<Task> action, string? callerHint = null)
        {
            _ = Task.Run(action).ContinueWith(t =>
            {
                try
                {
                    var stamp = $"[BG-LOG-FAIL] {DateTime.UtcNow:O}";
                    var hint = string.IsNullOrEmpty(callerHint) ? "" : $" caller={callerHint}";
                    Console.Error.WriteLine($"{stamp}{hint} fire-and-forget logging task faulted: {t.Exception?.GetBaseException()}");
                }
                catch
                {
                    // Si hasta el Console.Error falla, nada más podemos hacer — el proceso
                    // está en estado tan degradado que el rolling restart va a recogerlo.
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}

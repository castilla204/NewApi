using Hangfire;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    /// <summary>
    /// Filtro de Hangfire que detecta cuando un job falla definitivamente (después de agotar todos los reintentos)
    /// y crea un log crítico para alertar a soporte.
    /// </summary>
    public class HangfireFailedJobNotificationFilter : IElectStateFilter
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public HangfireFailedJobNotificationFilter(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public void OnStateElection(ElectStateContext context)
        {
            // Solo procesar cuando el estado candidato es "Failed" (fallo definitivo)
            if (context.CandidateState is FailedState failedState)
            {
                var job = context.BackgroundJob;
                var exception = failedState.Exception;
                
                // Obtener información del job
                var jobMethod = job.Job.Method.Name;
                var jobType = job.Job.Type.Name;
                var jobId = job.Id;
                var jobArgs = string.Join(", ", job.Job.Args.Select(a => a?.ToString() ?? "null"));

                // ⚠️ ANTI-BUCLE: si el job que falló es el PROPIO envío de email de alertas, NO crear otro
                // log crítico (que volvería a encolar emails). Si no, un SMTP caído genera un bucle
                // amplificador: filtro → LogCritical → SendEmailBackgroundJob → falla → filtro → ...
                if (jobMethod == "SendEmailBackgroundJob" || jobType == "LoggingService")
                {
                    Console.Error.WriteLine($"[HANGFIRE FILTER] Alert-email job {jobId} ({jobMethod}) failed definitively; skipping critical-log to avoid an email storm. Exception: {exception?.Message}");

                    // 🛡️ E2: aunque saltamos el LogCriticalAsync (que reenviaría email y crearía bucle),
                    // SÍ enrutamos al canal alternativo (Slack/Telegram). AlertChannelService usa HTTP,
                    // no SMTP, así que no realimenta el bucle aunque el SMTP esté caído. Sin esto,
                    // un fallo de email crítico (eliminación de cuenta, refund, dispute) desaparecía
                    // silenciosamente del radar de ops si SMTP llevaba >16min caído.
                    try
                    {
                        var capturedJobId = jobId;
                        var capturedJobMethod = jobMethod;
                        var capturedJobType = jobType;
                        var capturedExceptionType = exception?.GetType().Name;
                        var capturedExceptionMessage = exception?.Message;
                        var capturedRecipient = job.Job.Args.Count > 0 ? job.Job.Args[0]?.ToString() : null;
                        var capturedSubject = job.Job.Args.Count > 1 ? job.Job.Args[1]?.ToString() : null;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var alertScope = _serviceScopeFactory.CreateScope();
                                var alertChannel = alertScope.ServiceProvider.GetService<IAlertChannelService>();
                                if (alertChannel != null && (alertChannel.GetStatus().SlackConfigured || alertChannel.GetStatus().TelegramConfigured))
                                {
                                    await alertChannel.SendCriticalAsync(
                                        $"[CRITICAL] Email delivery job exhausted retries (SMTP down?)",
                                        new
                                        {
                                            event_type = "email_job_permanently_failed",
                                            HangfireJobId = capturedJobId,
                                            JobType = capturedJobType,
                                            JobMethod = capturedJobMethod,
                                            Recipient = capturedRecipient,
                                            Subject = capturedSubject,
                                            ExceptionType = capturedExceptionType,
                                            ExceptionMessage = capturedExceptionMessage,
                                            FailedAt = DateTime.UtcNow
                                        });
                                }
                            }
                            catch (Exception alertEx)
                            {
                                Console.Error.WriteLine($"[HANGFIRE FILTER] AlertChannel fallback for email job {capturedJobId} failed: {alertEx.Message}");
                            }
                        });
                    }
                    catch (Exception captureEx)
                    {
                        Console.Error.WriteLine($"[HANGFIRE FILTER] Could not schedule AlertChannel fallback for email job {jobId}: {captureEx.Message}");
                    }

                    return;
                }

                // Obtener información de reintentos
                var retryCount = context.GetJobParameter<int>("RetryCount");
                var maxRetryAttempts = GetMaxRetryAttempts(job);
                
                // Determinar si es un job crítico (procesa dinero o estados)
                var isCriticalJob = IsCriticalJob(jobMethod, jobType);
                
                // P3-1 (versión mínima): si el job que agotó retries es RetryMoneyDistributionJobAsync,
                // marcamos el SearchHire con RefundFailedAt para que el digest diario lo recoja.
                if (jobMethod == nameof(StripeRefundService.RetryMoneyDistributionJobAsync)
                    && job.Job.Args.Count > 0 && job.Job.Args[0] is int failedHireId)
                {
                    var capturedHireId = failedHireId;
                    var capturedExceptionMessage = exception?.Message;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var refundScope = _serviceScopeFactory.CreateScope();
                            var dbCtx = refundScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            var hire = await dbCtx.SearchHires.FirstOrDefaultAsync(h => h.Id == capturedHireId);
                            if (hire != null && hire.RefundFailedAt == null)
                            {
                                hire.RefundFailedAt = DateTime.UtcNow;
                                await dbCtx.SaveChangesAsync();

                                var logSvc = refundScope.ServiceProvider.GetRequiredService<ILoggingService>();
                                await logSvc.LogCriticalAsync(
                                    message: "CRITICAL: Money distribution exhausted retries — SearchHire flagged RefundFailedAt",
                                    details: $"SearchHire {capturedHireId} has RefundFailedAt set after exhausting Hangfire retries on RetryMoneyDistributionJobAsync. Last exception: {capturedExceptionMessage}. Action: manual reconciliation required (refund or transfer).",
                                    userId: null,
                                    source: $"Hangfire.{jobType}.{jobMethod}",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: capturedHireId,
                                    additionalData: new
                                    {
                                        event_type = "refund_money_distribution_permanently_failed",
                                        SearchHireId = capturedHireId,
                                        HangfireJobId = jobId,
                                        ExceptionMessage = capturedExceptionMessage
                                    });
                            }
                        }
                        catch (Exception flagEx)
                        {
                            Console.Error.WriteLine($"[HANGFIRE FILTER] Failed to flag RefundFailedAt for SearchHire {capturedHireId}: {flagEx.Message}");
                        }
                    });
                }

                // El scope se crea DENTRO del Task.Run para que el ILoggingService scoped
                // (que envuelve un DbContext) siga vivo mientras corre la tarea en segundo plano.
                // Antes un `using` externo liberaba el scope antes de ejecutarse la tarea, lo que
                // provocaba ObjectDisposedException y la pérdida silenciosa del log crítico.
                {
                    
                    // Crear log crítico para alertar a soporte
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _serviceScopeFactory.CreateScope();
                            var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();

                            var logMessage = isCriticalJob
                                ? "CRITICAL: Hangfire job failed definitively after all retries - CRITICAL JOB"
                                : "CRITICAL: Hangfire job failed definitively after all retries";
                            
                            var logDetails = $"Hangfire job {jobId} failed definitively after {retryCount}/{maxRetryAttempts} retry attempts. " +
                                           $"Job Type: {jobType}, Method: {jobMethod}, Arguments: [{jobArgs}]. " +
                                           $"Exception Type: {exception?.GetType().Name ?? "Unknown"}, " +
                                           $"Exception Message: {exception?.Message ?? "No message"}. " +
                                           $"Stack Trace: {exception?.StackTrace ?? "No stack trace"}. " +
                                           $"{(isCriticalJob ? "⚠️ CRITICAL: This job processes money or state changes. Manual intervention required immediately." : "")} " +
                                           $"ACTION REQUIRED: Review job failure in Hangfire dashboard and investigate root cause. " +
                                           $"If this is a critical job, verify if money distribution or state changes need manual processing.";
                            
                            await loggingService.LogCriticalAsync(
                                message: logMessage,
                                details: logDetails,
                                userId: null, // No hay userId específico para jobs de sistema
                                source: $"Hangfire.{jobType}.{jobMethod}",
                                relatedEntityType: "HangfireJob",
                                relatedEntityId: int.TryParse(jobId, out var jobIdInt) ? jobIdInt : null,
                                additionalData: new
                                {
                                    JobId = jobId,
                                    JobType = jobType,
                                    JobMethod = jobMethod,
                                    JobArguments = jobArgs,
                                    RetryCount = retryCount,
                                    MaxRetryAttempts = maxRetryAttempts,
                                    ExceptionType = exception?.GetType().Name,
                                    ExceptionMessage = exception?.Message,
                                    StackTrace = exception?.StackTrace,
                                    IsCriticalJob = isCriticalJob,
                                    FailedAt = DateTime.UtcNow
                                }
                            );
                        }
                        catch (Exception logEx)
                        {
                            // Si falla el logging, al menos escribir en consola
                            Console.WriteLine($"[HANGFIRE FILTER ERROR] Failed to log job failure: {logEx.Message}");
                        }
                    });
                }
            }
        }

        public void OnStateUnapplied(ElectStateContext context, IWriteOnlyTransaction transaction)
        {
            // No necesitamos hacer nada cuando se desaplica un estado
        }

        public void OnStateApplied(ElectStateContext context, IWriteOnlyTransaction transaction)
        {
            // No necesitamos hacer nada cuando se aplica un estado
        }

        /// <summary>
        /// Determina si un job es crítico (procesa dinero o cambia estados)
        /// </summary>
        private bool IsCriticalJob(string methodName, string typeName)
        {
            var criticalMethods = new[]
            {
                "ProcessAppointmentTimerAsync",
                "ProcessAppointmentToAwaitingReportAsync",
                "ProcessMoneyDistributionAsync"
            };
            
            var criticalTypes = new[]
            {
                "AppointmentService",
                "StripeRefundService"
            };
            
            return criticalMethods.Contains(methodName) || criticalTypes.Contains(typeName);
        }

        /// <summary>
        /// Obtiene el número máximo de reintentos configurados para el job
        /// </summary>
        private int GetMaxRetryAttempts(BackgroundJob job)
        {
            // Intentar obtener de los atributos del método
            try
            {
                var method = job.Job.Method;
                var automaticRetryAttribute = method.GetCustomAttributes(typeof(AutomaticRetryAttribute), true)
                    .FirstOrDefault() as AutomaticRetryAttribute;
                
                if (automaticRetryAttribute != null)
                {
                    return automaticRetryAttribute.Attempts;
                }
            }
            catch
            {
                // Si no se puede obtener, usar valor por defecto
            }
            
            // Valor por defecto de Hangfire si no hay atributo
            return 10;
        }
    }
}


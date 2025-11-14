using Hangfire;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;

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
                
                // Obtener información de reintentos
                var retryCount = context.GetJobParameter<int>("RetryCount");
                var maxRetryAttempts = GetMaxRetryAttempts(job);
                
                // Determinar si es un job crítico (procesa dinero o estados)
                var isCriticalJob = IsCriticalJob(jobMethod, jobType);
                
                // Usar scope para acceder a servicios scoped
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
                    
                    // Crear log crítico para alertar a soporte
                    _ = Task.Run(async () =>
                    {
                        try
                        {
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


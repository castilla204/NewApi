using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using newApi.Common;
using newApi.Controllers;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;

namespace newApi.Services
{
    // TODO rename to SearchHireTimeoutService: el contenido real es ProcessAwaitingClientDecisionAsync (timeouts de 24h en SearchHires).
    public class SubscriptionService : ISubscriptionService
    {
        private readonly AppDbContext _context;
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;
        private readonly IInAppNotificationService _inAppNotifications;

        public SubscriptionService(AppDbContext context, StripeRefundService refundService, ILoggingService loggingService, IInAppNotificationService inAppNotifications)
        {
            _context = context;
            _refundService = refundService;
            _loggingService = loggingService;
            _inAppNotifications = inAppNotifications;
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue)
        {
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus");
            
            if (systemStatus == null)
            {
                // ⚠️ FRENTE 8: estado no encontrado → AVISAR en vez de rebobinar a "pending" en silencio.
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: SearchHireStatus value not found - defaulting to 'pending'",
                    details: $"GetStatusIdByValueAsync could not resolve StatusValue '{statusValue}' (SearchHireStatus). Defaulting to pending (1); verify the status is seeded. This can silently misroute a hire.",
                    source: "SubscriptionService.GetStatusIdByValueAsync",
                    relatedEntityType: "SearchHire");
                return 1;
            }

            return systemStatus.Id;
        }

        public async Task<SubscriptionLimits> GetUserSubscriptionLimits(int userId)
        {
            // Suscripciones periódicas deshabilitadas: se elimina lectura de SubscriptionPlan
            // Implementación anterior comentada para referencia:
            /*
            try
            {
                var user = await _context.Users
                    .Include(u => u.SubscriptionPlan)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user?.SubscriptionPlan == null)
                {
                    var freePlan = await _context.SubscriptionPlans
                        .FirstOrDefaultAsync(p => p.PriceYearly == 0);

                    if (freePlan == null)
                    {
                        return new SubscriptionLimits { MaxSearches = 1, MinSearchInterval = 24 };
                    }

                    return new SubscriptionLimits
                    {
                        MaxSearches = freePlan.MaxSearches,
                        MinSearchInterval = freePlan.MinSearchInterval
                    };
                }

                return new SubscriptionLimits
                {
                    MaxSearches = user.SubscriptionPlan.MaxSearches,
                    MinSearchInterval = user.SubscriptionPlan.MinSearchInterval
                };
            }
            catch (Exception ex)
            {
                throw;
            }
            */

            // Devolver límites neutros por ahora
            await Task.CompletedTask;
            return new SubscriptionLimits { MaxSearches = 9999, MinSearchInterval = 0 };
        }

        /// <summary>
        /// Solo para uso manual/admin: Procesa todos los SearchHires en awaiting_client_decision que llevan más de 24h
        /// Normalmente esto se hace con scheduled jobs, pero este método permite procesamiento manual si es necesario
        /// </summary>
        public async Task ProcessAwaitingClientDecisionAsync()
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddHours(-24); // UNIFICADO: 24h para disputas y aprobación automática

                if (_context == null)
                {
                    throw new InvalidOperationException("Database context is not initialized");
                }

                // Solo para uso manual/admin: Procesar contrataciones donde el experto completó el trabajo pero el cliente no ha decidido en 24h
                var query = _context.SearchHires
                    .Include(sh => sh.Status)
                    .Where(sh => sh.Status.StatusValue == SearchHireStatus.AwaitingClientDecision.ToStringValue()
                              && sh.UpdatedAt.HasValue
                              && sh.UpdatedAt.Value <= cutoffDate)
                    .Select(sh => new { sh.Id, sh.ClientId, sh.ExpertId, sh.Amount });

                var awaitingDecisionHires = await query.ToListAsync();

                if (awaitingDecisionHires == null || !awaitingDecisionHires.Any())
                {
                    return;
                }

                // Procesar cada uno usando el método específico
                foreach (var item in awaitingDecisionHires)
                {
                    await ProcessAwaitingClientDecisionAsync(item.Id);
                }
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        public async Task ProcessAwaitingClientDecisionAsync(int searchHireId)
        {
            try
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                if (searchHire == null)
                {
                    return; // SearchHire no encontrado
                }

                // Verificar que esté en awaiting_client_decision
                if (searchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                {
                    return; // Ya no está en awaiting_client_decision, puede haber sido procesado
                }

                // Verificar que hayan pasado 24 horas desde que cambió a awaiting_client_decision
                if (searchHire.UpdatedAt.HasValue && searchHire.UpdatedAt.Value.AddHours(24) > DateTime.UtcNow)
                {
                    return; // Aún no han pasado 24 horas
                }

                // 🛡️ Round 28 TX-5: envuelto en CreateExecutionStrategy().ExecuteAsync.
                // CRÍTICO: si el SaveChanges fallaba con retry transient (NpgsqlRetryingExecutionStrategy)
                // pero ProcessMoneyDistributionAsync ya había transferido el dinero al experto, el hire
                // quedaba SIN marcar como Completed → el job timer lo reintentaba → DOBLE PAGO al experto.
                // ProcessMoneyDistributionAsync usa idempotency keys Stripe internamente, así que un
                // retry del lambda completo es seguro (Stripe dedupe el transfer).
                var autoCompletionStrategy = _context.Database.CreateExecutionStrategy();
                await autoCompletionStrategy.ExecuteAsync(async () =>
                {
                    _context.Database.AutoSavepointsEnabled = false;
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        // Verificar nuevamente dentro de la transacción
                        var currentSearchHire = await _context.SearchHires
                            .Include(sh => sh.Status)
                            .FirstOrDefaultAsync(sh => sh.Id == searchHireId);

                        if (currentSearchHire == null || currentSearchHire.Status.StatusValue != SearchHireStatus.AwaitingClientDecision.ToStringValue())
                        {
                            await transaction.CommitAsync();
                            return;
                        }

                        // Call central orchestrator directly (idempotente por Stripe keys)
                        await _refundService.ProcessMoneyDistributionAsync(
                            searchHireId,
                            "appointment_completed_without_client_approval",
                            "Auto transfer after client timeout (24h)",
                            null);

                        // Only update status and create notifications if transfer succeeds
                        currentSearchHire.ClientApproved = true;
                        currentSearchHire.StatusId = await GetStatusIdByValueAsync(SearchHireStatus.Completed.ToStringValue());
                        currentSearchHire.UpdatedAt = DateTime.UtcNow;

                        var autoCompleteNotifications = new List<Notification>();
                        if (currentSearchHire.ExpertId.HasValue)
                        {
                            autoCompleteNotifications.Add(new Notification
                            {
                                Id = Guid.NewGuid(),
                                UserId = currentSearchHire.ExpertId.Value,
                                Title = "Pago Automático Recibido",
                                Message = $"Has recibido el pago de €{currentSearchHire.Amount:F2} por tu servicio. El cliente no respondió en 24h.",
                                Type = "payment",
                                Read = false,
                                CreatedAt = DateTime.UtcNow
                            });
                        }

                        autoCompleteNotifications.Add(new Notification
                        {
                            Id = Guid.NewGuid(),
                            UserId = currentSearchHire.ClientId,
                            Title = "Servicio Completado Automáticamente",
                            Message = $"Tu servicio de €{currentSearchHire.Amount:F2} se ha completado automáticamente.",
                            Type = "service_completion",
                            Read = false,
                            CreatedAt = DateTime.UtcNow
                        });
                        _context.Notifications.AddRange(autoCompleteNotifications);

                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();

                        // 🔔 NOTIF-CENTRAL: trigger realtime DESPUÉS del commit (best-effort,
                        // lote en una sola llamada HTTP).
                        await _inAppNotifications.BroadcastCreatedAsync(autoCompleteNotifications);
                    }
                    catch
                    {
                        try { await transaction.RollbackAsync(); } catch { }
                        throw;
                    }
                });
            }
            catch (Exception ex)
            {
                throw;
            }
        }

    }
}
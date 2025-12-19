using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Stripe;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using newApi.DataLayer.Models;
using newApi.Common;
using System;

namespace newApi.Services
{
    public class StripeRefundService
    {
        private readonly AppDbContext _context;
        private readonly SystemStatusService _systemStatusService;
        private readonly ILoggingService _loggingService;

        public StripeRefundService(AppDbContext context, SystemStatusService systemStatusService, ILoggingService loggingService)
        {
            _context = context;
            _systemStatusService = systemStatusService;
            _loggingService = loggingService;
        }


        /// <summary>
        /// Orquesta la distribución de dinero según un estado concreto: realiza refund al cliente y transferencia al experto.
        /// Respeta subestados de finalización y granularidad (categoría/tipo/global) mediante el statusValue recibido.
        /// 
        /// Estructura en 3 fases:
        /// - Fase 1: Validaciones (sin cambiar estado)
        /// - Fase 2: Cambio de estado (transacción BD rápida, separada)
        /// - Fase 3: Procesamiento de dinero (Stripe, fuera de transacción de estado)
        /// </summary>
        /// <param name="searchHireId">ID del servicio contratado</param>
        /// <param name="statusValue">Estado específico, p.ej. "appointment_cancelled_by_expert_second"</param>
        /// <param name="reason">Razón del movimiento</param>
        /// <param name="initiatedByUserId">Opcional: usuario que inicia la operación (para trazas)</param>
        /// <param name="updateState">Si true, cambia el estado de Appointment y SearchHire antes de procesar dinero (por defecto true)</param>
        /// <returns>True si refund y (si aplica) transfer se procesan correctamente</returns>
        public async Task<bool> ProcessMoneyDistributionAsync(int searchHireId, string statusValue, string reason, int? initiatedByUserId = null, bool updateState = true)
        {
            try
            {
                // Bloqueo a nivel de fila para consistencia
                var searchHire = await _context.SearchHires
                    .FromSqlInterpolated($"SELECT * FROM \"SearchHires\" WHERE \"Id\" = {searchHireId} FOR UPDATE")
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                        .ThenInclude(e => e.ExpertProfile)
                    .Include(sh => sh.SearchService)
                        .ThenInclude(ss => ss.ServiceType)
                    .FirstOrDefaultAsync();

                if (searchHire == null)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: SearchHire not found - money distribution failed",
                        details: $"SearchHire {searchHireId} not found in database. Cannot process money distribution for status {statusValue}. " +
                                $"Reason: {reason}. " +
                                $"ACTION REQUIRED: Verify SearchHire exists in database.",
                        userId: initiatedByUserId ?? 0,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            SearchHireId = searchHireId,
                            Status = statusValue,
                            Reason = reason
                        }
                    );
                    return false;
                }

                // Validar si el estado es de finalización cuando proviene de AppointmentStatus
                try
                {
                    var statusRow = await _context.SystemStatuses
                        .FirstOrDefaultAsync(s => s.StatusValue == statusValue);
                    if (statusRow != null && statusRow.StatusType == "AppointmentStatus" && statusRow.IsFinalizationStatus == false)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Invalid AppointmentStatus for money distribution",
                            details: $"Status {statusValue} is an AppointmentStatus but is not a finalization status. " +
                                    $"Cannot process money distribution. SearchHireId: {searchHireId}, Reason: {reason}. " +
                                    $"ACTION REQUIRED: Use a finalization status or SearchHireStatus for money distribution.",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                StatusType = statusRow.StatusType,
                                IsFinalizationStatus = statusRow.IsFinalizationStatus,
                                Reason = reason
                            }
                        );
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Error validating AppointmentStatus",
                        details: $"Error validating status {statusValue}: {ex.Message}",
                        userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            Status = statusValue,
                            Error = ex.Message
                        }
                    );
                }

                // Obtener configuración de distribución para el estado concreto (subestado/granularidad lo resuelve el servicio)
                var config = await _systemStatusService.GetMoneyDistributionConfigAsync(
                    statusValue,
                    searchHire.SearchService?.CategoryId,
                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId
                );

                if (config == null)
                {
                    // Fallback: si no hay configuración para subestado, usar estado final de SearchHire
                    // Intentar mapear statusValue (appointment_*) a SearchHireStatus mediante servicio centralizado
                    try
                    {
                        AppointmentStatus? appointmentStatus = MapAppointmentStatus(statusValue);
                        if (appointmentStatus.HasValue)
                        {
                            var targetSearchHireStatus = await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatus.Value);
                            if (targetSearchHireStatus.HasValue)
                            {
                                var finalStatusValue = SearchHireStatusExtensions.ToStringValue(targetSearchHireStatus.Value);
                                // Validar que el target sea estado de finalización
                                try
                                {
                                    var targetRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusValue == finalStatusValue && s.StatusType == "SearchHireStatus");
                                    if (targetRow != null && targetRow.IsFinalizationStatus == false)
                                    {
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Target SearchHireStatus is not a finalization status",
                                            details: $"Mapped status {finalStatusValue} from {statusValue} is not a finalization status. " +
                                                    $"Cannot process money distribution. SearchHireId: {searchHireId}, Reason: {reason}. " +
                                                    $"ACTION REQUIRED: Use a finalization status for money distribution.",
                                            userId: initiatedByUserId ?? searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { 
                                                OriginalStatus = statusValue,
                                                MappedStatus = finalStatusValue,
                                                IsFinalizationStatus = targetRow.IsFinalizationStatus,
                                                Reason = reason
                                            }
                                        );
                                        return false;
                                    }
                                }
                                catch (Exception ex2)
                                {
                                    // Log error but continue
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Error validating mapped SearchHireStatus",
                                        details: $"Error validating mapped status {finalStatusValue}: {ex2.Message}",
                                        userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { 
                                            OriginalStatus = statusValue,
                                            MappedStatus = finalStatusValue,
                                            Error = ex2.Message
                                        }
                                    );
                                }
                                config = await _systemStatusService.GetMoneyDistributionConfigAsync(
                                    finalStatusValue,
                                    searchHire.SearchService?.CategoryId,
                                    searchHire.SearchService?.ServiceType?.ServiceTypeCategoryId
                                );
                            }
                        }
                    }
                    catch (Exception mapEx)
                    {
                    }

                    if (config == null)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Missing money distribution config",
                            details: $"Config not found for status {statusValue}",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { Status = statusValue }
                        );
                        return false;
                    }
                }

                // MODIFICACIÓN: Validar que los porcentajes sumen 100% para evitar distribuciones incorrectas (best practice para configs financieras)
                if (Math.Abs(config.ClientPercentage + config.ExpertPercentage + config.PlatformPercentage - 100m) > 0.01m)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Invalid money distribution config",
                        details: $"Percentages do not sum to 100 for status {statusValue}",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { Status = statusValue, Config = config }
                    );
                    return false;
                }

                // ✅ CORRECCIÓN DE SEGURIDAD: Validar que ningún porcentaje sea negativo
                // Un porcentaje negativo podría usarse para extraer dinero extra del sistema
                if (config.ClientPercentage < 0 || config.ExpertPercentage < 0 || config.PlatformPercentage < 0)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Invalid money distribution config - negative percentage detected",
                        details: $"One or more percentages are negative for status {statusValue}. " +
                                $"Client: {config.ClientPercentage}%, Expert: {config.ExpertPercentage}%, Platform: {config.PlatformPercentage}%. " +
                                $"This could indicate a configuration attack or data corruption. " +
                                $"ACTION REQUIRED: Review StatusConfigurations table for status {statusValue}.",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            Status = statusValue, 
                            ClientPercentage = config.ClientPercentage,
                            ExpertPercentage = config.ExpertPercentage,
                            PlatformPercentage = config.PlatformPercentage,
                            PossibleAttack = true
                        }
                    );
                    return false;
                }

                // ✅ STRIPE TAX: Calcular sobre BASE PRE-TAX (sin IVA), no sobre total con IVA
                // Si BaseAmount es null (datos antiguos), usar Amount como fallback para compatibilidad
                var baseAmount = searchHire.BaseAmount ?? searchHire.Amount;
                
                if (searchHire.BaseAmount == null)
                {
                    // ⚠️ WARNING: No hay BaseAmount, usando Amount como fallback
                    // Esto puede causar que se calcule comisión sobre IVA en datos antiguos
                    await _loggingService.LogWarningAsync(
                        message: "Calculating percentages on total amount (tax may be included)",
                        details: $"SearchHire {searchHireId} does not have BaseAmount. Using Amount {searchHire.Amount}€ as fallback. " +
                                $"This may cause commission to be calculated on tax amount. " +
                                $"TaxAmount: {searchHire.TaxAmount ?? 0}€",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId
                    );
                }

                // ✅ CORRECCIÓN CRÍTICA: Calcular refunds sobre BASE (para distribución interna)
                // pero convertir a BRUTO (con IVA) cuando se envía a Stripe para que calcule el IVA proporcional
                var clientRefundAmountBase = baseAmount * (config.ClientPercentage / 100);
                var expertAmount = baseAmount * (config.ExpertPercentage / 100);
                var platformAmount = baseAmount * (config.PlatformPercentage / 100);
                
                // ✅ STRIPE TAX: Convertir refund base a BRUTO (con IVA proporcional) para Stripe
                // Si hay TaxAmount, calcular el porcentaje de IVA y añadirlo al refund
                decimal clientRefundAmountForStripe;
                
                // ✅ OPTIMIZACIÓN: Para reembolso 100%, usar Amount original directamente para evitar errores de redondeo
                if (config.ClientPercentage == 100)
                {
                    // Reembolso total: devolver el monto exacto que pagó el cliente
                    clientRefundAmountForStripe = searchHire.Amount;
                }
                else if (searchHire.TaxAmount.HasValue && searchHire.TaxAmount.Value > 0 && baseAmount > 0)
                {
                    // Reembolso parcial con tax: calcular proporcionalmente
                    // Método más preciso: usar proporción del total en lugar de recalcular con taxRate
                    // grossRefund = totalAmount * (clientPercentage / 100)
                    clientRefundAmountForStripe = searchHire.Amount * (config.ClientPercentage / 100);
                }
                else
                {
                    // Si no hay tax o es dato antiguo, usar el monto calculado directamente
                    // (Stripe manejará el tax automáticamente si está configurado)
                    clientRefundAmountForStripe = clientRefundAmountBase;
                }
                
                // ✅ Usar clientRefundAmountBase para cálculos internos, clientRefundAmountForStripe para Stripe
                var clientRefundAmount = clientRefundAmountBase; // Para logs y cálculos internos
                
                // ✅ Logging mejorado con información de tax
                await _loggingService.LogInfoAsync(
                    message: "Money distribution calculated on base amount (pre-tax)",
                    details: $"BaseAmount: {baseAmount}€, TaxAmount: {searchHire.TaxAmount ?? 0}€, " +
                            $"TotalAmount: {searchHire.Amount}€. " +
                            $"Distribution (base): Client {clientRefundAmount}€ ({config.ClientPercentage}%), " +
                            $"Expert {expertAmount}€ ({config.ExpertPercentage}%), " +
                            $"Platform {platformAmount}€ ({config.PlatformPercentage}%). " +
                            $"Refund to Stripe (gross with tax): {clientRefundAmountForStripe}€",
                    userId: initiatedByUserId ?? searchHire.ClientId,
                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId
                );

                // MODIFICACIÓN: Estimar fees de Stripe y warning si platformAmount no cubre (para evitar pérdidas, según guías 2025)
                var stripeFeeEstimate = searchHire.Amount * 0.029m + 0.30m; // 2.9% + 0.30€ estándar para EUR
                if (platformAmount < stripeFeeEstimate)
                {
                    // Opcional: Fallar si es crítico, pero por ahora warning
                }


                // Localizar el pago original y verificar transacciones existentes
                FinancialTransaction existingRefund = null;
                FinancialTransaction existingTransfer = null;
                FinancialTransaction servicePayment = null;
                
                // Buscar el servicePayment para validaciones
                servicePayment = await _context.FinancialTransactions
                    .Where(ft => ft.UserId == searchHire.ClientId
                              && ft.TransactionType == "ServicePayment"
                              && ft.RelatedEntityType == "SearchHire"
                              && ft.RelatedEntityId == searchHireId
                              && !string.IsNullOrEmpty(ft.StripePaymentIntentId))
                    .FirstOrDefaultAsync();
                
                // Si hay servicePayment, verificar si ya existen refund o transfer
                if (servicePayment != null)
                {
                    existingRefund = await _context.FinancialTransactions
                        .FirstOrDefaultAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                                   ft.RelatedEntityId == searchHireId &&
                                                   ft.TransactionType == "Refund" &&
                                                   ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId);
                    
                    existingTransfer = await _context.FinancialTransactions
                        .FirstOrDefaultAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                                   ft.RelatedEntityId == searchHireId &&
                                                   ft.TransactionType == "Payout" &&
                                                   !string.IsNullOrEmpty(ft.StripeTransferId));
                }
                else
                {
                    // Buscar el servicePayment para procesar dinero
                    servicePayment = await _context.FinancialTransactions
                        .Where(ft => ft.UserId == searchHire.ClientId
                                  && ft.TransactionType == "ServicePayment"
                                  && ft.RelatedEntityType == "SearchHire"
                                  && ft.RelatedEntityId == searchHireId
                                  && !string.IsNullOrEmpty(ft.StripePaymentIntentId))
                        .FirstOrDefaultAsync();
                }  

                if (servicePayment == null)
                {
                    // 🚨 LOG CRÍTICO: Pago original no encontrado (una sola vez, con toda la información)
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Original payment not found - money distribution failed",
                        details: $"SearchHire {searchHireId} finalization failed because the original payment (ServicePayment) transaction was not found in the database. " +
                                $"This indicates a data consistency issue. " +
                                $"Status: {statusValue}, Reason: {reason}, ClientId: {searchHire.ClientId}, ExpertId: {searchHire.ExpertId}, Amount: {searchHire.Amount}€. " +
                                $"ACTION REQUIRED: Verify FinancialTransactions table for SearchHire {searchHireId} and ServicePayment transaction.",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            Status = statusValue,
                            Reason = reason,
                            ClientId = searchHire.ClientId,
                            ExpertId = searchHire.ExpertId,
                            Amount = searchHire.Amount,
                            ClientRefundAmount = clientRefundAmount,
                            ExpertTransferAmount = expertAmount,
                            PlatformAmount = platformAmount
                        }
                    );
                    return false;
                }

                // MODIFICACIÓN: Verificar balance disponible antes de cualquier outflow (best practice Stripe 2025 para evitar negativos)
                try
                {
                    var balanceService = new BalanceService();
                    var balance = await balanceService.GetAsync();
                    var availableEur = balance.Available?.FirstOrDefault(b => b.Currency == "eur")?.Amount / 100.0m ?? 0;
                    // ✅ STRIPE TAX FIX: Usar monto BRUTO (con IVA) para verificación de balance
                    // El refund a Stripe será clientRefundAmountForStripe (incluye IVA proporcional)
                    // El transfer al experto es expertAmount (sin IVA, es pago de servicios no sujeto)
                    var totalOutflow = clientRefundAmountForStripe + expertAmount;
                    if (availableEur < totalOutflow)
                    {
                        // 🚨 LOG CRÍTICO: Balance insuficiente (una sola vez, con información completa)
                        // IMPORTANTE: Este log se crea ANTES de entrar en la transacción, así que debe estar disponible inmediatamente
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Insufficient Stripe platform balance for money distribution",
                            details: $"SearchHire {searchHireId} finalization failed due to insufficient Stripe platform balance. " +
                                    $"Available Balance: {availableEur}€, Required Outflow: {totalOutflow}€ (Client Refund Gross: {clientRefundAmountForStripe}€, Expert Transfer: {expertAmount}€). " +
                                    $"Distribution Plan: Client={config.ClientPercentage}%, Expert={config.ExpertPercentage}%, Platform={config.PlatformPercentage}%. " +
                                    $"Status: {statusValue}, Reason: {reason}, PaymentIntentId: {servicePayment.StripePaymentIntentId}. " +
                                    $"ACTION REQUIRED: Wait for balance to be available (from PaymentIntent capture) or manually verify Stripe balance and retry distribution.",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Reason = reason,
                                AvailableBalance = availableEur,
                                TotalOutflow = totalOutflow,
                                ClientRefundAmountGross = clientRefundAmountForStripe,
                                ClientRefundAmountBase = clientRefundAmount,
                                ExpertTransferAmount = expertAmount,
                                PlatformAmount = platformAmount,
                                PaymentIntentId = servicePayment.StripePaymentIntentId
                            }
                        );
                        
                        // ✅ NO necesitamos delay - LoggingService usa su propio DbContext scoped
                        // que se commitea independientemente de la transacción de RefundService
                        // Esto asegura que el log sea visible inmediatamente post-commit sin interferencia
                        return false;
                    }
                }
                catch (StripeException balanceEx)
                {
                    // 🚨 LOG CRÍTICO: Error al verificar balance (una sola vez, con toda la información)
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Error checking Stripe balance - money distribution failed",
                        details: $"SearchHire {searchHireId} finalization failed due to error checking Stripe platform balance. " +
                                $"Stripe Error: {balanceEx.Message}, Type: {balanceEx.StripeError?.Type}, Code: {balanceEx.StripeError?.Code}. " +
                                $"Required outflow: {clientRefundAmountForStripe + expertAmount}€ (Client Gross: {clientRefundAmountForStripe}€, Expert: {expertAmount}€). " +
                                $"ACTION REQUIRED: Verify Stripe balance manually and retry distribution if balance is sufficient.",
                        userId: initiatedByUserId ?? searchHire.ClientId,
                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId,
                        additionalData: new { 
                            Status = statusValue,
                            StripeError = balanceEx.Message,
                            StripeErrorType = balanceEx.StripeError?.Type,
                            StripeErrorCode = balanceEx.StripeError?.Code,
                            RequiredOutflow = clientRefundAmountForStripe + expertAmount,
                            ClientRefundAmountGross = clientRefundAmountForStripe,
                            ClientRefundAmountBase = clientRefundAmount,
                            ExpertTransferAmount = expertAmount
                        }
                    );
                    return false;
                }

                // ✅ Verificar que el PaymentIntent esté capturado antes de intentar Transfer
                if (expertAmount > 0)
                {
                    try
                    {
                        // ✅ Verificar que el PaymentIntent esté capturado antes de intentar Transfer
                        var paymentIntentService = new PaymentIntentService();
                        var paymentIntent = await paymentIntentService.GetAsync(servicePayment.StripePaymentIntentId);
                        
                        if (paymentIntent.Status != "succeeded")
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Money distribution failed - PaymentIntent not captured",
                                details: $"SearchHire {searchHireId} finalization failed because PaymentIntent {servicePayment.StripePaymentIntentId} is not in 'succeeded' status. " +
                                        $"Current status: {paymentIntent.Status}. " +
                                        $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                        $"1) Ensure PaymentIntent is captured " +
                                        $"2) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) " +
                                        $"3) Platform retains {platformAmount:F2}€.",
                                userId: initiatedByUserId ?? searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    PaymentIntentId = servicePayment.StripePaymentIntentId,
                                    PaymentIntentStatus = paymentIntent.Status,
                                    ExpertTransferAmount = expertAmount,
                                    PlatformAmount = platformAmount,
                                    ExpertId = searchHire.ExpertId
                                }
                            );
                            
                            return false;
                        }
                    }
                    catch (StripeException stripeEx)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Error verifying PaymentIntent for money distribution",
                            details: $"SearchHire {searchHireId} finalization failed due to error verifying PaymentIntent. " +
                                    $"PaymentIntentId: {servicePayment.StripePaymentIntentId}, Error: {stripeEx.Message}",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                PaymentIntentId = servicePayment.StripePaymentIntentId,
                                Error = stripeEx.Message
                            }
                        );
                        
                        return false;
                    }
                    catch (Exception ex)
                    {
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Error verifying PaymentIntent for money distribution",
                            details: $"SearchHire {searchHireId} finalization failed due to error verifying PaymentIntent. " +
                                    $"PaymentIntentId: {servicePayment.StripePaymentIntentId}, Error: {ex.Message}",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                PaymentIntentId = servicePayment.StripePaymentIntentId,
                                Error = ex.Message
                            }
                        );
                        
                        return false;
                    }
                }

                // ===== FASE 2: CAMBIAR ESTADO (transacción BD rápida, separada) =====
                if (updateState)
                {
                    // ✅ CORRECCIÓN: Verificar si ya hay una transacción activa (ej: desde AccountDeletionService)
                    var existingTransaction = _context.Database.CurrentTransaction;
                    bool stateUpdateSuccess = false;
                    
                    // ✅ Si no hay transacción existente, crear una nueva con estrategia de reintento
                    if (existingTransaction == null)
                    {
                        var stateStrategy = _context.Database.CreateExecutionStrategy();
                        stateUpdateSuccess = await stateStrategy.ExecuteAsync(async () =>
                        {
                            using var stateTransaction = await _context.Database.BeginTransactionAsync(
                                System.Data.IsolationLevel.ReadCommitted
                            );
                            try
                            {
                            // ✅ MEJORA GROK: Cargar entidades explícitamente para evitar null references
                            var searchHireForState = await _context.SearchHires
                                .Include(sh => sh.Status)
                                .Include(sh => sh.Appointment)
                                    .ThenInclude(a => a.Status)
                                .FirstOrDefaultAsync(sh => sh.Id == searchHireId);
                        
                        if (searchHireForState == null)
                        {
                            await stateTransaction.RollbackAsync();
                            return false;
                        }
                        
                        // ✅ MEJORA GROK: Verificar estado actual (evitar dobles cancelaciones)
                        if (searchHireForState.Status?.IsFinalizationStatus == true)
                        {
                            // Ya está finalizado, no cambiar estado pero continuar con dinero
                            await stateTransaction.CommitAsync();
                            // Continuar a Fase 3 para procesar dinero si es necesario
                            return true; // Estado ya estaba finalizado, continuar con dinero
                        }
                        else
                        {
                            // Mapear statusValue a estados finales
                            AppointmentStatus? appointmentStatus = MapAppointmentStatus(statusValue);
                            
                            // ✅ MEJORA: Verificar si el estado objetivo ya está aplicado (evitar cambios redundantes)
                            bool stateNeedsUpdate = false;
                            
                            // Verificar Appointment.Status
                            if (appointmentStatus.HasValue && searchHireForState.Appointment != null)
                            {
                                var appointmentStatusRow = await _context.SystemStatuses
                                    .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                             s.StatusValue == statusValue);
                                if (appointmentStatusRow != null)
                                {
                                    // ✅ Verificar si el estado actual es diferente al objetivo
                                    if (searchHireForState.Appointment.StatusId != appointmentStatusRow.Id)
                                    {
                                        searchHireForState.Appointment.StatusId = appointmentStatusRow.Id;
                                        searchHireForState.Appointment.UpdatedAt = DateTime.UtcNow;
                                        stateNeedsUpdate = true;
                                    }
                                }
                            }
                            
                            // Verificar SearchHire.Status
                            var targetSearchHireStatus = appointmentStatus.HasValue 
                                ? await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatus.Value)
                                : null;
                            
                            string? targetSearchHireStatusValue = null;
                            if (!targetSearchHireStatus.HasValue)
                            {
                                // Si no hay mapeo de AppointmentStatus, usar statusValue directamente
                                targetSearchHireStatusValue = statusValue;
                            }
                            else
                            {
                                targetSearchHireStatusValue = SearchHireStatusExtensions.ToStringValue(targetSearchHireStatus.Value);
                            }
                            
                            if (!string.IsNullOrEmpty(targetSearchHireStatusValue))
                            {
                                var searchHireStatusRow = await _context.SystemStatuses
                                    .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                             s.StatusValue == targetSearchHireStatusValue);
                                if (searchHireStatusRow != null)
                                {
                                    // ✅ Verificar si el estado actual es diferente al objetivo
                                    if (searchHireForState.StatusId != searchHireStatusRow.Id)
                                    {
                                        searchHireForState.StatusId = searchHireStatusRow.Id;
                                        searchHireForState.UpdatedAt = DateTime.UtcNow;
                                        stateNeedsUpdate = true;
                                    }
                                }
                            }
                            
                            // Solo hacer SaveChanges si realmente hay cambios
                            if (stateNeedsUpdate)
                            {
                                await _context.SaveChangesAsync();
                            }
                            await stateTransaction.CommitAsync();
                            // ✅ Estado verificado/actualizado y commiteado
                            return true; // Estado actualizado exitosamente
                        }
                    }
                    catch (DbUpdateConcurrencyException ex)
                    {
                        // ✅ MEJORA GROK: Manejo específico de concurrencia
                        await stateTransaction.RollbackAsync();
                        // Usar searchHire ya cargado o usar initiatedByUserId como fallback
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Concurrency conflict updating state",
                            details: $"Another process modified SearchHire {searchHireId} concurrently. Error: {ex.Message}",
                            userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Error = ex.Message,
                                ErrorType = ex.GetType().Name
                            }
                        );
                        return false; // NO procesar dinero si no pudimos cambiar estado
                    }
                    catch (Exception ex)
                    {
                        // Error de BD al cambiar estado → Revertir
                        await stateTransaction.RollbackAsync();
                        // Usar searchHire ya cargado o usar initiatedByUserId como fallback
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Failed to update state before money distribution",
                            details: $"SearchHire {searchHireId} state update failed: {ex.Message}. StackTrace: {ex.StackTrace}",
                            userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Error = ex.Message,
                                ErrorType = ex.GetType().Name,
                                StackTrace = ex.StackTrace
                            }
                        );
                        return false; // NO procesar dinero si no pudimos cambiar estado
                    }
                    });
                    }
                    else
                    {
                        // ✅ Usar transacción existente - ejecutar sin crear nueva transacción
                        try
                        {
                            // ✅ MEJORA GROK: Cargar entidades explícitamente para evitar null references
                            var searchHireForState = await _context.SearchHires
                                .Include(sh => sh.Status)
                                .Include(sh => sh.Appointment)
                                    .ThenInclude(a => a.Status)
                                .FirstOrDefaultAsync(sh => sh.Id == searchHireId);
                        
                            if (searchHireForState == null)
                            {
                                stateUpdateSuccess = false;
                            }
                            else if (searchHireForState.Status?.IsFinalizationStatus == true)
                            {
                                // Ya está finalizado, no cambiar estado pero continuar con dinero
                                stateUpdateSuccess = true; // Estado ya estaba finalizado, continuar con dinero
                            }
                            else
                            {
                                // Mapear statusValue a estados finales
                                AppointmentStatus? appointmentStatus = MapAppointmentStatus(statusValue);
                                
                                // ✅ MEJORA: Verificar si el estado objetivo ya está aplicado (evitar cambios redundantes)
                                bool stateNeedsUpdate = false;
                                
                                // Verificar Appointment.Status
                                if (appointmentStatus.HasValue && searchHireForState.Appointment != null)
                                {
                                    var appointmentStatusRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                                 s.StatusValue == statusValue);
                                    if (appointmentStatusRow != null)
                                    {
                                        // ✅ Verificar si el estado actual es diferente al objetivo
                                        if (searchHireForState.Appointment.StatusId != appointmentStatusRow.Id)
                                        {
                                            searchHireForState.Appointment.StatusId = appointmentStatusRow.Id;
                                            searchHireForState.Appointment.UpdatedAt = DateTime.UtcNow;
                                            stateNeedsUpdate = true;
                                        }
                                    }
                                }
                                
                                // Verificar SearchHire.Status
                                var targetSearchHireStatus = appointmentStatus.HasValue 
                                    ? await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatus.Value)
                                    : null;
                                
                                string? targetSearchHireStatusValue = null;
                                if (!targetSearchHireStatus.HasValue)
                                {
                                    // Si no hay mapeo de AppointmentStatus, usar statusValue directamente
                                    targetSearchHireStatusValue = statusValue;
                                }
                                else
                                {
                                    targetSearchHireStatusValue = SearchHireStatusExtensions.ToStringValue(targetSearchHireStatus.Value);
                                }
                                
                                if (!string.IsNullOrEmpty(targetSearchHireStatusValue))
                                {
                                    var searchHireStatusRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                                 s.StatusValue == targetSearchHireStatusValue);
                                    if (searchHireStatusRow != null)
                                    {
                                        // ✅ Verificar si el estado actual es diferente al objetivo
                                        if (searchHireForState.StatusId != searchHireStatusRow.Id)
                                        {
                                            searchHireForState.StatusId = searchHireStatusRow.Id;
                                            searchHireForState.UpdatedAt = DateTime.UtcNow;
                                            stateNeedsUpdate = true;
                                        }
                                    }
                                }
                                
                                // Solo hacer SaveChanges si realmente hay cambios
                                if (stateNeedsUpdate)
                                {
                                    await _context.SaveChangesAsync();
                                }
                                // ✅ Estado verificado/actualizado (sin commit - usa transacción existente)
                                stateUpdateSuccess = true; // Estado actualizado exitosamente
                            }
                        }
                        catch (DbUpdateConcurrencyException ex)
                        {
                            // ✅ MEJORA GROK: Manejo específico de concurrencia
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Concurrency conflict updating state",
                                details: $"Another process modified SearchHire {searchHireId} concurrently. Error: {ex.Message}. " +
                                        $"Note: Using existing transaction from caller, rollback will be handled by caller.",
                                userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    Error = ex.Message,
                                    ErrorType = ex.GetType().Name,
                                    UsingExistingTransaction = true
                                }
                            );
                            stateUpdateSuccess = false;
                        }
                        catch (Exception ex)
                        {
                            // Error de BD al cambiar estado
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Failed to update state before money distribution",
                                details: $"SearchHire {searchHireId} state update failed: {ex.Message}. StackTrace: {ex.StackTrace}. " +
                                        $"Note: Using existing transaction from caller, rollback will be handled by caller.",
                                userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    Error = ex.Message,
                                    ErrorType = ex.GetType().Name,
                                    StackTrace = ex.StackTrace,
                                    UsingExistingTransaction = true
                                }
                            );
                            stateUpdateSuccess = false;
                        }
                    }

                    // ✅ Verificar si el cambio de estado fue exitoso
                    if (!stateUpdateSuccess)
                    {
                        // ⚠️ FALLBACK: Si falló el cambio de estado, intentar cambiarlo manualmente para evitar bloqueos
                        // Esto es crítico para evitar que el sistema quede bloqueado
                        try
                        {
                            var fallbackSearchHire = await _context.SearchHires
                                .Include(sh => sh.Status)
                                .Include(sh => sh.Appointment)
                                    .ThenInclude(a => a.Status)
                                .FirstOrDefaultAsync(sh => sh.Id == searchHireId);
                            
                            if (fallbackSearchHire != null && fallbackSearchHire.Status?.IsFinalizationStatus != true)
                            {
                                // Mapear statusValue a estados finales
                                AppointmentStatus? appointmentStatus = MapAppointmentStatus(statusValue);
                                
                                // Cambiar Appointment.Status si aplica
                                if (appointmentStatus.HasValue && fallbackSearchHire.Appointment != null)
                                {
                                    var appointmentStatusRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                                 s.StatusValue == statusValue);
                                    if (appointmentStatusRow != null && fallbackSearchHire.Appointment.StatusId != appointmentStatusRow.Id)
                                    {
                                        fallbackSearchHire.Appointment.StatusId = appointmentStatusRow.Id;
                                        fallbackSearchHire.Appointment.UpdatedAt = DateTime.UtcNow;
                                    }
                                }
                                
                                // Cambiar SearchHire.Status
                                var targetSearchHireStatus = appointmentStatus.HasValue 
                                    ? await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatus.Value)
                                    : null;
                                
                                string? targetSearchHireStatusValue = null;
                                if (!targetSearchHireStatus.HasValue)
                                {
                                    targetSearchHireStatusValue = statusValue;
                                }
                                else
                                {
                                    targetSearchHireStatusValue = SearchHireStatusExtensions.ToStringValue(targetSearchHireStatus.Value);
                                }
                                
                                if (!string.IsNullOrEmpty(targetSearchHireStatusValue))
                                {
                                    var searchHireStatusRow = await _context.SystemStatuses
                                        .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                                 s.StatusValue == targetSearchHireStatusValue);
                                    if (searchHireStatusRow != null && fallbackSearchHire.StatusId != searchHireStatusRow.Id)
                                    {
                                        fallbackSearchHire.StatusId = searchHireStatusRow.Id;
                                        fallbackSearchHire.UpdatedAt = DateTime.UtcNow;
                                    }
                                }
                                
                                await _context.SaveChangesAsync();
                                
                                await _loggingService.LogWarningAsync(
                                    message: "State updated manually after ProcessMoneyDistributionAsync state phase failure",
                                    details: $"SearchHire {searchHireId} state was manually updated as fallback because ProcessMoneyDistributionAsync failed in Phase 2 (state change). " +
                                            $"This prevents the system from being blocked. Status changed to: {targetSearchHireStatusValue ?? statusValue}",
                                    userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { 
                                        Status = statusValue,
                                        FallbackStateChange = true
                                    }
                                );
                                
                                // Continuar con procesamiento de dinero aunque haya fallado la Fase 2
                                // El estado ya está cambiado, así que podemos intentar procesar el dinero
                            }
                        }
                        catch (Exception fallbackEx)
                        {
                            // Si el fallback también falla, log crítico pero continuar
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Fallback state update also failed",
                                details: $"SearchHire {searchHireId} state update failed in both main phase and fallback. " +
                                        $"Fallback error: {fallbackEx.Message}. " +
                                        $"System may be blocked. Manual intervention required.",
                                userId: initiatedByUserId ?? searchHire?.ClientId ?? 0,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    FallbackError = fallbackEx.Message
                                }
                            );
                            // Aún así, intentar procesar dinero (puede que el estado ya esté correcto)
                        }
                    }
                }

                // ===== FASE 3: PROCESAR DINERO (fuera de transacción de estado) =====
                // Orquestación bajo estrategia de reintento y transacción
                // ✅ CORRECCIÓN: Verificar si ya hay una transacción activa ANTES de usar CreateExecutionStrategy
                var existingTransactionForMoney = _context.Database.CurrentTransaction;
                
                // ✅ Función auxiliar para procesar dinero (reutilizable)
                async Task<bool> ProcessMoneyAsync()
                {
                    IDbContextTransaction transaction = null;
                    if (existingTransactionForMoney == null)
                    {
                        transaction = await _context.Database.BeginTransactionAsync();
                    }
                    // MODIFICACIÓN: Declarar variables fuera del try para acceso en catch blocks
                    string createdTransferId = null;
                    string createdRefundId = null;
                    FinancialTransaction pendingRefundTx = null;
                    FinancialTransaction pendingTransferTx = null;
                    
                    try
                    {
                        // ✅ CORRECCIÓN: Usar las transacciones ya verificadas dentro del FOR UPDATE (si están disponibles)
                        // Si no están disponibles, verificar de nuevo dentro de la transacción para garantizar atomicidad
                        FinancialTransaction existingRefundLocal = existingRefund;
                        FinancialTransaction existingTransferLocal = existingTransfer;
                        
                        // Si no se encontraron antes, verificar de nuevo dentro de la transacción
                        if (existingRefundLocal == null && existingTransferLocal == null && servicePayment != null)
                        {
                            existingRefundLocal = await _context.FinancialTransactions
                            .FirstOrDefaultAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                                       ft.RelatedEntityId == searchHireId &&
                                                       ft.TransactionType == "Refund" &&
                                                       ft.StripePaymentIntentId == servicePayment.StripePaymentIntentId);
                        
                            existingTransferLocal = await _context.FinancialTransactions
                            .FirstOrDefaultAsync(ft => ft.RelatedEntityType == "SearchHire" &&
                                                       ft.RelatedEntityId == searchHireId &&
                                                       ft.TransactionType == "Payout" &&
                                                       !string.IsNullOrEmpty(ft.StripeTransferId));
                        }
                        
                        // Si ya existe refund o transfer, verificar si es necesario procesar de nuevo
                        bool refundAlreadyProcessed = existingRefundLocal != null && !string.IsNullOrEmpty(existingRefundLocal.StripeRefundId);
                        bool transferAlreadyProcessed = existingTransferLocal != null && !string.IsNullOrEmpty(existingTransferLocal.StripeTransferId);
                        
                        // Si ambos ya están procesados, retornar true (idempotencia)
                        if (refundAlreadyProcessed && (transferAlreadyProcessed || expertAmount == 0))
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Money distribution already processed - idempotent call",
                                details: $"SearchHire {searchHireId} money distribution was already processed. " +
                                        $"Refund: {(refundAlreadyProcessed ? $"Already processed ({existingRefundLocal.StripeRefundId})" : "Not needed")}, " +
                                        $"Transfer: {(transferAlreadyProcessed ? $"Already processed ({existingTransferLocal.StripeTransferId})" : expertAmount == 0 ? "Not needed" : "Not processed")}. " +
                                        $"Status: {statusValue}, Reason: {reason}",
                                userId: initiatedByUserId ?? searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    RefundAlreadyProcessed = refundAlreadyProcessed,
                                    TransferAlreadyProcessed = transferAlreadyProcessed,
                                    ExistingRefundId = existingRefundLocal?.StripeRefundId,
                                    ExistingTransferId = existingTransferLocal?.StripeTransferId
                                }
                            );
                            
                            if (transaction != null)
                            {
                                await transaction.CommitAsync();
                            }
                            return true; // ✅ Ya procesado, retornar éxito
                        }
                        
                        // Si solo uno está procesado, log warning pero continuar con el que falta
                        if (refundAlreadyProcessed || transferAlreadyProcessed)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "Partial money distribution detected - processing missing transactions",
                                details: $"SearchHire {searchHireId} has partial money distribution. " +
                                        $"Refund: {(refundAlreadyProcessed ? $"Already processed ({existingRefundLocal.StripeRefundId})" : "Needs processing")}, " +
                                        $"Transfer: {(transferAlreadyProcessed ? $"Already processed ({existingTransferLocal.StripeTransferId})" : "Needs processing")}. " +
                                        $"Processing missing transactions only.",
                                userId: initiatedByUserId ?? searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                additionalData: new { 
                                    Status = statusValue,
                                    RefundAlreadyProcessed = refundAlreadyProcessed,
                                    TransferAlreadyProcessed = transferAlreadyProcessed
                                }
                            );
                        }

                        var normalizedStatusValue = (statusValue ?? "unknown").ToLowerInvariant();
                        var idempotencyBaseKey = $"searchhire:{searchHireId}:status:{normalizedStatusValue}";
                        string BuildIdempotencyKey(string suffix)
                        {
                            var fullKey = $"{idempotencyBaseKey}:{suffix}";
                            return fullKey.Length > 255 ? fullKey.Substring(0, 255) : fullKey;
                        }

                        var transferIdempotencyKey = BuildIdempotencyKey("transfer");
                        var refundIdempotencyKey = BuildIdempotencyKey("refund");
                        var reversalIdempotencyKey = BuildIdempotencyKey("reversal");

                        // ✅ CORRECCIÓN CRÍTICA: Ejecutar Refund PRIMERO, luego Transfer solo si Refund fue exitoso
                        // Esto previene pérdida de dinero: si Refund falla, no se hace Transfer
                        var needsRefund = clientRefundAmount > 0 && !refundAlreadyProcessed;
                        var needsTransfer = expertAmount > 0 && searchHire.ExpertId.HasValue && !transferAlreadyProcessed;

                        // ✅ PASO 1: Refund PRIMERO (si aplica) - Patrón Outbox
                        if (needsRefund)
                        {
                            // ✅ PATRÓN OUTBOX: Guardar en BD ANTES de llamar a Stripe (con estado "pending")
                            // ✅ CORRECCIÓN: Guardar el monto BRUTO (con IVA) que el cliente realmente recibe
                            pendingRefundTx = new FinancialTransaction
                            {
                                UserId = searchHire.ClientId,
                                Amount = clientRefundAmountForStripe, // ✅ BRUTO con IVA (lo que el cliente recibe)
                                TransactionType = "Refund",
                                RelatedEntityType = "SearchHire",
                                RelatedEntityId = searchHireId,
                                StripePaymentIntentId = servicePayment.StripePaymentIntentId,
                                StripeRefundId = null, // Pendiente - se actualizará después
                                CreatedAt = DateTime.UtcNow
                            };
                            _context.FinancialTransactions.Add(pendingRefundTx);
                            
                            // ✅ CRÍTICO: Verificar que SaveChangesAsync inicial sea exitoso antes de llamar a Stripe
                            try
                            {
                                await _context.SaveChangesAsync(); // ✅ Guardar ANTES de Stripe
                            }
                            catch (Exception saveEx)
                            {
                                // Si falla al guardar inicial, NO llamar a Stripe
                                _context.FinancialTransactions.Remove(pendingRefundTx);
                                if (transaction != null)
                                {
                                    await transaction.RollbackAsync();
                                }
                                
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Failed to save pending refund transaction before Stripe",
                                    details: $"SearchHire {searchHireId} finalization failed: Cannot proceed with Stripe refund because SaveChangesAsync failed when saving pending transaction. " +
                                            $"No money was moved. Error: {saveEx.Message}. " +
                                            $"Stack Trace: {saveEx.StackTrace}. " +
                                            $"ACTION REQUIRED: Check database connectivity and retry.",
                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { 
                                        Status = statusValue,
                                        ClientRefundAmount = clientRefundAmount,
                                        ErrorType = saveEx.GetType().Name,
                                        ErrorMessage = saveEx.Message,
                                        StackTrace = saveEx.StackTrace,
                                        InnerException = saveEx.InnerException?.Message
                                    }
                                );
                                return false;
                            }

                            var refundOptions = new RefundCreateOptions
                            {
                                PaymentIntent = servicePayment.StripePaymentIntentId,
                                // ✅ STRIPE TAX: Enviar monto BRUTO (con IVA proporcional) para que Stripe calcule correctamente
                                // ✅ CRÍTICO: Usar Math.Round para evitar pérdida de centavos por truncamiento
                                Amount = (long)Math.Round(clientRefundAmountForStripe * 100, MidpointRounding.AwayFromZero),
                                Reason = RefundReasons.RequestedByCustomer,
                                Metadata = new Dictionary<string, string>
                                {
                                    { "searchHireId", searchHireId.ToString() },
                                    { "statusValue", statusValue },
                                    { "clientPercentage", config.ClientPercentage.ToString() },
                                    { "expertPercentage", config.ExpertPercentage.ToString() },
                                    { "platformPercentage", config.PlatformPercentage.ToString() },
                                    { "reason", reason },
                                    { "originalTransactionId", servicePayment.Id.ToString() },
                                    { "clientId", searchHire.ClientId.ToString() },
                                    { "financialTransactionId", pendingRefundTx.Id.ToString() } // ✅ Trazabilidad
                                }
                            };

                            var refundRequestOptions = new RequestOptions
                            {
                                IdempotencyKey = refundIdempotencyKey
                            };

                            try
                            {
                                var refundSvc = new RefundService();

                                // Reintento simple para transients (hasta 3 veces)
                                Refund refund = null;
                                const int maxRetries = 3;
                                for (int attempt = 1; attempt <= maxRetries; attempt++)
                                {
                                    try
                                    {
                                        refund = await refundSvc.CreateAsync(refundOptions, refundRequestOptions);
                                        break;
                                    }
                                    catch (StripeException ex) when ((int)ex.HttpStatusCode >= 500 || (int)ex.HttpStatusCode == 429)
                                    {
                                        if (attempt == maxRetries)
                                            throw;
                                        await Task.Delay(1000 * attempt);
                                    }
                                }
                                
                                // ✅ CRÍTICO: Verificar que el refund esté en estado "succeeded"
                                bool refundVerified = await VerifyRefundStatusAsync(refund.Id, searchHireId, initiatedByUserId);
                                if (!refundVerified)
                                {
                                    // ✅ CORRECCIÓN CRÍTICA DE SEGURIDAD: El refund ya se creó en Stripe (tenemos refund.Id)
                                    // El refund puede estar en estado "pending" (en proceso) o "failed"
                                    // Si está "pending", el dinero YA está en movimiento - NO hacer rollback
                                    // Debemos PRESERVAR el registro para evitar doble refund
                                    
                                    // Actualizar el registro con el ID aunque no esté verificado
                                    createdRefundId = refund.Id;
                                    pendingRefundTx.StripeRefundId = refund.Id;
                                    servicePayment.IsRefunded = true;
                                    servicePayment.StripeRefundId = refund.Id;
                                    
                                    // COMMIT PARCIAL: El refund existe en Stripe, preservar registro
                                    if (transaction != null)
                                    {
                                        try
                                        {
                                            await _context.SaveChangesAsync();
                                            await transaction.CommitAsync();
                                            
                                            await _loggingService.LogWarningAsync(
                                                message: "Partial commit: Refund created but not verified - record preserved",
                                                details: $"SearchHire {searchHireId}: Refund {refund.Id} was created in Stripe but verification failed (may be pending). " +
                                                        $"Record preserved in DB to prevent double refund. Manual verification required.",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { RefundId = refund.Id }
                                            );
                                        }
                                        catch (Exception commitEx)
                                        {
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: Commit failed for unverified refund - refund exists in Stripe",
                                                details: $"SearchHire {searchHireId}: Refund {refund.Id} exists in Stripe but DB commit failed. MANUAL SYNC REQUIRED.",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { RefundId = refund.Id, Error = commitEx.Message }
                                            );
                                        }
                                    }
                                    
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Refund created but not verified - record preserved",
                                        details: $"SearchHire {searchHireId} finalization: refund {refund.Id} was created but not in 'succeeded' status. " +
                                                $"REFUND RECORD PRESERVED IN DB (may be pending in Stripe). " +
                                                $"PENDING ACTIONS: " +
                                                $"1) Verify refund {refund.Id} status in Stripe (may take time if pending) " +
                                                $"2) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) - NOT PROCESSED " +
                                                $"3) Platform retains {platformAmount:F2}€.",
                                        userId: initiatedByUserId ?? searchHire.ClientId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { 
                                            Status = statusValue,
                                            RefundId = refund.Id,
                                            ClientRefundAmount = clientRefundAmount,
                                            ExpertTransferAmount = expertAmount,
                                            PlatformAmount = platformAmount
                                        }
                                    );
                                    return false;
                                }
                                
                                // ✅ Actualizar transacción con ID de Stripe
                                createdRefundId = refund.Id;
                                pendingRefundTx.StripeRefundId = refund.Id;
                                servicePayment.IsRefunded = true;
                                servicePayment.StripeRefundId = refund.Id;
                                
                                // ✅ CRÍTICO: Manejar SaveChangesAsync después de Stripe con verificación en Stripe
                                try
                                {
                                    await _context.SaveChangesAsync(); // ✅ Actualizar con ID de Stripe
                                }
                                catch (Exception saveEx)
                                {
                                    // ✅ CRÍTICO: Si SaveChangesAsync falla después de Stripe, verificar en Stripe
                                    // El dinero ya se movió, NO hacer rollback sin verificar
                                    try
                                    {
                                        var refundSvcVerify = new RefundService();
                                        var refundInStripe = await refundSvcVerify.GetAsync(refund.Id);
                                        
                                        if (refundInStripe != null && refundInStripe.Status == "succeeded")
                                        {
                                            // ✅ Refund existe y está succeeded en Stripe
                                            // El dinero YA se movió, NO hacer rollback
                                            // Intentar actualizar BD manualmente o crear registro compensatorio
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: SaveChangesAsync failed after Stripe refund succeeded",
                                                details: $"SearchHire {searchHireId} finalization: Refund {refund.Id} was processed successfully in Stripe (status: {refundInStripe.Status}, amount: {refundInStripe.Amount / 100.0m}€) " +
                                                        $"but SaveChangesAsync failed when updating database. " +
                                                        $"MONEY ALREADY MOVED IN STRIPE - DO NOT ROLLBACK. " +
                                                        $"PENDING ACTION: Manually sync database with Stripe. " +
                                                        $"RefundId: {refund.Id}, ClientId: {searchHire.ClientId}, Amount: {clientRefundAmount}€. " +
                                                        $"SaveChangesAsync Error: {saveEx.Message}",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { 
                                                    Status = statusValue,
                                                    RefundId = refund.Id,
                                                    RefundStatusInStripe = refundInStripe.Status,
                                                    RefundAmountInStripe = refundInStripe.Amount / 100.0m,
                                                    ClientRefundAmount = clientRefundAmount,
                                                    SaveChangesError = saveEx.Message,
                                                    SaveChangesErrorType = saveEx.GetType().Name,
                                                    StackTrace = saveEx.StackTrace
                                                }
                                            );
                                            
                                            // Intentar actualizar BD de nuevo (último intento)
                                            try
                                            {
                                                // ✅ MEJORA: Verificar si existe en BD antes de actualizar
                                                var existingRefundTx = await _context.FinancialTransactions
                                                    .FirstOrDefaultAsync(ft => ft.Id == pendingRefundTx.Id);
                                                
                                                if (existingRefundTx != null)
                                                {
                                                    // Entidad existe, actualizar
                                                    existingRefundTx.StripeRefundId = refund.Id;
                                                    
                                                    // Verificar servicePayment en contexto
                                                    var existingServicePayment = await _context.FinancialTransactions
                                                        .FirstOrDefaultAsync(ft => ft.Id == servicePayment.Id);
                                                    
                                                    if (existingServicePayment != null)
                                                    {
                                                        existingServicePayment.IsRefunded = true;
                                                        existingServicePayment.StripeRefundId = refund.Id;
                                                    }
                                                    
                                                    await _context.SaveChangesAsync();
                                                }
                                                else
                                                {
                                                    // Entidad no existe, crear nueva
                                                    var newRefundTx = new FinancialTransaction
                                                    {
                                                        UserId = searchHire.ClientId,
                                                        Amount = clientRefundAmount,
                                                        TransactionType = "Refund",
                                                        RelatedEntityType = "SearchHire",
                                                        RelatedEntityId = searchHireId,
                                                        StripePaymentIntentId = servicePayment.StripePaymentIntentId,
                                                        StripeRefundId = refund.Id,
                                                        CreatedAt = DateTime.UtcNow
                                                    };
                                                    _context.FinancialTransactions.Add(newRefundTx);
                                                    
                                                    // Actualizar servicePayment si existe
                                                    var existingServicePayment = await _context.FinancialTransactions
                                                        .FirstOrDefaultAsync(ft => ft.Id == servicePayment.Id);
                                                    
                                                    if (existingServicePayment != null)
                                                    {
                                                        existingServicePayment.IsRefunded = true;
                                                        existingServicePayment.StripeRefundId = refund.Id;
                                                    }
                                                    
                                                    await _context.SaveChangesAsync();
                                                }
                                                
                                                // Si esto funciona, continuar normalmente
                                                await _loggingService.LogInfoAsync(
                                                    message: "Database sync successful after SaveChangesAsync failure",
                                                    details: $"Successfully synced database with Stripe refund {refund.Id} after initial SaveChangesAsync failure.",
                                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                    relatedEntityType: "SearchHire",
                                                    relatedEntityId: searchHireId
                                                );
                                            }
                                            catch (Exception retryEx)
                                            {
                                                // Si el retry también falla, el dinero ya está en Stripe pero no hay registro en BD
                                                // NO hacer rollback, solo log crítico para intervención manual
                                                await _loggingService.LogCriticalAsync(
                                                    message: "CRITICAL: Database sync failed after Stripe refund - manual intervention required",
                                                    details: $"SearchHire {searchHireId}: Refund {refund.Id} exists in Stripe but database sync failed. " +
                                                            $"MONEY ALREADY MOVED: Client received {clientRefundAmount}€ refund. " +
                                                            $"MANUAL ACTION REQUIRED: Update FinancialTransaction {pendingRefundTx.Id} with StripeRefundId = {refund.Id}. " +
                                                            $"Retry Error: {retryEx.Message}",
                                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                    relatedEntityType: "SearchHire",
                                                    relatedEntityId: searchHireId,
                                                    additionalData: new { 
                                                        RefundId = refund.Id,
                                                        FinancialTransactionId = pendingRefundTx.Id,
                                                        RetryError = retryEx.Message
                                                    }
                                                );
                                                
                                                // NO hacer rollback porque el dinero ya se movió
                                                // Retornar false para que se maneje manualmente
                                                return false;
                                            }
                                        }
                                        else
                                        {
                                            // Refund no existe o no está succeeded, puede hacer rollback
                                            _context.FinancialTransactions.Remove(pendingRefundTx);
                                            if (transaction != null)
                                            {
                                                await transaction.RollbackAsync();
                                            }
                                            
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: SaveChangesAsync failed and refund not verified in Stripe",
                                                details: $"SearchHire {searchHireId}: SaveChangesAsync failed and refund {refund.Id} verification in Stripe failed. " +
                                                        $"Refund status in Stripe: {(refundInStripe?.Status ?? "not found")}. " +
                                                        $"Rollback performed. Error: {saveEx.Message}",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { 
                                                    RefundId = refund.Id,
                                                    RefundStatusInStripe = refundInStripe?.Status,
                                                    SaveChangesError = saveEx.Message
                                                }
                                            );
                                            return false;
                                        }
                                    }
                                    catch (Exception verifyEx)
                                    {
                                        // Error al verificar en Stripe, asumir que el refund existe (más seguro)
                                        // NO hacer rollback porque no podemos verificar
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Cannot verify refund in Stripe after SaveChangesAsync failure",
                                            details: $"SearchHire {searchHireId}: SaveChangesAsync failed and cannot verify refund {refund.Id} in Stripe. " +
                                                    $"ASSUMING REFUND EXISTS - DO NOT ROLLBACK. " +
                                                    $"SaveChangesAsync Error: {saveEx.Message}, Verification Error: {verifyEx.Message}. " +
                                                    $"MANUAL ACTION REQUIRED: Verify refund {refund.Id} in Stripe and sync database.",
                                            userId: initiatedByUserId ?? searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { 
                                                RefundId = refund.Id,
                                                SaveChangesError = saveEx.Message,
                                                VerificationError = verifyEx.Message
                                            }
                                        );
                                        return false;
                                    }
                                }
                            }
                            catch (StripeException refundEx)
                            {
                                // Si el refund falla, eliminar la transacción pendiente
                                if (pendingRefundTx != null)
                                {
                                    _context.FinancialTransactions.Remove(pendingRefundTx);
                                }
                                
                                // ✅ CORRECCIÓN: Solo hacer rollback si creamos la transacción
                                if (transaction != null)
                                {
                                    await transaction.RollbackAsync();
                                }
                                
                                // 🚨 LOG CRÍTICO: Reembolso falló
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Refund failed - money distribution rolled back",
                                    details: $"SearchHire {searchHireId} finalization failed: refund to client failed. " +
                                            $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                            $"1) Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) - FAILED " +
                                            $"2) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) - NOT PROCESSED " +
                                            $"3) Platform retains {platformAmount:F2}€. " +
                                            $"RefundError: {refundEx.Message}",
                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { 
                                        Status = statusValue,
                                        ClientRefundAmount = clientRefundAmount,
                                        ExpertTransferAmount = expertAmount,
                                        PlatformAmount = platformAmount,
                                        ClientId = searchHire.ClientId,
                                        ExpertId = searchHire.ExpertId,
                                        RefundError = refundEx.Message
                                    }
                                );
                                
                                return false;
                            }
                        }

                        // ✅ PASO 2: Transfer DESPUÉS (solo si Refund fue exitoso o no se necesita)
                        if (needsTransfer)
                        {
                            try
                            {
                                var expertStripeAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId;
                                if (string.IsNullOrEmpty(expertStripeAccountId))
                                {
                                // 🚨 LOG CRÍTICO: Cuenta de Stripe del experto faltante
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Expert Stripe account missing - money distribution failed",
                                    details: $"SearchHire {searchHireId} finalization failed because Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) has no Stripe account configured. " +
                                            $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                            $"1) {(needsRefund && !string.IsNullOrEmpty(createdRefundId) ? $"Refund {clientRefundAmount:F2}€ to Client - ALREADY PROCESSED ✅" : $"Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId}")} " +
                                            $"2) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) - REQUIRES MANUAL SETUP " +
                                            $"3) Platform retains {platformAmount:F2}€. " +
                                            $"Configuration: Client {config.ClientPercentage}%, Expert {config.ExpertPercentage}%, Platform {config.PlatformPercentage}%",
                                    userId: searchHire.ExpertId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "Transfer",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { 
                                        Status = statusValue,
                                        ClientRefundAmount = clientRefundAmount,
                                        ExpertTransferAmount = expertAmount,
                                        PlatformAmount = platformAmount,
                                        ClientId = searchHire.ClientId,
                                        ExpertId = searchHire.ExpertId,
                                        ClientName = searchHire.Client?.Name,
                                        ExpertName = searchHire.Expert?.Name,
                                        ExpertStripeAccountId = expertStripeAccountId,
                                        RefundAlreadyProcessed = !string.IsNullOrEmpty(createdRefundId),
                                        RefundId = createdRefundId
                                    }
                                );
                                // ✅ CORRECCIÓN CRÍTICA DE SEGURIDAD: Si el refund ya se procesó, NO hacer rollback
                                // Hacer commit parcial para preservar el registro del refund
                                if (transaction != null)
                                {
                                    if (!string.IsNullOrEmpty(createdRefundId))
                                    {
                                        // Refund ya procesado - commit parcial
                                        try
                                        {
                                            await _context.SaveChangesAsync();
                                            await transaction.CommitAsync();
                                        }
                                        catch (Exception commitEx)
                                        {
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: Partial commit failed - refund already in Stripe",
                                                details: $"SearchHire {searchHireId}: Refund {createdRefundId} exists in Stripe but commit failed. MANUAL SYNC REQUIRED.",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { RefundId = createdRefundId, Error = commitEx.Message }
                                            );
                                        }
                                    }
                                    else
                                    {
                                        // No hay refund procesado - rollback seguro
                                        await transaction.RollbackAsync();
                                    }
                                }
                                return false;
                                }

                                // MODIFICACIÓN: Chequear status de connected account (best practice 2025 para cumplimiento)
                                var accountService = new AccountService();
                                var expertAccount = await accountService.GetAsync(expertStripeAccountId);
                                if (expertAccount.ChargesEnabled == false || expertAccount.PayoutsEnabled == false)
                                {
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Expert account not enabled for transfers",
                                        details: $"Expert {searchHire.ExpertId} account {expertStripeAccountId} is not fully verified. " +
                                                $"{(needsRefund && !string.IsNullOrEmpty(createdRefundId) ? $"Refund {createdRefundId} was already processed - PRESERVED." : "No refund processed.")}",
                                        userId: searchHire.ExpertId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "Account",
                                        relatedEntityId: (int)searchHire.ExpertId,
                                        additionalData: new { 
                                            AccountId = expertStripeAccountId, 
                                            ChargesEnabled = expertAccount.ChargesEnabled, 
                                            PayoutsEnabled = expertAccount.PayoutsEnabled,
                                            RefundAlreadyProcessed = !string.IsNullOrEmpty(createdRefundId),
                                            RefundId = createdRefundId
                                        }
                                    );
                                    // ✅ CORRECCIÓN CRÍTICA DE SEGURIDAD: Si el refund ya se procesó, NO hacer rollback
                                    if (transaction != null)
                                    {
                                        if (!string.IsNullOrEmpty(createdRefundId))
                                        {
                                            // Refund ya procesado - commit parcial
                                            try
                                            {
                                                await _context.SaveChangesAsync();
                                                await transaction.CommitAsync();
                                            }
                                            catch (Exception commitEx)
                                            {
                                                await _loggingService.LogCriticalAsync(
                                                    message: "CRITICAL: Partial commit failed - refund already in Stripe",
                                                    details: $"SearchHire {searchHireId}: Refund {createdRefundId} exists in Stripe but commit failed. MANUAL SYNC REQUIRED.",
                                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                    relatedEntityType: "SearchHire",
                                                    relatedEntityId: searchHireId,
                                                    additionalData: new { RefundId = createdRefundId, Error = commitEx.Message }
                                                );
                                            }
                                        }
                                        else
                                        {
                                            // No hay refund procesado - rollback seguro
                                            await transaction.RollbackAsync();
                                        }
                                    }
                                    return false;
                                }

                                // ✅ PATRÓN OUTBOX: Guardar en BD ANTES de llamar a Stripe (con estado "pending")
                                pendingTransferTx = new FinancialTransaction
                                {
                                    UserId = searchHire.ExpertId.Value,
                                    Amount = expertAmount,
                                    TransactionType = "Payout",
                                    RelatedEntityType = "SearchHire",
                                    RelatedEntityId = searchHireId,
                                    StripeTransferId = null, // Pendiente - se actualizará después
                                    CreatedAt = DateTime.UtcNow
                                };
                                _context.FinancialTransactions.Add(pendingTransferTx);
                                
                                // ✅ CRÍTICO: Verificar que SaveChangesAsync inicial sea exitoso antes de llamar a Stripe
                                try
                                {
                                    await _context.SaveChangesAsync(); // ✅ Guardar ANTES de Stripe
                                }
                                catch (Exception saveEx)
                                {
                                    // Si falla al guardar inicial, NO llamar a Stripe
                                    _context.FinancialTransactions.Remove(pendingTransferTx);
                                    
                                    // ✅ CORRECCIÓN CRÍTICA DE SEGURIDAD: Verificar si refund ya se procesó
                                    bool refundAlreadyInStripe = !string.IsNullOrEmpty(createdRefundId);
                                    
                                    if (transaction != null)
                                    {
                                        if (refundAlreadyInStripe)
                                        {
                                            // Refund ya procesado en Stripe - commit parcial para preservar registro
                                            try
                                            {
                                                await _context.SaveChangesAsync();
                                                await transaction.CommitAsync();
                                                
                                                await _loggingService.LogWarningAsync(
                                                    message: "Partial commit: Refund preserved after transfer save failure",
                                                    details: $"SearchHire {searchHireId}: Transfer save failed but refund {createdRefundId} preserved in DB.",
                                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                    relatedEntityType: "SearchHire",
                                                    relatedEntityId: searchHireId,
                                                    additionalData: new { RefundId = createdRefundId }
                                                );
                                            }
                                            catch (Exception commitEx)
                                            {
                                                await _loggingService.LogCriticalAsync(
                                                    message: "CRITICAL: Partial commit failed - refund already in Stripe",
                                                    details: $"SearchHire {searchHireId}: Refund {createdRefundId} exists in Stripe but commit failed. MANUAL SYNC REQUIRED.",
                                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                    relatedEntityType: "SearchHire",
                                                    relatedEntityId: searchHireId,
                                                    additionalData: new { RefundId = createdRefundId, Error = commitEx.Message }
                                                );
                                            }
                                        }
                                        else
                                        {
                                            // No hay refund procesado - rollback seguro
                                            if (pendingRefundTx != null)
                                            {
                                                _context.FinancialTransactions.Remove(pendingRefundTx);
                                            }
                                            await transaction.RollbackAsync();
                                        }
                                    }
                                    
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Failed to save pending transfer transaction before Stripe",
                                        details: $"SearchHire {searchHireId} finalization failed: Cannot proceed with Stripe transfer because SaveChangesAsync failed when saving pending transaction. " +
                                                $"No money was moved for transfer. Error: {saveEx.Message}. " +
                                                $"{(refundAlreadyInStripe ? $"Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) - ALREADY PROCESSED AND PRESERVED ✅" : "No refund processed")}. " +
                                                $"ACTION REQUIRED: Check database connectivity and retry transfer manually.",
                                        userId: initiatedByUserId ?? searchHire.ClientId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { 
                                            Status = statusValue,
                                            ExpertTransferAmount = expertAmount,
                                            RefundAlreadyProcessed = refundAlreadyInStripe,
                                            RefundId = createdRefundId,
                                            ErrorType = saveEx.GetType().Name,
                                            ErrorMessage = saveEx.Message,
                                            StackTrace = saveEx.StackTrace,
                                            InnerException = saveEx.InnerException?.Message
                                        }
                                    );
                                    return false;
                                }

                            var transferOptions = new TransferCreateOptions
                            {
                                Amount = (long)(expertAmount * 100),
                                Currency = "eur",
                                Destination = expertStripeAccountId,
                                Metadata = new Dictionary<string, string>
                                {
                                    { "searchHireId", searchHireId.ToString() },
                                    { "statusValue", statusValue },
                                    { "clientPercentage", config.ClientPercentage.ToString() },
                                    { "expertPercentage", config.ExpertPercentage.ToString() },
                                    { "platformPercentage", config.PlatformPercentage.ToString() },
                                    { "reason", reason },
                                    { "clientId", searchHire.ClientId.ToString() },
                                    { "expertId", searchHire.ExpertId?.ToString() ?? "N/A" },
                                    { "financialTransactionId", pendingTransferTx.Id.ToString() } // ✅ Trazabilidad
                                }
                            };

                            var transferRequestOptions = new RequestOptions
                            {
                                IdempotencyKey = transferIdempotencyKey
                            };

                            var transferSvc = new TransferService();

                            // Reintento simple para transients (hasta 3 veces)
                            Transfer transfer = null;
                            const int maxRetries = 3;
                            for (int attempt = 1; attempt <= maxRetries; attempt++)
                            {
                                try
                                {
                                    transfer = await transferSvc.CreateAsync(transferOptions, transferRequestOptions);
                                    break;
                                }
                                catch (StripeException ex) when ((int)ex.HttpStatusCode >= 500 || (int)ex.HttpStatusCode == 429) // Server errors or rate limits
                                {
                                    if (attempt == maxRetries)
                                        throw;
                                    await Task.Delay(1000 * attempt); // Exponential backoff simple
                                }
                            }
                            
                            // ✅ CRÍTICO: Verificar que el transfer esté en estado válido
                            bool transferVerified = await VerifyTransferStatusAsync(transfer.Id, searchHireId, initiatedByUserId);
                            if (!transferVerified)
                            {
                                // Si la verificación falla, eliminar la transacción pendiente del transfer
                                _context.FinancialTransactions.Remove(pendingTransferTx);
                                
                                // ✅ CORRECCIÓN CRÍTICA DE SEGURIDAD: Verificar si refund ya se procesó
                                bool refundAlreadyInStripeForVerify = !string.IsNullOrEmpty(createdRefundId);
                                
                                if (transaction != null)
                                {
                                    if (refundAlreadyInStripeForVerify)
                                    {
                                        // Refund ya procesado en Stripe - commit parcial para preservar registro
                                        try
                                        {
                                            await _context.SaveChangesAsync();
                                            await transaction.CommitAsync();
                                            
                                            await _loggingService.LogWarningAsync(
                                                message: "Partial commit: Refund preserved after transfer verification failure",
                                                details: $"SearchHire {searchHireId}: Transfer {transfer.Id} verification failed but refund {createdRefundId} preserved in DB.",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { RefundId = createdRefundId, TransferId = transfer.Id }
                                            );
                                        }
                                        catch (Exception commitEx)
                                        {
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: Partial commit failed - refund already in Stripe",
                                                details: $"SearchHire {searchHireId}: Refund {createdRefundId} exists in Stripe but commit failed. MANUAL SYNC REQUIRED.",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { RefundId = createdRefundId, Error = commitEx.Message }
                                            );
                                        }
                                    }
                                    else
                                    {
                                        // No hay refund procesado - rollback seguro
                                        if (pendingRefundTx != null)
                                        {
                                            _context.FinancialTransactions.Remove(pendingRefundTx);
                                        }
                                        await transaction.RollbackAsync();
                                    }
                                }
                                
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Transfer not verified - partial money distribution",
                                    details: $"SearchHire {searchHireId} finalization: transfer {transfer.Id} was created but verification failed. " +
                                            $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                            $"1) Verify transfer {transfer.Id} status in Stripe " +
                                            $"2) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) " +
                                            $"3) Platform retains {platformAmount:F2}€. " +
                                            $"{(refundAlreadyInStripeForVerify ? $"Refund {clientRefundAmount:F2}€ to Client - PRESERVED IN DB ✅" : "No refund processed")}",
                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { 
                                        Status = statusValue,
                                        TransferId = transfer.Id,
                                        ClientRefundAmount = clientRefundAmount,
                                        ExpertTransferAmount = expertAmount,
                                        PlatformAmount = platformAmount,
                                        RefundAlreadyProcessed = refundAlreadyInStripeForVerify,
                                        RefundId = createdRefundId
                                    }
                                );
                                return false;
                            }
                            
                            // ✅ Actualizar transacción con ID de Stripe
                            createdTransferId = transfer.Id;
                            pendingTransferTx.StripeTransferId = transfer.Id;
                            
                            // ✅ CRÍTICO: Manejar SaveChangesAsync después de Stripe con verificación en Stripe
                            try
                            {
                                await _context.SaveChangesAsync(); // ✅ Actualizar con ID de Stripe
                            }
                            catch (Exception saveEx)
                            {
                                // ✅ CRÍTICO: Si SaveChangesAsync falla después de Stripe, verificar en Stripe
                                // El dinero ya se movió, NO hacer rollback sin verificar
                                try
                                {
                                    var transferSvcVerify = new TransferService();
                                    var transferInStripe = await transferSvcVerify.GetAsync(transfer.Id);
                                    
                                    // ✅ CORRECCIÓN: En Stripe.NET, Transfer no tiene Status. Se verifica si existe y no está revertido
                                    if (transferInStripe != null && !transferInStripe.Reversed)
                                    {
                                        // ✅ Transfer existe y no está revertido en Stripe
                                        // El dinero YA se movió, NO hacer rollback
                                        // Intentar actualizar BD manualmente o crear registro compensatorio
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: SaveChangesAsync failed after Stripe transfer succeeded",
                                            details: $"SearchHire {searchHireId} finalization: Transfer {transfer.Id} was processed successfully in Stripe (amount: {transferInStripe.Amount / 100.0m}€, reversed: {transferInStripe.Reversed}) " +
                                                    $"but SaveChangesAsync failed when updating database. " +
                                                    $"MONEY ALREADY MOVED IN STRIPE - DO NOT ROLLBACK. " +
                                                    $"PENDING ACTION: Manually sync database with Stripe. " +
                                                    $"TransferId: {transfer.Id}, ExpertId: {searchHire.ExpertId}, Amount: {expertAmount}€. " +
                                                    $"{(needsRefund && !string.IsNullOrEmpty(createdRefundId) ? $"Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) - ALREADY PROCESSED ✅" : "No refund needed")}. " +
                                                    $"SaveChangesAsync Error: {saveEx.Message}",
                                            userId: initiatedByUserId ?? searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { 
                                                Status = statusValue,
                                                TransferId = transfer.Id,
                                                TransferReversed = transferInStripe.Reversed,
                                                TransferAmountInStripe = transferInStripe.Amount / 100.0m,
                                                ExpertTransferAmount = expertAmount,
                                                RefundAlreadyProcessed = needsRefund && !string.IsNullOrEmpty(createdRefundId),
                                                RefundId = createdRefundId,
                                                SaveChangesError = saveEx.Message,
                                                SaveChangesErrorType = saveEx.GetType().Name,
                                                StackTrace = saveEx.StackTrace
                                            }
                                        );
                                        
                                        // Intentar actualizar BD de nuevo (último intento)
                                        try
                                        {
                                            // ✅ MEJORA: Verificar si existe en BD antes de actualizar
                                            var existingTransferTx = await _context.FinancialTransactions
                                                .FirstOrDefaultAsync(ft => ft.Id == pendingTransferTx.Id);
                                            
                                            if (existingTransferTx != null)
                                            {
                                                // Entidad existe, actualizar
                                                existingTransferTx.StripeTransferId = transfer.Id;
                                                await _context.SaveChangesAsync();
                                            }
                                            else
                                            {
                                                // Entidad no existe, crear nueva
                                                var newTransferTx = new FinancialTransaction
                                                {
                                                    UserId = searchHire.ExpertId.Value,
                                                    Amount = expertAmount,
                                                    TransactionType = "Payout",
                                                    RelatedEntityType = "SearchHire",
                                                    RelatedEntityId = searchHireId,
                                                    StripeTransferId = transfer.Id,
                                                    CreatedAt = DateTime.UtcNow
                                                };
                                                _context.FinancialTransactions.Add(newTransferTx);
                                                await _context.SaveChangesAsync();
                                            }
                                            
                                            // Si esto funciona, continuar normalmente
                                            await _loggingService.LogInfoAsync(
                                                message: "Database sync successful after SaveChangesAsync failure",
                                                details: $"Successfully synced database with Stripe transfer {transfer.Id} after initial SaveChangesAsync failure.",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId
                                            );
                                        }
                                        catch (Exception retryEx)
                                        {
                                            // Si el retry también falla, el dinero ya está en Stripe pero no hay registro en BD
                                            // NO hacer rollback, solo log crítico para intervención manual
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: Database sync failed after Stripe transfer - manual intervention required",
                                                details: $"SearchHire {searchHireId}: Transfer {transfer.Id} exists in Stripe but database sync failed. " +
                                                        $"MONEY ALREADY MOVED: Expert received {expertAmount}€ transfer. " +
                                                        $"MANUAL ACTION REQUIRED: Update FinancialTransaction {pendingTransferTx.Id} with StripeTransferId = {transfer.Id}. " +
                                                        $"{(needsRefund && !string.IsNullOrEmpty(createdRefundId) ? $"Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) - ALREADY PROCESSED ✅" : "No refund needed")}. " +
                                                        $"Retry Error: {retryEx.Message}",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { 
                                                    TransferId = transfer.Id,
                                                    FinancialTransactionId = pendingTransferTx.Id,
                                                    RefundAlreadyProcessed = needsRefund && !string.IsNullOrEmpty(createdRefundId),
                                                    RefundId = createdRefundId,
                                                    RetryError = retryEx.Message
                                                }
                                            );
                                            
                                            // NO hacer rollback porque el dinero ya se movió
                                            // Retornar false para que se maneje manualmente
                                            return false;
                                        }
                                    }
                                    else
                                    {
                                        // Transfer no existe o no está paid, puede hacer rollback
                                        _context.FinancialTransactions.Remove(pendingTransferTx);
                                        
                                        // Si ya se procesó el refund, NO hacer rollback completo
                                        if (transaction != null && !needsRefund)
                                        {
                                            await transaction.RollbackAsync();
                                        }
                                        else if (transaction != null && pendingRefundTx != null)
                                        {
                                            _context.FinancialTransactions.Remove(pendingRefundTx);
                                            await transaction.RollbackAsync();
                                        }
                                        
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: SaveChangesAsync failed and transfer not verified in Stripe",
                                            details: $"SearchHire {searchHireId}: SaveChangesAsync failed and transfer {transfer.Id} verification in Stripe failed. " +
                                                    $"Transfer in Stripe: {(transferInStripe != null ? $"exists, reversed: {transferInStripe.Reversed}" : "not found")}. " +
                                                    $"Rollback performed. Error: {saveEx.Message}",
                                            userId: initiatedByUserId ?? searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { 
                                                TransferId = transfer.Id,
                                                TransferReversed = transferInStripe?.Reversed ?? false,
                                                SaveChangesError = saveEx.Message
                                            }
                                        );
                                        return false;
                                    }
                                }
                                catch (Exception verifyEx)
                                {
                                    // Error al verificar en Stripe, asumir que el transfer existe (más seguro)
                                    // NO hacer rollback porque no podemos verificar
                                        await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Cannot verify transfer in Stripe after SaveChangesAsync failure",
                                        details: $"SearchHire {searchHireId}: SaveChangesAsync failed and cannot verify transfer {transfer.Id} in Stripe. " +
                                                $"ASSUMING TRANSFER EXISTS - DO NOT ROLLBACK. " +
                                                $"SaveChangesAsync Error: {saveEx.Message}, Verification Error: {verifyEx.Message}. " +
                                                $"{(needsRefund && !string.IsNullOrEmpty(createdRefundId) ? $"Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) - ALREADY PROCESSED ✅" : "No refund needed")}. " +
                                                $"MANUAL ACTION REQUIRED: Verify transfer {transfer.Id} in Stripe and sync database.",
                                            userId: initiatedByUserId ?? searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { 
                                            TransferId = transfer.Id,
                                            RefundAlreadyProcessed = needsRefund && !string.IsNullOrEmpty(createdRefundId),
                                            RefundId = createdRefundId,
                                            SaveChangesError = saveEx.Message,
                                            VerificationError = verifyEx.Message
                                        }
                                    );
                                    return false;
                                }
                            }
                            }
                            catch (StripeException transferEx)
                            {
                                // Si el transfer falla, eliminar la transacción pendiente del transfer
                                if (pendingTransferTx != null)
                                {
                                    _context.FinancialTransactions.Remove(pendingTransferTx);
                                }
                                
                                // ✅ CORRECCIÓN CRÍTICA DE SEGURIDAD: Si el refund YA se procesó en Stripe,
                                // NO hacer rollback - hacer COMMIT parcial para mantener registro del refund
                                // Esto previene doble refund y pérdida de trazabilidad
                                bool refundWasProcessed = !string.IsNullOrEmpty(createdRefundId);
                                
                                if (transaction != null)
                                {
                                    if (refundWasProcessed)
                                    {
                                        // ✅ COMMIT PARCIAL: Refund ya procesado en Stripe, guardar su registro
                                        // Solo el transfer pendiente fue eliminado del contexto
                                        try
                                        {
                                            await _context.SaveChangesAsync(); // Guardar estado actual (sin transfer pendiente)
                                            await transaction.CommitAsync();
                                            
                                            await _loggingService.LogWarningAsync(
                                                message: "Partial commit: Refund saved, transfer failed",
                                                details: $"SearchHire {searchHireId}: Refund {createdRefundId} was committed to DB because it was already processed in Stripe. " +
                                                        $"Transfer failed and was NOT saved. Manual intervention needed for transfer.",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { RefundId = createdRefundId, TransferError = transferEx.Message }
                                            );
                                        }
                                        catch (Exception commitEx)
                                        {
                                            // Si el commit falla, log crítico pero NO hacer rollback (dinero ya se movió)
                                            await _loggingService.LogCriticalAsync(
                                                message: "CRITICAL: Failed to commit partial transaction after refund",
                                                details: $"SearchHire {searchHireId}: Refund {createdRefundId} exists in Stripe but commit failed. " +
                                                        $"MONEY ALREADY MOVED. Manual sync required. CommitError: {commitEx.Message}",
                                                userId: initiatedByUserId ?? searchHire.ClientId,
                                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                                relatedEntityType: "SearchHire",
                                                relatedEntityId: searchHireId,
                                                additionalData: new { RefundId = createdRefundId, CommitError = commitEx.Message }
                                            );
                                        }
                                    }
                                    else
                                    {
                                        // No hay refund procesado, hacer rollback completo seguro
                                        if (pendingRefundTx != null)
                                        {
                                            _context.FinancialTransactions.Remove(pendingRefundTx);
                                        }
                                        await transaction.RollbackAsync();
                                    }
                                }
                                
                                // 🚨 LOG CRÍTICO: Transfer falló
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL: Transfer failed - money distribution partially processed",
                                    details: $"SearchHire {searchHireId} finalization failed: transfer to expert failed. " +
                                            $"PENDING TRANSACTIONS TO COMPLETE MANUALLY: " +
                                            $"1) Transfer {expertAmount:F2}€ to Expert {searchHire.ExpertId} ({searchHire.Expert?.Name}) - FAILED " +
                                            $"2) {(needsRefund && !string.IsNullOrEmpty(createdRefundId) ? $"Refund {clientRefundAmount:F2}€ to Client {searchHire.ClientId} ({searchHire.Client?.Name}) - ALREADY PROCESSED ✅" : "No refund needed")} " +
                                            $"3) Platform retains {platformAmount:F2}€. " +
                                            $"TransferError: {transferEx.Message}",
                                    userId: initiatedByUserId ?? searchHire.ClientId,
                                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                    relatedEntityType: "SearchHire",
                                    relatedEntityId: searchHireId,
                                    additionalData: new { 
                                        Status = statusValue,
                                        ClientRefundAmount = clientRefundAmount,
                                        ExpertTransferAmount = expertAmount,
                                        PlatformAmount = platformAmount,
                                        ClientId = searchHire.ClientId,
                                        ExpertId = searchHire.ExpertId,
                                        TransferError = transferEx.Message,
                                        RefundAlreadyProcessed = needsRefund && !string.IsNullOrEmpty(createdRefundId),
                                        RefundId = createdRefundId
                                    }
                                );
                                
                                return false;
                            }
                        }

                        // ✅ PATRÓN OUTBOX: Las transacciones ya están guardadas en BD antes de Stripe
                        // Solo necesitamos hacer commit de la transacción si todo fue exitoso
                        // (Las actualizaciones con IDs de Stripe ya se hicieron en los bloques anteriores)
                        
                        // Verificar que todas las operaciones necesarias fueron exitosas
                        bool allOperationsSucceeded = true;
                        bool finalRefundCompleted = !string.IsNullOrEmpty(createdRefundId);
                        bool finalTransferCompleted = !string.IsNullOrEmpty(createdTransferId);
                        
                        if (needsRefund && !finalRefundCompleted)
                        {
                            allOperationsSucceeded = false;
                        }
                        if (needsTransfer && !finalTransferCompleted)
                        {
                            allOperationsSucceeded = false;
                        }
                        
                        if (!allOperationsSucceeded)
                        {
                            // ✅ CORRECCIÓN DE SEGURIDAD: Si alguna operación YA se procesó en Stripe,
                            // NO hacer rollback completo - hacer commit parcial para preservar registros
                            if (transaction != null)
                            {
                                if (finalRefundCompleted || finalTransferCompleted)
                                {
                                    // Commit parcial: guardar lo que sí se procesó
                                    try
                                    {
                                        // Eliminar transacciones pendientes que no se completaron
                                        if (pendingRefundTx != null && !finalRefundCompleted)
                                        {
                                            _context.FinancialTransactions.Remove(pendingRefundTx);
                                        }
                                        if (pendingTransferTx != null && !finalTransferCompleted)
                                        {
                                            _context.FinancialTransactions.Remove(pendingTransferTx);
                                        }
                                        
                                        await _context.SaveChangesAsync();
                                        await transaction.CommitAsync();
                                        
                                        await _loggingService.LogWarningAsync(
                                            message: "Partial commit: Some operations succeeded",
                                            details: $"SearchHire {searchHireId}: Committed partial results after validation. " +
                                                    $"Refund: {(finalRefundCompleted ? $"SAVED ({createdRefundId})" : "not processed")}, " +
                                                    $"Transfer: {(finalTransferCompleted ? $"SAVED ({createdTransferId})" : "not processed")}.",
                                            userId: initiatedByUserId ?? searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { RefundId = createdRefundId, TransferId = createdTransferId }
                                        );
                                    }
                                    catch (Exception commitEx)
                                    {
                                        await _loggingService.LogCriticalAsync(
                                            message: "CRITICAL: Partial commit failed - money already moved",
                                            details: $"SearchHire {searchHireId}: Commit failed but money already moved. " +
                                                    $"RefundInStripe: {createdRefundId ?? "none"}, TransferInStripe: {createdTransferId ?? "none"}. " +
                                                    $"MANUAL SYNC REQUIRED. Error: {commitEx.Message}",
                                            userId: initiatedByUserId ?? searchHire.ClientId,
                                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                            relatedEntityType: "SearchHire",
                                            relatedEntityId: searchHireId,
                                            additionalData: new { RefundId = createdRefundId, TransferId = createdTransferId, Error = commitEx.Message }
                                        );
                                    }
                                }
                                else
                                {
                                    // Ninguna operación se completó - rollback seguro
                                    await transaction.RollbackAsync();
                                }
                            }
                            return false;
                        }
                        
                        // ✅ CORRECCIÓN: Solo hacer commit si creamos la transacción
                        if (transaction != null)
                        {
                        await transaction.CommitAsync();
                        }

                        // ✅ Notificar a usuarios sobre movimientos de dinero exitosos
                        if (needsRefund && !string.IsNullOrEmpty(createdRefundId))
                        {
                            // Refund exitoso - notificar al cliente
                            // ✅ CORRECCIÓN: Mostrar monto BRUTO (lo que el cliente realmente recibe)
                            await _loggingService.LogInfoAsync(
                                message: "Reembolso procesado",
                                details: $"Se procesó tu reembolso de {clientRefundAmountForStripe:F2}€ por el servicio #{searchHireId}. El dinero llegará a tu cuenta en 5-10 días hábiles.",
                                userId: searchHire.ClientId,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                notifyUser: true
                            );
                        }

                        if (needsTransfer && !string.IsNullOrEmpty(createdTransferId) && searchHire.ExpertId.HasValue)
                        {
                            // Transfer exitoso - notificar al experto
                            await _loggingService.LogInfoAsync(
                                message: "Pago recibido",
                                details: $"Has recibido {expertAmount:F2}€ por el servicio #{searchHireId}. El dinero está disponible en tu cuenta de Stripe.",
                                userId: searchHire.ExpertId.Value,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                notifyUser: true
                            );
                        }

                        return true;
                    }
                    catch (StripeException ex)
                    {
                        // ✅ CORRECCIÓN CRÍTICA DE SEGURIDAD: Manejar rollback según qué operaciones ya se procesaron
                        // Si el Refund ya se procesó en Stripe, NO hacer rollback - hacer COMMIT parcial
                        bool refundProcessedInStripe = !string.IsNullOrEmpty(createdRefundId);
                        bool transferProcessedInStripe = !string.IsNullOrEmpty(createdTransferId);
                        
                        // Eliminar transacciones pendientes que NO se completaron en Stripe
                        if (pendingRefundTx != null && !refundProcessedInStripe)
                        {
                            _context.FinancialTransactions.Remove(pendingRefundTx);
                        }
                        if (pendingTransferTx != null && !transferProcessedInStripe)
                        {
                            _context.FinancialTransactions.Remove(pendingTransferTx);
                        }
                        
                        if (transaction != null)
                        {
                            if (refundProcessedInStripe || transferProcessedInStripe)
                            {
                                // ✅ COMMIT PARCIAL: Al menos una operación ya se procesó en Stripe
                                // Guardar registros de las operaciones completadas para evitar duplicados
                                try
                                {
                                    await _context.SaveChangesAsync();
                                    await transaction.CommitAsync();
                                    
                                    await _loggingService.LogWarningAsync(
                                        message: "Partial commit after StripeException: processed operations saved",
                                        details: $"SearchHire {searchHireId}: Committed partial results. " +
                                                $"Refund: {(refundProcessedInStripe ? $"SAVED ({createdRefundId})" : "not processed")}, " +
                                                $"Transfer: {(transferProcessedInStripe ? $"SAVED ({createdTransferId})" : "not processed")}. " +
                                                $"StripeError: {ex.Message}",
                                        userId: initiatedByUserId ?? searchHire.ClientId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { 
                                            RefundId = createdRefundId, 
                                            TransferId = createdTransferId,
                                            StripeError = ex.Message 
                                        }
                                    );
                                }
                                catch (Exception commitEx)
                                {
                                    // Si commit falla, el dinero ya se movió en Stripe - log crítico pero NO rollback
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Partial commit failed - money already moved in Stripe",
                                        details: $"SearchHire {searchHireId}: Commit failed but money already moved. " +
                                                $"RefundInStripe: {createdRefundId ?? "none"}, TransferInStripe: {createdTransferId ?? "none"}. " +
                                                $"MANUAL SYNC REQUIRED. CommitError: {commitEx.Message}",
                                        userId: initiatedByUserId ?? searchHire.ClientId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { 
                                            RefundId = createdRefundId, 
                                            TransferId = createdTransferId,
                                            CommitError = commitEx.Message 
                                        }
                                    );
                                }
                            }
                            else
                            {
                                // Ninguna operación se completó en Stripe - rollback seguro
                                await transaction.RollbackAsync();
                            }
                        }
                        
                        // ✅ MEJORA GROK: Notificar al experto si hay error de Stripe (estado ya está cambiado)
                        if (searchHire.ExpertId.HasValue)
                        {
                            await _loggingService.LogCriticalAsync(
                                message: "CRITICAL: Stripe error - state already updated",
                                details: $"El estado del servicio #{searchHireId} se actualizó correctamente, pero hubo un error al procesar el pago. " +
                                        $"Error de Stripe: {ex.Message}. " +
                                        $"Se requiere procesamiento manual del pago. " +
                                        $"Plan de distribución: Cliente={clientRefundAmount:F2}€ ({config.ClientPercentage}%), Experto={expertAmount:F2}€ ({config.ExpertPercentage}%), Plataforma={platformAmount:F2}€ ({config.PlatformPercentage}%). " +
                                        $"Estado: {statusValue}, Razón: {reason}. " +
                                        $"Transfer={(transferProcessedInStripe ? $"Creado ({createdTransferId}) ✅" : "No intentado/Falló")}, Refund={(refundProcessedInStripe ? $"Creado ({createdRefundId}) ✅" : "No intentado/Falló")}.",
                                userId: searchHire.ExpertId.Value,
                                source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHireId,
                                notifyUser: true, // ✅ Notificar al experto
                                additionalData: new { 
                                    Status = statusValue,
                                    Reason = reason,
                                    ClientRefundAmount = clientRefundAmount,
                                    ExpertTransferAmount = expertAmount,
                                    PlatformAmount = platformAmount,
                                    PaymentIntentId = servicePayment.StripePaymentIntentId,
                                    ExpertAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId,
                                    CreatedTransferId = createdTransferId,
                                    CreatedRefundId = createdRefundId,
                                    StripeError = ex.Message,
                                    StripeErrorType = ex.StripeError?.Type,
                                    StripeErrorCode = ex.StripeError?.Code,
                                    RefundProcessedInStripe = refundProcessedInStripe,
                                    TransferProcessedInStripe = transferProcessedInStripe
                                }
                            );
                        }
                        
                        // 🚨 LOG CRÍTICO: Error de Stripe durante distribución (una sola vez, con información completa)
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Stripe exception during money distribution transaction",
                            details: $"Stripe exception occurred during money distribution transaction for SearchHire {searchHireId}. " +
                                    $"Distribution Plan: Client={clientRefundAmount}€ ({config.ClientPercentage}%), Expert={expertAmount}€ ({config.ExpertPercentage}%), Platform={platformAmount}€ ({config.PlatformPercentage}%). " +
                                    $"Stripe Error: {ex.Message}, Type: {ex.StripeError?.Type}, Code: {ex.StripeError?.Code}, DeclineCode: {ex.StripeError?.DeclineCode}, Param: {ex.StripeError?.Param}. " +
                                    $"PaymentIntentId: {servicePayment.StripePaymentIntentId}, ExpertAccountId: {searchHire.Expert?.ExpertProfile?.StripeAccountId}. " +
                                    $"Transaction Status: Transfer={(transferProcessedInStripe ? $"Created ({createdTransferId}) ✅" : "Not attempted/Failed")}, Refund={(refundProcessedInStripe ? $"Created ({createdRefundId}) ✅" : "Not attempted/Failed")}. " +
                                    $"NOTE: State was already updated in Phase 2. ACTION REQUIRED: Review Stripe error details and retry distribution if applicable. " +
                                    $"{(refundProcessedInStripe ? "Refund was already processed in Stripe - do NOT retry refund." : "")} " +
                                    $"{(transferProcessedInStripe ? "Transfer was already processed in Stripe - do NOT retry transfer." : "")}",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Reason = reason,
                                ClientRefundAmount = clientRefundAmount,
                                ExpertTransferAmount = expertAmount,
                                PlatformAmount = platformAmount,
                                PaymentIntentId = servicePayment.StripePaymentIntentId,
                                ExpertAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId,
                                CreatedTransferId = createdTransferId,
                                CreatedRefundId = createdRefundId,
                                StripeError = ex.Message,
                                StripeErrorType = ex.StripeError?.Type,
                                StripeErrorCode = ex.StripeError?.Code,
                                StripeDeclineCode = ex.StripeError?.DeclineCode,
                                StripeParam = ex.StripeError?.Param,
                                RefundProcessedInStripe = refundProcessedInStripe,
                                TransferProcessedInStripe = transferProcessedInStripe
                            }
                        );
                        return false;
                    }
                    catch (Exception ex)
                    {
                        // ✅ CORRECCIÓN CRÍTICA DE SEGURIDAD: Manejar rollback según qué operaciones ya se procesaron
                        // Si alguna operación ya se procesó en Stripe, NO hacer rollback - hacer COMMIT parcial
                        bool refundProcessedInStripe = !string.IsNullOrEmpty(createdRefundId);
                        bool transferProcessedInStripe = !string.IsNullOrEmpty(createdTransferId);
                        
                        // Eliminar transacciones pendientes que NO se completaron en Stripe
                        if (pendingRefundTx != null && !refundProcessedInStripe)
                        {
                            _context.FinancialTransactions.Remove(pendingRefundTx);
                        }
                        if (pendingTransferTx != null && !transferProcessedInStripe)
                        {
                            _context.FinancialTransactions.Remove(pendingTransferTx);
                        }
                        
                        if (transaction != null)
                        {
                            if (refundProcessedInStripe || transferProcessedInStripe)
                            {
                                // ✅ COMMIT PARCIAL: Al menos una operación ya se procesó en Stripe
                                // Guardar registros de las operaciones completadas para evitar duplicados
                                try
                                {
                                    await _context.SaveChangesAsync();
                                    await transaction.CommitAsync();
                                    
                                    await _loggingService.LogWarningAsync(
                                        message: "Partial commit after Exception: processed operations saved",
                                        details: $"SearchHire {searchHireId}: Committed partial results. " +
                                                $"Refund: {(refundProcessedInStripe ? $"SAVED ({createdRefundId})" : "not processed")}, " +
                                                $"Transfer: {(transferProcessedInStripe ? $"SAVED ({createdTransferId})" : "not processed")}. " +
                                                $"Error: {ex.Message}",
                                        userId: initiatedByUserId ?? searchHire.ClientId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { 
                                            RefundId = createdRefundId, 
                                            TransferId = createdTransferId,
                                            Error = ex.Message 
                                        }
                                    );
                                }
                                catch (Exception commitEx)
                                {
                                    // Si commit falla, el dinero ya se movió en Stripe - log crítico pero NO rollback
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL: Partial commit failed - money already moved in Stripe",
                                        details: $"SearchHire {searchHireId}: Commit failed but money already moved. " +
                                                $"RefundInStripe: {createdRefundId ?? "none"}, TransferInStripe: {createdTransferId ?? "none"}. " +
                                                $"MANUAL SYNC REQUIRED. CommitError: {commitEx.Message}",
                                        userId: initiatedByUserId ?? searchHire.ClientId,
                                        source: "StripeRefundService.ProcessMoneyDistributionAsync",
                                        relatedEntityType: "SearchHire",
                                        relatedEntityId: searchHireId,
                                        additionalData: new { 
                                            RefundId = createdRefundId, 
                                            TransferId = createdTransferId,
                                            CommitError = commitEx.Message 
                                        }
                                    );
                                }
                            }
                            else
                            {
                                // Ninguna operación se completó en Stripe - rollback seguro
                                await transaction.RollbackAsync();
                            }
                        }
                        
                        // 🚨 LOG CRÍTICO: Error general durante distribución (una sola vez, con información completa)
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL: Unexpected exception during money distribution transaction",
                            details: $"An unexpected exception occurred during money distribution transaction for SearchHire {searchHireId}. " +
                                    $"Distribution Plan: Client={clientRefundAmount}€ ({config.ClientPercentage}%), Expert={expertAmount}€ ({config.ExpertPercentage}%), Platform={platformAmount}€ ({config.PlatformPercentage}%). " +
                                    $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                    $"PaymentIntentId: {servicePayment.StripePaymentIntentId}, ExpertAccountId: {searchHire.Expert?.ExpertProfile?.StripeAccountId}. " +
                                    $"Transaction Status: Transfer={(transferProcessedInStripe ? $"Created ({createdTransferId}) ✅" : "Not attempted/Failed")}, Refund={(refundProcessedInStripe ? $"Created ({createdRefundId}) ✅" : "Not attempted/Failed")}. " +
                                    $"Stack Trace: {ex.StackTrace}. " +
                                    $"ACTION REQUIRED: Review exception details. " +
                                    $"{(refundProcessedInStripe ? "Refund was already processed in Stripe - do NOT retry refund." : "")} " +
                                    $"{(transferProcessedInStripe ? "Transfer was already processed in Stripe - do NOT retry transfer." : "")}",
                            userId: initiatedByUserId ?? searchHire.ClientId,
                            source: "StripeRefundService.ProcessMoneyDistributionAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId,
                            additionalData: new { 
                                Status = statusValue,
                                Reason = reason,
                                ClientRefundAmount = clientRefundAmount,
                                ExpertTransferAmount = expertAmount,
                                PlatformAmount = platformAmount,
                                PaymentIntentId = servicePayment.StripePaymentIntentId,
                                ExpertAccountId = searchHire.Expert?.ExpertProfile?.StripeAccountId,
                                CreatedTransferId = createdTransferId,
                                CreatedRefundId = createdRefundId,
                                ErrorType = ex.GetType().Name,
                                ErrorMessage = ex.Message,
                                StackTrace = ex.StackTrace,
                                InnerException = ex.InnerException?.Message,
                                RefundProcessedInStripe = refundProcessedInStripe,
                                TransferProcessedInStripe = transferProcessedInStripe
                            }
                        );
                        return false;
                    }
                };
                
                // ✅ Si no hay transacción existente, usar estrategia de reintento
                if (existingTransactionForMoney == null)
                {
                    var strategy = _context.Database.CreateExecutionStrategy();
                    return await strategy.ExecuteAsync(ProcessMoneyAsync);
                }
                else
                {
                    // ✅ Usar transacción existente - ejecutar directamente sin estrategia de reintento
                    // (el reintento se maneja a nivel de la transacción global)
                    return await ProcessMoneyAsync();
                }
            }
            catch (Exception ex)
            {
                // 🚨 LOG CRÍTICO: Error general fuera de la transacción (una sola vez, con información completa)
                // Este error ocurre ANTES de entrar en la transacción, por lo que no hay datos de distribución calculados
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: ProcessMoneyDistributionAsync failed - outer catch",
                    details: $"An unexpected exception occurred in ProcessMoneyDistributionAsync before entering transaction for SearchHire {searchHireId}. " +
                            $"Status: {statusValue}, Reason: {reason}, InitiatedByUserId: {initiatedByUserId}. " +
                            $"Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                            $"Stack Trace: {ex.StackTrace}. " +
                            $"ACTION REQUIRED: Review error - this indicates a pre-transaction validation, data loading, or configuration issue.",
                    userId: initiatedByUserId,
                    source: "StripeRefundService.ProcessMoneyDistributionAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { 
                        Status = statusValue,
                        Reason = reason,
                        InitiatedByUserId = initiatedByUserId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );
                return false;
            }
        }

        private static AppointmentStatus? MapAppointmentStatus(string statusValue)
        {
            if (string.IsNullOrWhiteSpace(statusValue))
            {
                return null;
            }
            try
            {
                return AppointmentStatusExtensions.FromStringValue(statusValue);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Verifica que un Refund de Stripe esté en estado "succeeded" o "pending"
        /// </summary>
        private async Task<bool> VerifyRefundStatusAsync(string refundId, int searchHireId, int? initiatedByUserId)
        {
            try
            {
                var refundService = new RefundService();
                var refund = await refundService.GetAsync(refundId);
                
                // Refund puede estar en: succeeded, pending, failed, canceled
                if (refund.Status == "succeeded")
                {
                    return true;
                }
                else if (refund.Status == "pending")
                {
                    // Esperar un poco y verificar de nuevo (máximo 3 intentos)
                    for (int i = 0; i < 3; i++)
                    {
                        await Task.Delay(2000); // Esperar 2 segundos
                        refund = await refundService.GetAsync(refundId);
                        if (refund.Status == "succeeded")
                        {
                            return true;
                        }
                        if (refund.Status == "failed" || refund.Status == "canceled")
                        {
                            break;
                        }
                    }
                }
                
                // Si llegamos aquí, el refund no está en estado succeeded
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Refund not in succeeded status",
                    details: $"Refund {refundId} for SearchHire {searchHireId} is in status '{refund.Status}' instead of 'succeeded'.",
                    userId: initiatedByUserId ?? 0,
                    source: "StripeRefundService.VerifyRefundStatusAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { RefundId = refundId, Status = refund.Status }
                );
                return false;
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error verifying refund status",
                    details: $"Error verifying refund {refundId} for SearchHire {searchHireId}: {ex.Message}",
                    userId: initiatedByUserId ?? 0,
                    source: "StripeRefundService.VerifyRefundStatusAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { RefundId = refundId, Error = ex.Message }
                );
                return false;
            }
        }

        /// <summary>
        /// Verifica que un Transfer de Stripe exista y no esté revertido.
        /// ✅ CORRECCIÓN: En Stripe.NET, Transfer no tiene propiedad Status. Se verifica si existe y no está revertido.
        /// </summary>
        private async Task<bool> VerifyTransferStatusAsync(string transferId, int searchHireId, int? initiatedByUserId)
        {
            try
            {
                var transferService = new TransferService();
                var transfer = await transferService.GetAsync(transferId);
                
                // ✅ CORRECCIÓN: En Stripe.NET, Transfer no tiene Status. Se verifica si existe y no está revertido
                if (transfer != null && !transfer.Reversed)
                {
                    return true;
                }
                
                // Si está revertido o no existe, no es válido
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Transfer not valid",
                    details: $"Transfer {transferId} for SearchHire {searchHireId} is {(transfer == null ? "not found" : "reversed")}.",
                    userId: initiatedByUserId ?? 0,
                    source: "StripeRefundService.VerifyTransferStatusAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { TransferId = transferId, IsReversed = transfer?.Reversed ?? false }
                );
                return false;
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error verifying transfer status",
                    details: $"Error verifying transfer {transferId} for SearchHire {searchHireId}: {ex.Message}",
                    userId: initiatedByUserId ?? 0,
                    source: "StripeRefundService.VerifyTransferStatusAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { TransferId = transferId, Error = ex.Message }
                );
                return false;
            }
        }

        /// <summary>
        /// Verifica que una TransferReversal de Stripe exista.
        /// ✅ CORRECCIÓN: En Stripe.NET, TransferReversal no tiene propiedad Status. Se verifica si existe.
        /// </summary>
        private async Task<bool> VerifyReversalStatusAsync(string transferId, string reversalId, int searchHireId, int? initiatedByUserId)
        {
            try
            {
                var reversalService = new TransferReversalService();
                var reversal = await reversalService.GetAsync(transferId, reversalId);
                
                // ✅ CORRECCIÓN: En Stripe.NET, TransferReversal no tiene Status. Se verifica si existe
                if (reversal != null)
                {
                    return true;
                }
                
                // Si no existe, no es válido
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Transfer reversal not found",
                    details: $"Transfer reversal {reversalId} for Transfer {transferId} (SearchHire {searchHireId}) was not found.",
                    userId: initiatedByUserId ?? 0,
                    source: "StripeRefundService.VerifyReversalStatusAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { TransferId = transferId, ReversalId = reversalId }
                );
                return false;
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Error verifying transfer reversal status",
                    details: $"Error verifying transfer reversal {reversalId} for Transfer {transferId} (SearchHire {searchHireId}): {ex.Message}",
                    userId: initiatedByUserId ?? 0,
                    source: "StripeRefundService.VerifyReversalStatusAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new { TransferId = transferId, ReversalId = reversalId, Error = ex.Message }
                );
                return false;
            }
        }
    }
}

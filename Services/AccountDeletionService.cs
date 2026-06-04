
using Microsoft.EntityFrameworkCore;
using Npgsql;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.enums;
using newApi.Common;
using Hangfire;

namespace newApi.Services
{
    public interface IAccountDeletionService
    {
        Task<AccountDeletionStatusDto> CheckDeletionStatusAsync(int userId, CancellationToken cancellationToken = default);
        Task<AccountDeletionResponseDto> DeleteAccountAsync(int userId, AccountDeletionRequestDto request, CancellationToken cancellationToken = default);
    }

    public class AccountDeletionService : IAccountDeletionService
    {
        private readonly AppDbContext _context;
        private readonly IAccountDeletionNotificationService _notificationService;
        private readonly StripeRefundService _refundService;
        private readonly ILoggingService _loggingService;
        private readonly SystemStatusService _systemStatusService;
        // 🛡️ GDPR-S1 FIX: ISupabaseStorageService para limpiar archivos físicos en buckets
        // (avatares, fotos de servicio, attachments de chat, evidencias de disputa, fotos de reviews).
        // Sin esto, las filas BD se borran pero los archivos quedan accesibles por URL pública/firmada
        // hasta que alguien los purgue manualmente → violación GDPR Art 17 ("derecho al olvido").
        private readonly ISupabaseStorageService _storage;

        // ✅ MEJORA: Timeout para transacciones (5 minutos)
        private static readonly TimeSpan _transactionTimeout = TimeSpan.FromMinutes(5);

        public AccountDeletionService(
            AppDbContext context,
            IAccountDeletionNotificationService notificationService,
            StripeRefundService refundService,
            ILoggingService loggingService,
            SystemStatusService systemStatusService,
            ISupabaseStorageService storage)
        {
            _context = context;
            _notificationService = notificationService;
            _refundService = refundService;
            _loggingService = loggingService;
            _systemStatusService = systemStatusService;
            _storage = storage;
        }

        /// <summary>
        /// 🛡️ GDPR-S1 FIX: best-effort delete de objetos en Supabase Storage.
        /// NO aborta la eliminación de la cuenta si Supabase falla — la coherencia GDPR
        /// se garantiza con un log Critical para limpieza manual posterior.
        /// </summary>
        private async Task TryDeleteStorageObjectsAsync(
            string bucket,
            IEnumerable<string?> objectNames,
            int userId,
            string sourceTag,
            CancellationToken cancellationToken)
        {
            var paths = objectNames
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => o!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Count == 0) return;

            if (!_storage.IsConfigured)
            {
                await _loggingService.LogCriticalAsync(
                    message: $"GDPR-S1: SupabaseStorage no configurado — {paths.Count} archivo(s) NO purgados",
                    details: $"User {userId}: {paths.Count} objectPath(s) en bucket '{bucket}' deberían eliminarse pero el servicio Storage no tiene URL/ServiceRoleKey configurada. ACCIÓN ADMIN: purgar manualmente. Paths: {string.Join(", ", paths.Take(20))}",
                    userId: userId,
                    source: $"AccountDeletionService.{sourceTag}.S1",
                    relatedEntityType: "SupabaseStorage",
                    relatedEntityId: null);
                return;
            }

            int ok = 0, fail = 0;
            var failed = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    var deleted = await _storage.DeleteAsync(bucket, path, cancellationToken);
                    if (deleted) ok++;
                    else { fail++; failed.Add(path); }
                }
                catch (Exception ex)
                {
                    fail++;
                    failed.Add($"{path} (ex: {ex.GetType().Name})");
                }
            }

            if (fail > 0)
            {
                await _loggingService.LogCriticalAsync(
                    message: $"GDPR-S1: Supabase delete falló para {fail}/{paths.Count} archivo(s)",
                    details: $"User {userId} bucket '{bucket}': {ok} borrados, {fail} fallidos. ACCIÓN ADMIN: purgar manualmente en Supabase Dashboard. Failed paths: {string.Join(" | ", failed.Take(20))}",
                    userId: userId,
                    source: $"AccountDeletionService.{sourceTag}.S1",
                    relatedEntityType: "SupabaseStorage",
                    relatedEntityId: null);
            }
            else
            {
                await _loggingService.LogInfoAsync(
                    message: $"GDPR-S1: Supabase delete OK ({ok} archivos)",
                    details: $"User {userId} bucket '{bucket}': {ok} objectPath(s) purgados correctamente.",
                    userId: null,
                    source: $"AccountDeletionService.{sourceTag}.S1",
                    relatedEntityType: "SupabaseStorage",
                    relatedEntityId: null);
            }
        }

        public async Task<AccountDeletionStatusDto> CheckDeletionStatusAsync(int userId, CancellationToken cancellationToken = default)
        {
            try
            {
                // ✅ MEJORA: No incluir SearchHires aquí - GetActiveContractsAsync hace sus propias queries optimizadas
                var user = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

                if (user == null)
                {
                    return new AccountDeletionStatusDto
                    {
                        CanDeleteImmediately = false,
                        HasActiveContracts = false,
                        ActiveContractsCount = 0,
                        Message = "Usuario no encontrado"
                    };
                }

                // Buscar contrataciones activas
                var activeContracts = await GetActiveContractsAsync(userId, cancellationToken);

                var canDeleteImmediately = !activeContracts.Any();
                var message = canDeleteImmediately
                    ? "La cuenta puede ser eliminada inmediatamente"
                    : $"Se encontraron {activeContracts.Count} contrataciones activas que requieren atención";

                return new AccountDeletionStatusDto
                {
                    CanDeleteImmediately = canDeleteImmediately,
                    HasActiveContracts = activeContracts.Any(),
                    ActiveContractsCount = activeContracts.Count,
                    ActiveContracts = activeContracts,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                // ✅ MEJORA: Logging del error para mejor trazabilidad
                await _loggingService.LogErrorAsync(
                    message: "Error checking account deletion status",
                    details: $"Failed to check deletion status for user {userId}. Error: {ex.Message}",
                    userId: userId,
                    source: "AccountDeletionService.CheckDeletionStatusAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new
                    {
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                throw;
            }
        }

        public async Task<AccountDeletionResponseDto> DeleteAccountAsync(int userId, AccountDeletionRequestDto request, CancellationToken cancellationToken = default)
        {
            // ✅ MEJORA: Timeout para transacciones (5 minutos)
            using var timeoutCts = new CancellationTokenSource(_transactionTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            // ═══════════════════════════════════════════════════════════════════════════════
            // FASE 1: VALIDACIONES Y PROCESAMIENTO DE DINERO (FUERA de transacción global)
            // ═══════════════════════════════════════════════════════════════════════════════
            // ✅ CORRECCIÓN CRÍTICA: Procesar dinero ANTES de la transacción global
            // Cada llamada a ProcessMoneyDistributionAsync tendrá su propia transacción atómica
            // Esto evita que un rollback de eliminación de datos elimine registros de dinero ya movido en Stripe

            // 1. Verificar usuario
            var user = await _context.Users
                .IgnoreQueryFilters()
                .Include(u => u.ExpertProfile)
                .FirstOrDefaultAsync(u => u.Id == userId, linkedCts.Token);

            if (user == null)
            {
                return new AccountDeletionResponseDto
                {
                    Success = false,
                    Message = "Usuario no encontrado"
                };
            }

            // ✅ VALIDACIÓN: Verificar que el usuario no esté ya eliminado
            if (user.IsDeleted)
            {
                return new AccountDeletionResponseDto
                {
                    Success = false,
                    Message = $"Usuario ya fue eliminado el {user.DeletedAt:yyyy-MM-dd HH:mm:ss}"
                };
            }

            // ✅ P3-4: Bloqueo HARD - disputas pendientes y PaymentIntents activos
            // Antes de iniciar Fase 1 (procesamiento de dinero) verificamos invariantes que harían
            // inseguro continuar con la eliminación de la cuenta.
            // 🛡️ W2 FIX (Round 8 A14): bloquear también disputes en estado "Resolving".
            // Antes solo "Pending" → si una dispute pasaba a Resolving entre el check y el
            // delete, se anonimizaba a media transacción de resolución del admin → race
            // condition que dejaba la dispute huérfana sin reporter ni resolución completa.
            var hasPendingDispute = await _context.Disputes
                .AsNoTracking()
                .AnyAsync(d => (d.Status == "Pending" || d.Status == "Resolving")
                               && (d.ReporterId == userId
                                   || d.SearchHire.ClientId == userId
                                   || d.SearchHire.ExpertId == userId),
                          linkedCts.Token);

            if (hasPendingDispute)
            {
                await _loggingService.LogWarningAsync(
                    message: "Account deletion blocked - pending dispute",
                    details: $"User {userId} attempted account deletion while having at least one Dispute in Pending status.",
                    userId: userId,
                    source: "AccountDeletionService.DeleteAccountAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId);

                return new AccountDeletionResponseDto
                {
                    Success = false,
                    Message = "No se puede eliminar la cuenta con disputas pendientes. Espera a que se resuelvan o contacta con soporte."
                };
            }

            // FinancialTransaction no expone Stripe status, así que detectamos PIs activos a
            // través de FinancialTransactions ServicePayment del usuario que apuntan a un
            // SearchHire en estado "Pending" (PaymentIntent en deferred capture aún no liquidado).
            var pendingHireStatusValue = SearchHireStatus.Pending.ToStringValue();
            var hasActivePaymentIntent = await _context.FinancialTransactions
                .AsNoTracking()
                .AnyAsync(ft => ft.UserId == userId
                                && ft.TransactionType == "ServicePayment"
                                && ft.StripePaymentIntentId != null
                                && !ft.IsRefunded
                                && ft.RelatedEntityType == "SearchHire"
                                && ft.RelatedEntityId != null
                                && _context.SearchHires.Any(sh => sh.Id == ft.RelatedEntityId
                                                                  && sh.Status != null
                                                                  && sh.Status.StatusValue == pendingHireStatusValue),
                          linkedCts.Token);

            if (hasActivePaymentIntent)
            {
                await _loggingService.LogWarningAsync(
                    message: "Account deletion blocked - active PaymentIntents",
                    details: $"User {userId} attempted account deletion with SearchHires in '{pendingHireStatusValue}' linked to a StripePaymentIntentId (deferred capture not finalized).",
                    userId: userId,
                    source: "AccountDeletionService.DeleteAccountAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId);

                return new AccountDeletionResponseDto
                {
                    Success = false,
                    Message = "No se puede eliminar la cuenta con pagos en proceso. Espera a que los cobros pendientes se resuelvan o contacta con soporte."
                };
            }

            // 🛡️ N6 FIX: bloquear delete si hay Refund o Chargeback/ChargebackReversal pendientes.
            // El guard anterior solo cubre ServicePayment con PI activo; faltan los flows post-pago
            // que aún están en curso (refund encolado tras dispute resuelta, chargeback en proceso).
            // Si el delete procede ahora, las filas FT se anonimizan (UserId=null) y el operario
            // pierde la pista de qué refund/clawback corresponde a este usuario.
            //
            // 🛡️ Round 27 — R27-T27-1-6 FIX (CRÍTICO): el guard previo usaba `!ft.IsRefunded`,
            // pero IsRefunded SÓLO se setea a true en la fila ServicePayment original cuando un
            // refund settles (RefundService.cs:1643/1741/2012). NUNCA se setea en las filas con
            // TransactionType='Refund'. Resultado: cualquier usuario con un Refund histórico
            // (auto-refund por no-respuesta del experto, dispute resuelta a su favor, cancelación)
            // quedaba PERMANENTEMENTE bloqueado de borrar su cuenta → violación GDPR Art 17.
            // El path de admin (AccountDeletionController:83) llama al mismo método → admin
            // tampoco podía desbloquear sin tocar SQL manualmente.
            //
            // Ahora: ventana de 24h. Un Refund creado en las últimas 24h sí puede estar en
            // tránsito (Hangfire en curso, webhook charge.refund.updated pendiente). Más antiguo
            // ya está conciliado (Stripe responde con webhook en minutos, no días).
            var refundPendingCutoff = DateTime.UtcNow.AddHours(-24);
            var hasPendingRefund = await _context.FinancialTransactions
                .AsNoTracking()
                .AnyAsync(ft => ft.UserId == userId
                                && ft.TransactionType == "Refund"
                                && ft.CreatedAt >= refundPendingCutoff,
                          linkedCts.Token);
            if (hasPendingRefund)
            {
                await _loggingService.LogWarningAsync(
                    message: "N6: Account deletion blocked - pending refunds",
                    details: $"User {userId} tiene Refund(s) creados en las últimas 24h. Esperar a que el flow termine antes del delete para preservar el rastro fiscal.",
                    userId: userId,
                    source: "AccountDeletionService.DeleteAccountAsync.N6",
                    relatedEntityType: "User",
                    relatedEntityId: userId);
                return new AccountDeletionResponseDto
                {
                    Success = false,
                    Message = "No se puede eliminar la cuenta con reembolsos pendientes. Inténtalo de nuevo en unos minutos o contacta con soporte."
                };
            }

            // 🛡️ Round 27 — R27-T27-1-8 FIX (CRÍTICO): el guard previo matcheaba sólo por
            // ft.UserId == userId, pero HandleChargeDisputeCreated SIEMPRE crea las filas
            // Chargeback/ChargebackReversal con UserId=clientId (SubscriptionController:7278-7289).
            // Un experto con un chargeback activo contra una de sus ventas pasaba este guard,
            // borraba su cuenta + Stripe Connect account + DisputeFiles, y la plataforma quedaba
            // indefensa: BuildDisputeEvidenceAsync subía '[Datos eliminados]' + cero archivos,
            // Stripe rulea LOST, plataforma absorbe importe + fee + golpe reputacional.
            //
            // Ahora cubrimos AMBAS direcciones:
            //   (a) cliente con chargeback (caso original, ft.UserId == userId)
            //   (b) experto cuyo SearchHire tiene un chargeback activo
            //       (vía FT.RelatedEntityType='SearchHire' + SearchHire.ExpertId == userId)
            var hasPendingChargebackAsClient = await _context.FinancialTransactions
                .AsNoTracking()
                .AnyAsync(ft => ft.UserId == userId
                                && (ft.TransactionType == "Chargeback"
                                    || ft.TransactionType == "ChargebackReversal"),
                          linkedCts.Token);

            var hasPendingChargebackAsExpert = false;
            if (!hasPendingChargebackAsClient)
            {
                // Vía SearchHire.ExpertId: si una FT Chargeback apunta a un SearchHire del experto.
                hasPendingChargebackAsExpert = await _context.FinancialTransactions
                    .AsNoTracking()
                    .Where(ft => (ft.TransactionType == "Chargeback"
                                  || ft.TransactionType == "ChargebackReversal")
                                 && ft.RelatedEntityType == "SearchHire"
                                 && ft.RelatedEntityId != null)
                    .AnyAsync(ft => _context.SearchHires
                        .AsNoTracking()
                        .Any(sh => sh.Id == ft.RelatedEntityId!.Value && sh.ExpertId == userId),
                              linkedCts.Token);
            }

            if (hasPendingChargebackAsClient || hasPendingChargebackAsExpert)
            {
                await _loggingService.LogWarningAsync(
                    message: "N6: Account deletion blocked - chargeback in progress",
                    details: $"User {userId} tiene FinancialTransactions tipo Chargeback/ChargebackReversal " +
                             $"(asClient={hasPendingChargebackAsClient}, asExpert={hasPendingChargebackAsExpert}). " +
                             "El proceso de disputa externa con Stripe sigue abierto; el delete se aplazaría hasta su resolución para no perder trazabilidad ni evidencia.",
                    userId: userId,
                    source: "AccountDeletionService.DeleteAccountAsync.N6",
                    relatedEntityType: "User",
                    relatedEntityId: userId);
                return new AccountDeletionResponseDto
                {
                    Success = false,
                    Message = "No se puede eliminar la cuenta mientras hay un contracargo (chargeback) en proceso. Contacta con soporte para resolverlo primero."
                };
            }

            // 2. Obtener contrataciones activas (fuera de transacción)
            var activeContracts = await GetActiveContractsAsync(userId, linkedCts.Token);
            var disputesCreated = new List<DisputeCreatedInfo>();
            var processingErrors = new List<(int SearchHireId, string ErrorMessage, string ErrorType, decimal Amount)>();

            // 3. Procesar dinero de contrataciones activas FUERA de transacción global
            // ✅ CRÍTICO: Cada ProcessMoneyDistributionAsync usa su propia transacción atómica
            // Si falla la eliminación posterior, el dinero ya está correctamente procesado y registrado
            if (activeContracts.Any())
            {
                var result = await ProcessActiveContractsAsync(userId, activeContracts, request.Reason, linkedCts.Token);
                disputesCreated = result.TransactionsProcessed;
                processingErrors = result.ProcessingErrors;
            }

            // ═══════════════════════════════════════════════════════════════════════════════
            // FASE 2: ELIMINACIÓN DE DATOS DEL USUARIO (DENTRO de transacción)
            // ═══════════════════════════════════════════════════════════════════════════════
            // ✅ La transacción solo cubre la eliminación de datos, NO el procesamiento de dinero
            // Si esta fase falla, el dinero ya está seguro (procesado en Fase 1)

            // ✅ FIX CRÍTICO: NO usar ExecutionStrategy con transacciones manuales en PgBouncer
            // PgBouncer Transaction Pooler no admite savepoints automáticos que EF Core intenta crear
            // ✅ FIX: Deshabilitar savepoints automáticos según documentación oficial de Microsoft
            _context.Database.AutoSavepointsEnabled = false;

            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await _context.Database.BeginTransactionAsync(linkedCts.Token);
                try
                {
                    // 4. Eliminar datos del usuario (dentro de transacción)
                    await DeleteUserDataAsync(userId, linkedCts.Token);

                    // 5. Confirmar transacción PRIMERO (antes de notificaciones)
                    // ✅ MEJORA: Las notificaciones no deberían bloquear la eliminación de la cuenta
                    await transaction.CommitAsync(linkedCts.Token);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    // ✅ MEJORA: Manejo específico de conflictos de concurrencia (PostgreSQL MVCC)
                    await transaction.RollbackAsync(linkedCts.Token);

                    // ✅ NOTA: El dinero ya fue procesado en Fase 1 (antes de esta transacción)
                    // Solo se revierte la eliminación de datos del usuario
                    var moneyAlreadyProcessed = disputesCreated.Any() || processingErrors.Any();

                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Account deletion failed - concurrency conflict (money already processed)",
                        details: $"Account deletion transaction for user {userId} was rolled back due to concurrency conflict. " +
                                $"Another process modified the user data concurrently. Error: {ex.Message}. " +
                                $"IMPORTANT: Only data deletion was rolled back. " +
                                (moneyAlreadyProcessed
                                    ? $"MONEY WAS ALREADY PROCESSED: {disputesCreated.Count} transfers completed, {processingErrors.Count} failed. " +
                                      $"User account remains intact but financial transactions are committed. " +
                                      $"ACTION REQUIRED: Review processed transactions and retry account deletion."
                                    : $"No money was processed. User account remains intact. ACTION REQUIRED: Retry account deletion."),
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new
                        {
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace,
                            InnerException = ex.InnerException?.Message,
                            TransactionRolledBack = true,
                            ErrorCategory = "ConcurrencyConflict",
                            RetryRecommended = true,
                            MoneyAlreadyProcessed = moneyAlreadyProcessed,
                            TransfersCompleted = disputesCreated.Count,
                            TransfersFailed = processingErrors.Count,
                            // 🛡️ E2-error-logging-coverage: detalle por SearchHire para reconciliación manual GDPR.
                            // Sin esto, el admin sólo ve contadores y debe correlacionar a mano qué hires se movieron.
                            ProcessedTransactions = disputesCreated.Select(d => new
                            {
                                d.SearchHireId,
                                d.DisputeId,
                                d.Reason,
                                d.AffectedPartyName,
                                d.AffectedPartyEmail
                            }).ToList(),
                            FailedTransactions = processingErrors.Select(e => new
                            {
                                e.SearchHireId,
                                e.Amount,
                                e.ErrorType,
                                e.ErrorMessage
                            }).ToList()
                        }
                    );

                    throw;
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException is PostgresException pgEx)
                {
                    // ✅ MEJORA: Manejo específico de errores de PostgreSQL
                    await transaction.RollbackAsync(linkedCts.Token);

                    // ✅ NOTA: El dinero ya fue procesado en Fase 1 (antes de esta transacción)
                    var moneyAlreadyProcessed = disputesCreated.Any() || processingErrors.Any();

                    string errorCategory = "DatabaseError";
                    string actionRequired = "Review database error and retry if appropriate";

                    // Identificar tipo de error PostgreSQL por SqlState
                    switch (pgEx.SqlState)
                    {
                        case "23505": // Unique constraint violation
                            errorCategory = "UniqueConstraintViolation";
                            actionRequired = "Data conflict detected. Review unique constraints.";
                            break;
                        case "23503": // Foreign key violation
                            errorCategory = "ForeignKeyViolation";
                            actionRequired = "Referential integrity violation. Review related data.";
                            break;
                        case "40001": // Serialization failure (deadlock)
                            errorCategory = "Deadlock";
                            actionRequired = "Deadlock detected. Retry account deletion.";
                            break;
                        case "40P01": // Deadlock detected
                            errorCategory = "Deadlock";
                            actionRequired = "Deadlock detected. Retry account deletion.";
                            break;
                        case "08003": // Connection does not exist
                        case "08006": // Connection failure
                            errorCategory = "ConnectionError";
                            actionRequired = "Database connection error. Retry account deletion.";
                            break;
                    }

                    await _loggingService.LogCriticalAsync(
                        message: $"CRITICAL: Account deletion failed - PostgreSQL error ({pgEx.SqlState}) (money already processed)",
                        details: $"Account deletion transaction for user {userId} was rolled back due to PostgreSQL error. " +
                                $"SQL State: {pgEx.SqlState}, Constraint: {pgEx.ConstraintName}, " +
                                $"Message: {pgEx.Message}. IMPORTANT: Only data deletion was rolled back. " +
                                (moneyAlreadyProcessed
                                    ? $"MONEY WAS ALREADY PROCESSED: {disputesCreated.Count} transfers completed, {processingErrors.Count} failed. " +
                                      $"User account remains intact but financial transactions are committed. "
                                    : $"No money was processed. User account remains intact. ") +
                                $"ACTION REQUIRED: {actionRequired}",
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new
                        {
                            DeletedUserId = userId,
                            ErrorType = dbEx.GetType().Name,
                            PostgresErrorType = pgEx.GetType().Name,
                            SqlState = pgEx.SqlState,
                            ConstraintName = pgEx.ConstraintName,
                            TableName = pgEx.TableName,
                            ColumnName = pgEx.ColumnName,
                            ErrorMessage = pgEx.Message,
                            StackTrace = dbEx.StackTrace,
                            InnerException = dbEx.InnerException?.Message,
                            TransactionRolledBack = true,
                            ErrorCategory = errorCategory,
                            RetryRecommended = errorCategory == "Deadlock" || errorCategory == "ConnectionError",
                            MoneyAlreadyProcessed = moneyAlreadyProcessed,
                            TransfersCompleted = disputesCreated.Count,
                            TransfersFailed = processingErrors.Count,
                            // 🛡️ E2-error-logging-coverage: detalle por SearchHire para reconciliación manual GDPR.
                            ProcessedTransactions = disputesCreated.Select(d => new
                            {
                                d.SearchHireId,
                                d.DisputeId,
                                d.Reason,
                                d.AffectedPartyName,
                                d.AffectedPartyEmail
                            }).ToList(),
                            FailedTransactions = processingErrors.Select(e => new
                            {
                                e.SearchHireId,
                                e.Amount,
                                e.ErrorType,
                                e.ErrorMessage
                            }).ToList()
                        }
                    );

                    throw;
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == linkedCts.Token || ex.CancellationToken == timeoutCts.Token)
                {
                    // ✅ MEJORA: Manejo específico de timeout o cancelación
                    await transaction.RollbackAsync(linkedCts.Token);

                    // ✅ NOTA: El dinero ya fue procesado en Fase 1 (antes de esta transacción)
                    var moneyAlreadyProcessed = disputesCreated.Any() || processingErrors.Any();

                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Account deletion failed - transaction timeout (money already processed)",
                        details: $"Account deletion transaction for user {userId} was cancelled due to timeout (5 minutes) or cancellation request. " +
                                $"IMPORTANT: Only data deletion was rolled back. " +
                                (moneyAlreadyProcessed
                                    ? $"MONEY WAS ALREADY PROCESSED: {disputesCreated.Count} transfers completed, {processingErrors.Count} failed. " +
                                      $"User account remains intact but financial transactions are committed. "
                                    : $"No money was processed. User account remains intact. ") +
                                $"ACTION REQUIRED: Review if operation should be retried or if there are performance issues.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new
                        {
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace,
                            TransactionRolledBack = true,
                            ErrorCategory = "Timeout",
                            RetryRecommended = true,
                            TimeoutDuration = _transactionTimeout.TotalMinutes,
                            MoneyAlreadyProcessed = moneyAlreadyProcessed,
                            TransfersCompleted = disputesCreated.Count,
                            TransfersFailed = processingErrors.Count,
                            // 🛡️ E2-error-logging-coverage: detalle por SearchHire para reconciliación manual GDPR.
                            ProcessedTransactions = disputesCreated.Select(d => new
                            {
                                d.SearchHireId,
                                d.DisputeId,
                                d.Reason,
                                d.AffectedPartyName,
                                d.AffectedPartyEmail
                            }).ToList(),
                            FailedTransactions = processingErrors.Select(e => new
                            {
                                e.SearchHireId,
                                e.Amount,
                                e.ErrorType,
                                e.ErrorMessage
                            }).ToList()
                        }
                    );

                    throw;
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar rollback
                    try
                    {
                        await transaction.RollbackAsync(linkedCts.Token);
                    }
                    catch
                    {
                        // Ignorar errores de rollback si la conexión ya está disposed
                    }
                    throw;
                }
                catch (ObjectDisposedException disposedEx)
                {
                    // ✅ FIX CRÍTICO: Si la conexión está disposed, intentar rollback
                    try
                    {
                        await transaction.RollbackAsync(linkedCts.Token);
                    }
                    catch
                    {
                        // Ignorar errores de rollback si la conexión ya está disposed
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    // ✅ MEJOR PRÁCTICA: Logging completo del error antes de rethrow (catch-all para otros errores)
                    await transaction.RollbackAsync(linkedCts.Token);

                    // ✅ NOTA: El dinero ya fue procesado en Fase 1 (antes de esta transacción)
                    var moneyAlreadyProcessed = disputesCreated.Any() || processingErrors.Any();

                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Account deletion transaction rolled back (money already processed)",
                        details: $"Account deletion transaction for user {userId} was rolled back due to error. Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                $"IMPORTANT: Only data deletion was rolled back. " +
                                (moneyAlreadyProcessed
                                    ? $"MONEY WAS ALREADY PROCESSED: {disputesCreated.Count} transfers completed, {processingErrors.Count} failed. " +
                                      $"User account remains intact but financial transactions are committed. " +
                                      $"ACTION REQUIRED: Review processed transactions and manually complete account deletion if needed."
                                    : $"No money was processed. User account remains intact. ACTION REQUIRED: Retry account deletion."),
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new
                        {
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace,
                            InnerException = ex.InnerException?.Message,
                            TransactionRolledBack = true,
                            ErrorCategory = "Unknown",
                            MoneyAlreadyProcessed = moneyAlreadyProcessed,
                            TransfersCompleted = disputesCreated.Count,
                            TransfersFailed = processingErrors.Count,
                            // 🛡️ E2-error-logging-coverage: detalle por SearchHire para reconciliación manual GDPR.
                            ProcessedTransactions = disputesCreated.Select(d => new
                            {
                                d.SearchHireId,
                                d.DisputeId,
                                d.Reason,
                                d.AffectedPartyName,
                                d.AffectedPartyEmail
                            }).ToList(),
                            FailedTransactions = processingErrors.Select(e => new
                            {
                                e.SearchHireId,
                                e.Amount,
                                e.ErrorType,
                                e.ErrorMessage
                            }).ToList()
                        }
                    );

                    throw; // Re-throw para que el controller maneje el error
                }
            });

            // 6. Enviar notificaciones DESPUÉS del commit (si fallan, no afectan la eliminación)
            // ✅ MEJORA: Notificaciones fuera de transacción para que no bloqueen la eliminación
            try
            {
                if (disputesCreated.Any())
                {
                    await _notificationService.NotifyAffectedUsersAsync(disputesCreated);
                }
            }
            catch (Exception notificationEx)
            {
                // 🛡️ W1 FIX: CRITICAL (antes era Warning). Las contrapartes (cliente recibió
                // refund / experto recibió payout) NO se enteran de que el dinero se movió por
                // el delete del otro lado. Sin notificación → soporte recibe tickets a ciegas.
                // El admin debe enviar el aviso manualmente. CRITICAL dispara email automático.
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL W1: Failed to notify affected users after account deletion",
                    details: $"Account deletion completed for user {userId}, but the OTHER PARTIES (clients receiving refunds / experts receiving payouts) were NOT notified. They may have unexplained Stripe movements. ACCIÓN ADMIN: enviar notificación manual a cada afectado. Error: {notificationEx.Message}.",
                    userId: userId,
                    source: "AccountDeletionService.DeleteAccountAsync.W1",
                    relatedEntityType: "Notification",
                    relatedEntityId: null,
                    additionalData: new
                    {
                        DeletedUserId = userId,
                        Error = notificationEx.Message,
                        ErrorType = notificationEx.GetType().Name
                    }
                );
            }

            try
            {
                // Enviar notificación al usuario que eliminó su cuenta (aunque ya esté eliminado, el log queda)
                await _notificationService.SendAccountDeletionNotificationAsync(userId, request.Reason ?? "Sin razón especificada");
            }
            catch (Exception notificationEx)
            {
                // 🛡️ W1 FIX: CRITICAL (antes era Warning). El usuario NO recibe la confirmación
                // del delete → no tiene cómo verificar el resultado, no puede contactar soporte
                // con referencias, no tiene email de auditoría. CRITICAL dispara email al admin
                // para revisar el caso y considerar reenvío manual.
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL W1: Failed to send account deletion confirmation to user",
                    details: $"Account deletion succeeded for user {userId} but the USER was NOT notified — no tienen confirmación del delete. ACCIÓN ADMIN: revisar email del usuario y enviar manualmente la confirmación si procede (puede ser dirección anonimizada deleted-{userId}@deleted.local). Error: {notificationEx.Message}.",
                    userId: userId,
                    source: "AccountDeletionService.DeleteAccountAsync.W1",
                    relatedEntityType: "Notification",
                    relatedEntityId: null,
                    additionalData: new
                    {
                        DeletedUserId = userId,
                        Error = notificationEx.Message,
                        ErrorType = notificationEx.GetType().Name
                    }
                );
            }

            // ✅ MEJORA: Determinar si requiere revisión manual basado en errores de procesamiento
            var requiresManualReview = processingErrors.Any();
            var failedSearchHireIds = processingErrors.Select(e => e.SearchHireId).ToList();
            var message = activeContracts.Any()
                ? $"Cuenta eliminada. Se procesaron {disputesCreated.Count} transacciones automáticas para contrataciones activas."
                : "Cuenta eliminada exitosamente";

            // ✅ MEJORA: Mensaje mejorado - más conciso si hay muchos errores
            if (requiresManualReview)
            {
                if (processingErrors.Count <= 3)
                {
                    // Si hay pocos errores, mostrar detalles
                    var errorDetails = string.Join(", ", processingErrors.Select(e => $"#{e.SearchHireId}"));
                    message += $" {processingErrors.Count} contratación(es) requieren revisión manual debido a errores en el procesamiento (IDs: {errorDetails}).";
                }
                else
                {
                    // Si hay muchos errores, resumir (evitar mensajes muy largos)
                    message += $" {processingErrors.Count} contratación(es) requieren revisión manual debido a errores en el procesamiento. Ver logs para detalles completos.";
                }
            }

            return new AccountDeletionResponseDto
            {
                Success = true,
                Message = message,
                ActiveContracts = activeContracts,
                DisputesCreated = disputesCreated,
                RequiresManualReview = requiresManualReview, // ✅ Dinámico: true si hay errores de procesamiento
                FailedSearchHireIds = failedSearchHireIds, // ✅ MEJORA: IDs para facilitar revisión manual
                FailedContractsCount = processingErrors.Count // ✅ MEJORA: Cantidad de fallos
            };
        }

        private async Task<List<ActiveContractInfo>> GetActiveContractsAsync(int userId, CancellationToken cancellationToken = default)
        {
            var activeContracts = new List<ActiveContractInfo>();

            // ✅ MEJORA: Usar IsFinalizationStatus en lugar de array hardcodeado para detectar contrataciones activas
            // Una contratación está activa si NO está finalizada (IsFinalizationStatus = false)
            // ✅ MEJORA: Verificar que Status no sea null para evitar NullReferenceException
            // Buscar como cliente
            var clientContracts = await _context.SearchHires
                .Where(sh => sh.ClientId == userId && sh.Status != null && !sh.Status.IsFinalizationStatus)
                .Include(sh => sh.Status)
                .Include(sh => sh.Expert)
                .Include(sh => sh.SearchService)
                    .ThenInclude(ss => ss.ServiceType)
                .Include(sh => sh.Appointment)
                .ToListAsync(cancellationToken);

            foreach (var contract in clientContracts)
            {
                activeContracts.Add(new ActiveContractInfo
                {
                    SearchHireId = contract.Id,
                    Status = contract.Status.StatusValue,
                    ServiceName = contract.SearchService.ServiceType?.Name ?? "Servicio",
                    Amount = contract.Amount,
                    // 🛡️ Round 28 — Sprint 3: divisa snapshot del hire para que el frontend formatee correctamente.
                    Currency = string.IsNullOrWhiteSpace(contract.Currency) ? "EUR" : contract.Currency.Trim().ToUpperInvariant(),
                    CreatedAt = contract.CreatedAt,
                    OtherPartyName = contract.Expert?.Name ?? "Experto",
                    OtherPartyEmail = contract.Expert?.Email ?? "",
                    HasAppointment = contract.Appointment != null,
                    AppointmentDate = contract.Appointment?.ProposedDate
                });
            }

            // ✅ MEJORA: Usar IsFinalizationStatus en lugar de array hardcodeado
            // ✅ MEJORA: Verificar que Status no sea null para evitar NullReferenceException
            // Buscar como experto
            var expertContracts = await _context.SearchHires
                .Where(sh => sh.ExpertId == userId && sh.Status != null && !sh.Status.IsFinalizationStatus)
                .Include(sh => sh.Status)
                .Include(sh => sh.Client)
                .Include(sh => sh.SearchService)
                    .ThenInclude(ss => ss.ServiceType)
                .Include(sh => sh.Appointment)
                .ToListAsync(cancellationToken);

            foreach (var contract in expertContracts)
            {
                activeContracts.Add(new ActiveContractInfo
                {
                    SearchHireId = contract.Id,
                    Status = contract.Status.StatusValue,
                    ServiceName = contract.SearchService.ServiceType?.Name ?? "Servicio",
                    Amount = contract.Amount,
                    // 🛡️ Round 28 — Sprint 3: divisa snapshot del hire.
                    Currency = string.IsNullOrWhiteSpace(contract.Currency) ? "EUR" : contract.Currency.Trim().ToUpperInvariant(),
                    CreatedAt = contract.CreatedAt,
                    OtherPartyName = contract.Client?.Name ?? "Cliente",
                    OtherPartyEmail = contract.Client?.Email ?? "",
                    HasAppointment = contract.Appointment != null,
                    AppointmentDate = contract.Appointment?.ProposedDate
                });
            }

            return activeContracts;
        }

        private async Task<(List<DisputeCreatedInfo> TransactionsProcessed, List<(int SearchHireId, string ErrorMessage, string ErrorType, decimal Amount)> ProcessingErrors)> ProcessActiveContractsAsync(
            int userId,
            List<ActiveContractInfo> activeContracts,
            string? deletionReason,
            CancellationToken cancellationToken = default)
        {
            var transactionsProcessed = new List<DisputeCreatedInfo>();
            // ✅ MEJORA: Acumular errores para log crítico final
            var processingErrors = new List<(int SearchHireId, string ErrorMessage, string ErrorType, decimal Amount)>();

            // ✅ OPTIMIZACIÓN: Cargar todos los SearchHires de una vez para evitar N+1 queries
            var searchHireIds = activeContracts.Select(c => c.SearchHireId).ToList();
            var searchHires = await _context.SearchHires
                .Where(sh => searchHireIds.Contains(sh.Id))
                .Include(sh => sh.Status)
                .Include(sh => sh.Client)
                .Include(sh => sh.Expert)
                .ToDictionaryAsync(sh => sh.Id, cancellationToken);

            // ✅ OPTIMIZACIÓN: Cargar todos los Appointments de una vez para evitar N+1 queries
            var appointments = await _context.Appointments
                .Where(a => searchHireIds.Contains(a.SearchHireId))
                .Include(a => a.Status)
                .ToDictionaryAsync(a => a.SearchHireId, cancellationToken);

            foreach (var contract in activeContracts)
            {
                if (!searchHires.TryGetValue(contract.SearchHireId, out var searchHire) || searchHire == null)
                {
                    continue;
                }

                // Determinar quién es la parte afectada
                var affectedParty = searchHire.ClientId == userId ? searchHire.Expert : searchHire.Client;
                var isClientDeleting = searchHire.ClientId == userId;

                var reasonText = !string.IsNullOrEmpty(deletionReason)
                    ? (isClientDeleting
                        ? $"Cliente eliminó su cuenta. Razón: {deletionReason}"
                        : $"Experto eliminó su cuenta. Razón: {deletionReason}")
                    : (isClientDeleting
                        ? "Cliente eliminó su cuenta"
                        : "Experto eliminó su cuenta");

                try
                {
                    // 🚨 VERIFICACIÓN CRÍTICA: No tocar nada si ya está finalizado
                    if (searchHire.Status?.IsFinalizationStatus == true)
                    {
                        continue; // Saltar al siguiente SearchHire - NO tocar nada
                    }

                    // ✅ OPTIMIZACIÓN: Usar diccionario en lugar de query individual
                    // Verificar si hay subestado de finalización en appointment
                    var existingAppointment = appointments.TryGetValue(searchHire.Id, out var appointment) ? appointment : null;

                    if (existingAppointment?.Status != null && existingAppointment.Status.IsFinalizationStatus)
                    {
                        continue; // Saltar al siguiente SearchHire - NO tocar nada
                    }

                    // 🎯 PROCESAR DINERO PRIMERO (con updateState: true para que cambie el estado automáticamente)
                    // ✅ MEJORA: Procesar dinero ANTES de hacer cambios manuales para evitar estados inconsistentes
                    // ProcessMoneyDistributionAsync con updateState: true manejará:
                    // - Cambio de estado del SearchHire
                    // - Cambio de estado del Appointment (si existe)
                    // - Procesamiento del dinero

                    if (isClientDeleting)
                    {
                        // Si el cliente elimina su cuenta, dar el dinero al experto
                        var transferSuccess = await _refundService.ProcessMoneyDistributionAsync(
                            searchHire.Id,
                            // 🔧 FIX F1 (cita zombi): usar un AppointmentStatus que EXISTE en SystemStatuses y cuyo
                            // GetDefaultMapping == Completed (mismo reparto 0/95/5). El literal *_account_delete no
                            // tiene fila → el Appointment nunca se actualizaba (quedaba 'zombi' con el hire finalizado).
                            "appointment_completed_without_client_approval",
                            "Client account deletion - transfer to expert",
                            userId, // ✅ Agregar initiatedByUserId para auditoría
                            updateState: true); // ✅ updateState: true maneja el cambio de estado automáticamente

                        if (!transferSuccess)
                        {
                            var errorMessage = $"Failed to process transfer to expert for SearchHire {searchHire.Id}";
                            var errorType = "TransferFailure";

                            // ✅ Log individual del error
                            await _loggingService.LogErrorAsync(
                                message: "Failed to process transfer to expert for account deletion",
                                details: $"Transfer to expert failed for account deletion SearchHire {searchHire.Id}. Amount: {searchHire.Amount}€. Manual intervention required.",
                                userId: searchHire.ExpertId,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "Transfer",
                                relatedEntityId: searchHire.Id,
                                additionalData: new
                                {
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    ExpertId = searchHire.ExpertId,
                                    DeletionReason = deletionReason
                                }
                            );

                            // ✅ CRÍTICO: Verificar si el estado se cambió (puede haber fallado en Fase 1 o 2)
                            // Si NO se cambió, cambiarlo manualmente para evitar que el sistema quede bloqueado
                            await EnsureStateChangedAsync(searchHire.Id, "appointment_completed_without_client_approval", cancellationToken); // 🔧 FIX F1: estado existente (→Completed), evita cita zombi

                            // ✅ Acumular error para log crítico final
                            processingErrors.Add((searchHire.Id, errorMessage, errorType, searchHire.Amount));

                            // ✅ CANCELAR timers activos y jobs de Hangfire (aunque falle el dinero)
                            await CancelActiveTimersAndHangfireJobsAsync(searchHire.Id, cancellationToken);

                            // Continuar con siguiente contratación (no lanzar excepción)
                            continue;
                        }

                        // ✅ Notificar al experto que recibió el pago
                        if (searchHire.ExpertId.HasValue)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Pago procesado por eliminación de cuenta del cliente",
                                // 🛡️ Round 28 — Sprint 3: usar divisa real del hire en notificaciones, no € hardcoded.
                                details: $"El cliente del servicio #{searchHire.Id} eliminó su cuenta. Se procesó automáticamente el pago de {searchHire.Amount:F2} {(searchHire.Currency ?? "EUR")} a tu favor. El dinero está disponible en tu cuenta de Stripe.",
                                userId: searchHire.ExpertId.Value,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true,
                                additionalData: new
                                {
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    Action = "AccountDeletion_ClientDeleted",
                                    DeletionReason = deletionReason
                                }
                            );
                        }

                        // ✅ Notificar al cliente (antes de que se elimine su cuenta) sobre el procesamiento
                        await _loggingService.LogInfoAsync(
                            message: "Servicio cancelado por eliminación de cuenta",
                            details: $"Al eliminar tu cuenta, el servicio #{searchHire.Id} fue cancelado y el pago de {searchHire.Amount:F2} {(searchHire.Currency ?? "EUR")} fue transferido automáticamente al experto.",
                            userId: userId,
                            source: "AccountDeletionService.ProcessActiveContractsAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id,
                            notifyUser: true,
                            additionalData: new
                            {
                                SearchHireId = searchHire.Id,
                                Amount = searchHire.Amount,
                                Action = "AccountDeletion_ClientDeleted",
                                DeletionReason = deletionReason
                            }
                        );

                        // ✅ CANCELAR timers activos y jobs de Hangfire
                        await CancelActiveTimersAndHangfireJobsAsync(searchHire.Id, cancellationToken);
                    }
                    else
                    {
                        // Si el experto elimina su cuenta, reembolsar al cliente
                        var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                            searchHire.Id,
                            // 🔧 FIX F1 (cita zombi): AppointmentStatus existente con GetDefaultMapping == Cancelled
                            // (mismo reparto 100/0/0, cliente reembolsado). El literal *_account_delete no tiene fila
                            // SystemStatuses → el Appointment no se actualizaba.
                            "appointment_cancelled_by_expert_second",
                            reasonText,
                            userId, // ✅ Agregar initiatedByUserId para auditoría
                            updateState: true); // ✅ updateState: true maneja el cambio de estado automáticamente

                        if (!refundSuccess)
                        {
                            var errorMessage = $"Failed to process Stripe refund for SearchHire {searchHire.Id}";
                            var errorType = "RefundFailure";

                            // ✅ Log individual del error
                            await _loggingService.LogErrorAsync(
                                message: "Failed to process Stripe refund for account deletion",
                                details: $"Stripe refund failed for account deletion SearchHire {searchHire.Id}. Amount: {searchHire.Amount}€. Manual intervention required.",
                                userId: searchHire.ClientId,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "Refund",
                                relatedEntityId: searchHire.Id,
                                additionalData: new
                                {
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    ClientId = searchHire.ClientId,
                                    Reason = "Account deletion",
                                    DeletionReason = deletionReason
                                }
                            );

                            // ✅ CRÍTICO: Verificar si el estado se cambió (puede haber fallado en Fase 1 o 2)
                            // Si NO se cambió, cambiarlo manualmente para evitar que el sistema quede bloqueado
                            await EnsureStateChangedAsync(searchHire.Id, "appointment_cancelled_by_expert_second", cancellationToken); // 🔧 FIX F1: estado existente (→Cancelled), evita cita zombi

                            // ✅ Acumular error para log crítico final
                            processingErrors.Add((searchHire.Id, errorMessage, errorType, searchHire.Amount));

                            // ✅ CANCELAR timers activos y jobs de Hangfire (aunque falle el dinero)
                            await CancelActiveTimersAndHangfireJobsAsync(searchHire.Id, cancellationToken);

                            // Continuar con siguiente contratación (no lanzar excepción)
                            continue;
                        }

                        // ✅ Notificar al cliente que recibió el reembolso
                        await _loggingService.LogInfoAsync(
                            message: "Reembolso procesado por eliminación de cuenta del experto",
                            details: $"El experto del servicio #{searchHire.Id} eliminó su cuenta. Se procesó automáticamente tu reembolso de {searchHire.Amount:F2} {(searchHire.Currency ?? "EUR")}. El dinero llegará a tu cuenta en 5-10 días hábiles.",
                            userId: searchHire.ClientId,
                            source: "AccountDeletionService.ProcessActiveContractsAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id,
                            notifyUser: true,
                            additionalData: new
                            {
                                SearchHireId = searchHire.Id,
                                Amount = searchHire.Amount,
                                Action = "AccountDeletion_ExpertDeleted",
                                DeletionReason = deletionReason
                            }
                        );

                        // ✅ Notificar al experto (antes de que se elimine su cuenta) sobre el procesamiento
                        if (searchHire.ExpertId.HasValue)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Servicio cancelado por eliminación de cuenta",
                                details: $"Al eliminar tu cuenta, el servicio #{searchHire.Id} fue cancelado y se procesó automáticamente el reembolso de {searchHire.Amount:F2} {(searchHire.Currency ?? "EUR")} al cliente.",
                                userId: userId,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true,
                                additionalData: new
                                {
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    Action = "AccountDeletion_ExpertDeleted",
                                    DeletionReason = deletionReason
                                }
                            );
                        }

                        // ✅ CANCELAR timers activos y jobs de Hangfire
                        await CancelActiveTimersAndHangfireJobsAsync(searchHire.Id, cancellationToken);
                    }

                    // ✅ NOTA: NO cambiar StatusId manualmente aquí - ProcessMoneyDistributionAsync con updateState: true ya lo hizo
                    // El estado del SearchHire y Appointment ya fueron actualizados por ProcessMoneyDistributionAsync

                    transactionsProcessed.Add(new DisputeCreatedInfo
                    {
                        DisputeId = 0, // No hay disputa, es una transacción directa
                        SearchHireId = searchHire.Id,
                        Reason = reasonText,
                        AffectedPartyName = affectedParty?.Name ?? "Usuario",
                        AffectedPartyEmail = affectedParty?.Email ?? ""
                    });
                }
                catch (Exception ex)
                {
                    // ✅ MEJORA: Si falla el procesamiento, solo loguear y acumular error
                    // NOTA: No hay cambios de estado previos que revertir porque ProcessMoneyDistributionAsync
                    // se llama PRIMERO y si falla, no hace cambios (o hace rollback de sus propios cambios)
                    await _loggingService.LogErrorAsync(
                        message: "Money processing failed during account deletion",
                        details: $"Money processing failed for SearchHire {searchHire.Id} during account deletion of user {userId}. Error: {ex.Message}.",
                        userId: userId,
                        source: "AccountDeletionService.ProcessActiveContractsAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHire.Id,
                        additionalData: new
                        {
                            SearchHireId = searchHire.Id,
                            Error = ex.Message,
                            ErrorType = ex.GetType().Name,
                            StackTrace = ex.StackTrace,
                            DeletionReason = deletionReason
                        }
                    );

                    // ✅ Acumular error para log crítico final
                    processingErrors.Add((searchHire.Id, ex.Message, ex.GetType().Name, searchHire.Amount));

                    // Continuar con siguiente contratación (no lanzar excepción, no crear disputa)
                }
            }

            // ✅ MEJORA: Log crítico final resumiendo todos los errores acumulados
            if (processingErrors.Any())
            {
                var errorSummary = string.Join("; ", processingErrors.Select(e =>
                    $"SearchHire {e.SearchHireId} (Amount: {e.Amount}€, Error: {e.ErrorMessage})"));

                var totalFailedAmount = processingErrors.Sum(e => e.Amount);
                var errorTypes = processingErrors.GroupBy(e => e.ErrorType)
                    .Select(g => $"{g.Key}: {g.Count()}")
                    .ToList();

                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Account deletion completed with processing failures",
                    details: $"Account deletion for user {userId} completed, but {processingErrors.Count} contract(s) failed to process money. " +
                            // 🛡️ Round 28 — Sprint 3: total agregado (varias divisas posibles), etiqueta "mixed".
                            $"Total failed amount: {totalFailedAmount:F2} (importes agregados, posibles multi-divisa — ver hires individualmente). " +
                            $"Error summary: {errorSummary}. " +
                            $"Error types: {string.Join(", ", errorTypes)}. " +
                            $"ACTION REQUIRED: Manual review and processing required for failed contracts. " +
                            $"SearchHire IDs: {string.Join(", ", processingErrors.Select(e => e.SearchHireId))}.",
                    userId: userId,
                    source: "AccountDeletionService.ProcessActiveContractsAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new
                    {
                        DeletedUserId = userId,
                        FailedContractsCount = processingErrors.Count,
                        TotalFailedAmount = totalFailedAmount,
                        FailedSearchHireIds = processingErrors.Select(e => e.SearchHireId).ToList(),
                        ErrorDetails = processingErrors.Select(e => new
                        {
                            SearchHireId = e.SearchHireId,
                            Amount = e.Amount,
                            ErrorMessage = e.ErrorMessage,
                            ErrorType = e.ErrorType
                        }).ToList(),
                        ErrorTypes = errorTypes,
                        DeletionReason = deletionReason
                    }
                );
            }

            // ✅ NOTA: No es necesario SaveChangesAsync aquí porque:
            // 1. ProcessMoneyDistributionAsync con updateState: true ya guarda los cambios de estado en su propia transacción
            // 2. Los logs se guardan a través de LoggingService que usa su propio DbContext scoped
            // 3. No hay cambios directos en el contexto de EF Core que necesiten guardarse
            // 4. Todos los cambios se guardarán cuando se commitee la transacción global en DeleteAccountAsync
            return (transactionsProcessed, processingErrors);
        }

        /// <summary>
        /// Elimina/anonimiza todos los datos de un usuario siguiendo las mejores prácticas del sistema.
        /// Estructura en fases similar a ProcessMoneyDistributionAsync:
        /// - Fase 1: Validaciones (dentro de transacción global)
        /// - Fase 2: Anonimización de datos críticos (misma transacción global - sin nested tx)
        /// - Fase 3: Eliminación de datos no críticos (misma transacción global - batch deletes)
        /// - Fase 4: Eliminación del usuario (misma transacción global)
        ///
        /// NOTA: Todo se ejecuta en la transacción global de DeleteAccountAsync para evitar problemas de nested transactions.
        /// </summary>
        private async Task DeleteUserDataAsync(int userId, CancellationToken cancellationToken = default)
        {
            // ===== FASE 1: VALIDACIONES (dentro de transacción global) =====
            try
            {
                // ✅ VALIDACIONES PRE-DELETE: Verificar que no haya transacciones pendientes
                // ✅ FIX: Usar SQL directo para evitar ExecutionStrategy dentro de transacción manual
                // EnableRetryOnFailure activa ExecutionStrategy automáticamente, causando error con transacciones manuales
                var pendingTransactionsCount = await _context.Database.ExecuteSqlRawAsync(
                    @"SELECT COUNT(*) FROM ""FinancialTransactions""
                      WHERE ""UserId"" = {0}
                        AND (""TransactionType"" = 'ServicePayment' OR ""TransactionType"" = 'Deposit')",
                    new object[] { userId }, cancellationToken);
                var pendingTransactions = pendingTransactionsCount > 0;

                if (pendingTransactions)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Account deletion attempted with pending financial transactions",
                        details: $"User {userId} has pending financial transactions. Review before deletion.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                    // Continuar pero loguear para auditoría
                }

                // ===== FASE 2: ANONIMIZACIÓN DE DATOS CRÍTICOS (misma transacción global) =====
                // ✅ MEJOR PRÁCTICA: Todo en la misma transacción global para evitar problemas de nested transactions
                // Usar SQL directo para anonimización (más eficiente y evita problemas de EF Core)
                try
                {
                    // 🛡️ N19 + GDPR-S1 FIX: eliminar MessageAttachments del User ANTES de anonimizar Messages.
                    // Los attachments contienen Url (Supabase Storage path) y ObjectName que pueden tener
                    // PII en el filename (ej: "cv_juan_perez.pdf", "scan_dni_12345678.jpg"). Anonimizar
                    // solo SenderId/Content dejaba estos archivos huérfanos accesibles → violación GDPR Art 17.
                    // GDPR-S1: leemos los ObjectName ANTES del DELETE y purgamos los archivos físicos en
                    // Supabase Storage (bucket privado FilesBucket). Best-effort: si Supabase falla, log
                    // Critical pero no aborta el delete (la cuenta del usuario sigue desapareciendo).
                    var attachmentObjectNames = await _context.MessageAttachments
                        .AsNoTracking()
                        .Where(ma => _context.Messages.Any(m => m.Id == ma.MessageId && m.SenderId == userId))
                        .Select(ma => ma.ObjectName)
                        .ToListAsync(cancellationToken);

                    if (attachmentObjectNames.Count > 0)
                    {
                        await TryDeleteStorageObjectsAsync(
                            _storage.FilesBucket,
                            attachmentObjectNames,
                            userId,
                            "DeleteUserDataAsync.N19",
                            cancellationToken);
                    }

                    var n19AttachmentsDeleted = await _context.Database.ExecuteSqlRawAsync(
                        @"DELETE FROM ""MessageAttachments""
                          WHERE ""MessageId"" IN (
                              SELECT ""Id"" FROM ""Messages"" WHERE ""SenderId"" = {0}
                          )",
                        new object[] { userId }, cancellationToken);
                    if (n19AttachmentsDeleted > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "N19: deleted MessageAttachments for account deletion",
                            details: $"Deleted {n19AttachmentsDeleted} MessageAttachment(s) for user {userId} antes de anonimizar Messages. {attachmentObjectNames.Count} archivo(s) físico(s) en Supabase Storage también purgados (ver log GDPR-S1).",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync.N19",
                            relatedEntityType: "MessageAttachment",
                            relatedEntityId: null);
                    }

                    // 1. ✅ ANONIMIZAR mensajes (NO ELIMINAR - preservar para la otra parte)
                    // PostgreSQL + C# Best Practice: Anonimizar en lugar de eliminar para preservar contexto
                    // SenderId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si SenderId no es NULL (no anonimizado ya)
                    var messagesCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Messages""
                          SET ""SenderId"" = NULL,
                              ""Content"" = '[Usuario eliminado] ' || COALESCE(""Content"", '')
                          WHERE ""SenderId"" = {0} AND ""SenderId"" IS NOT NULL",
                        new object[] { userId }, cancellationToken);

                    if (messagesCount > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Messages anonymized for account deletion",
                            details: $"Anonymized {messagesCount} messages for user {userId}. Context preserved for other party.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "Message",
                            relatedEntityId: null
                        );
                    }

                    // 2. ✅ ANONIMIZAR conversaciones (NO ELIMINAR - preservar para la otra parte)
                    // PostgreSQL + C# Best Practice: Anonimizar referencias pero mantener conversación
                    // ClientId y ExpertId son nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si ClientId/ExpertId no son NULL (no anonimizado ya)
                    var conversationsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Conversations""
                          SET ""ClientId"" = CASE WHEN ""ClientId"" = {0} THEN NULL ELSE ""ClientId"" END,
                              ""ExpertId"" = CASE WHEN ""ExpertId"" = {0} THEN NULL ELSE ""ExpertId"" END,
                              ""UpdatedAt"" = CURRENT_TIMESTAMP
                          WHERE (""ClientId"" = {0} AND ""ClientId"" IS NOT NULL)
                             OR (""ExpertId"" = {0} AND ""ExpertId"" IS NOT NULL)",
                        new object[] { userId }, cancellationToken);

                    if (conversationsCount > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Conversations anonymized for account deletion",
                            details: $"Anonymized {conversationsCount} conversations for user {userId}. History preserved.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "Conversation",
                            relatedEntityId: null
                        );
                    }

                    // 🛡️ GDPR-S1.d FIX: borrar imágenes físicas (Supabase ImagesBucket) y filas de
                    // ReviewImages para Reviews escritas POR el usuario (ReviewerId=userId). Estas fotos
                    // son PII del autor (típicamente fotos del problema/resultado del servicio). NO
                    // tocamos imágenes de Reviews ajenas aunque referencien al usuario como Expert.
                    var reviewImageObjects = await _context.ReviewImages
                        .AsNoTracking()
                        .Where(ri => _context.Reviews.Any(r => r.Id == ri.ReviewId && r.ReviewerId == userId))
                        .Select(ri => ri.ImageObjectName)
                        .ToListAsync(cancellationToken);

                    if (reviewImageObjects.Count > 0)
                    {
                        await TryDeleteStorageObjectsAsync(
                            _storage.ImagesBucket,
                            reviewImageObjects,
                            userId,
                            "DeleteUserDataAsync.ReviewImages",
                            cancellationToken);
                    }

                    var reviewImagesDeleted = await _context.Database.ExecuteSqlRawAsync(
                        @"DELETE FROM ""ReviewImages""
                          WHERE ""ReviewId"" IN (
                              SELECT ""Id"" FROM ""Reviews"" WHERE ""ReviewerId"" = {0}
                          )",
                        new object[] { userId }, cancellationToken);
                    if (reviewImagesDeleted > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "GDPR-S1.d: deleted ReviewImages",
                            details: $"Deleted {reviewImagesDeleted} ReviewImage row(s) for user {userId}. {reviewImageObjects.Count} archivo(s) físico(s) Supabase Storage también purgados.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync.ReviewImages",
                            relatedEntityType: "ReviewImage",
                            relatedEntityId: null);
                    }

                    // 3. ✅ ANONIMIZAR reseñas dadas (NO ELIMINAR - preservar para mantener calificaciones)
                    // PostgreSQL + C# Best Practice: Anonimizar pero preservar rating para mantener promedios
                    // ReviewerId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si ReviewerId no es NULL (no anonimizado ya)
                    // ✅ MEJORA: Agregar UpdatedAt para trazabilidad (aunque Review no tiene UpdatedAt, se preserva CreatedAt)
                    // ✅ CRÍTICO: Anonimizar Reviews ANTES de anonimizar/eliminar SearchHires para evitar violaciones de FK
                    // 🛡️ Round 27 — R27-A11-2 FIX: el texto original de la reseña puede contener PII
                    // del autor (nombre, dirección, teléfono). Antes se prefijaba con '[Usuario eliminado] '
                    // pero el cuerpo quedaba accesible vía GET /api/Review/expert/{expertId} a cualquier
                    // usuario autenticado → violación GDPR Art 17. Ahora reemplazamos el cuerpo entero
                    // por un placeholder; el Score numérico se preserva y los promedios siguen siendo correctos.
                    // Idempotencia por ReviewerId=NULL (la 2ª pasada no matchea).
                    var reviewsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Reviews""
                          SET ""ReviewerId"" = NULL,
                              ""Description"" = CASE WHEN ""Description"" IS NOT NULL AND ""Description"" != ''
                                  THEN '[Reseña eliminada por el autor]'
                                  ELSE ""Description"" END
                          WHERE ""ReviewerId"" = {0} AND ""ReviewerId"" IS NOT NULL",
                        new object[] { userId }, cancellationToken);

                    // ✅ CRÍTICO: También anonimizar Reviews que referencian SearchHires del usuario
                    // Esto previene violaciones de FK cuando se anonimizan/eliminan SearchHires
                    var reviewsForUserSearchHires = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Reviews""
                          SET ""Description"" = CASE WHEN ""Description"" IS NOT NULL AND ""Description"" != ''
                                  THEN SUBSTRING('[Usuario eliminado] ' || ""Description"" FROM 1 FOR 2000)
                                  ELSE ""Description"" END
                          WHERE ""SearchHireId"" IN (
                              SELECT ""Id"" FROM ""SearchHires""
                              WHERE ""ClientId"" = {0} OR ""ExpertId"" = {0}
                          ) AND COALESCE(""Description"", '') NOT ILIKE '[Usuario eliminado]%'",
                        new object[] { userId }, cancellationToken);

                    var totalReviewsAnonymized = reviewsCount + reviewsForUserSearchHires;
                    if (totalReviewsAnonymized > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Reviews anonymized for account deletion",
                            details: $"Anonymized {totalReviewsAnonymized} reviews for user {userId} ({reviewsCount} as reviewer, {reviewsForUserSearchHires} related to user's SearchHires). Ratings and averages preserved.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "Review",
                            relatedEntityId: null
                        );
                    }

                    // 4. ✅ ANONIMIZAR transacciones financieras (NO ELIMINAR - requerido por ley)
                    // PostgreSQL + C# Best Practice: Anonimizar datos financieros para cumplimiento legal
                    // Las transacciones financieras deben conservarse por 6 años (España) y para auditoría de Stripe
                    // IMPORTANTE: Anonimizar ANTES de eliminar el usuario para evitar cascade delete
                    // UserId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si UserId no es NULL (no anonimizado ya)
                    // ✅ MEJORA: Agregar UpdatedAt para trazabilidad de cuándo se anonimizó
                    // ✅ NOTA: FinancialTransaction no tiene UpdatedAt, solo CreatedAt (se preserva para auditoría)
                    var transactionsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""FinancialTransactions""
                          SET ""UserId"" = NULL
                          WHERE ""UserId"" = {0} AND ""UserId"" IS NOT NULL",
                        new object[] { userId }, cancellationToken);

                    if (transactionsCount > 0)
                    {
                        // ✅ LOG: Anonimización de transacciones financieras
                        await _loggingService.LogInfoAsync(
                            message: "Financial transactions anonymized for account deletion",
                            details: $"Anonymized {transactionsCount} financial transactions for user {userId}. Financial data preserved for legal compliance (6 years retention - Spain accounting law) and Stripe reconciliation. UserId set to NULL (deleted user marker).",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "FinancialTransaction",
                            relatedEntityId: null,
                            additionalData: new
                            {
                                DeletedUserId = userId,
                                TransactionsAnonymized = transactionsCount,
                                Action = "AnonymizeFinancialTransactions",
                                LegalCompliance = "6 years retention (Spain accounting law)",
                                StripeReconciliation = "Preserved StripeRefundId, StripeTransferId, StripePaymentIntentId, Amount, TransactionType, CreatedAt"
                            }
                        );
                    }

                    // 5. ✅ ANONIMIZAR notificaciones (NO ELIMINAR - preservar para auditoría)
                    // 🛡️ GDPR-R6 FIX: REEMPLAZAR Message en lugar de prefijar. Antes era
                    // '[Usuario eliminado] ' || Message → conservaba el contenido original que
                    // puede incluir nombre/email/teléfono del usuario eliminado (ej:
                    // "Juan Pérez (juan@example.com) ha solicitado tu servicio..."). Reemplazar
                    // con un placeholder evita la fuga. La fila se mantiene como rastro de que
                    // hubo una notificación, sin desvelar a quién pertenecía.
                    // Idempotencia con NOT ILIKE para evitar re-anonimizar.
                    var notificationsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Notifications""
                          SET ""UserId"" = NULL,
                              ""Message"" = '[Notificación de usuario eliminado]'
                          WHERE ""UserId"" = {0} AND ""UserId"" IS NOT NULL
                            AND COALESCE(""Message"", '') NOT ILIKE '[Notificación de usuario eliminado]%'",
                        new object[] { userId }, cancellationToken);

                    if (notificationsCount > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Notifications anonymized for account deletion",
                            details: $"Anonymized {notificationsCount} notifications for user {userId}. Audit trail preserved.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "Notification",
                            relatedEntityId: null
                        );
                    }

                    // 🛡️ GDPR FIX: capturar IDs de SearchHires del usuario ANTES de anonimizar
                    // (luego ClientId/ExpertId pasan a NULL y no podríamos joinear). Estos IDs los
                    // reutilizan los bloques A1 (Appointments) / F1 (DisputeFiles) / D1 (Disputes)
                    // para localizar las filas relacionadas con precisión, sin heurísticas temporales.
                    var userSearchHireIdsAll = await _context.SearchHires
                        .AsNoTracking()
                        .Where(sh => sh.ClientId == userId || sh.ExpertId == userId)
                        .Select(sh => sh.Id)
                        .ToListAsync(cancellationToken);
                    var userSearchHireIdsAsClient = await _context.SearchHires
                        .AsNoTracking()
                        .Where(sh => sh.ClientId == userId)
                        .Select(sh => sh.Id)
                        .ToListAsync(cancellationToken);

                    // 6. ✅ ANONIMIZAR SearchHires (NO ELIMINAR - preservar contrataciones históricas)
                    // PostgreSQL + C# Best Practice: Anonimizar referencias pero mantener historial de contrataciones
                    // ClientId y ExpertId son ahora nullable, permitiendo anonimización completa
                    // ✅ MEJORA: Anonimización completa de SearchHires (ClientId y ExpertId)
                    var searchHiresAsClient = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""SearchHires""
                          SET ""ClientId"" = NULL,
                              ""UpdatedAt"" = CURRENT_TIMESTAMP
                          WHERE ""ClientId"" = {0} AND ""ClientId"" IS NOT NULL",
                        new object[] { userId }, cancellationToken);

                    // ✅ Anonimizar ExpertId (ahora nullable después de la migración)
                    // ✅ MEJORA: Mantener try-catch por seguridad, pero debería funcionar correctamente ahora
                    int searchHiresAsExpert = 0;
                    try
                    {
                        searchHiresAsExpert = await _context.Database.ExecuteSqlRawAsync(
                            @"UPDATE ""SearchHires""
                              SET ""ExpertId"" = NULL,
                                  ""UpdatedAt"" = CURRENT_TIMESTAMP
                              WHERE ""ExpertId"" = {0} AND ""ExpertId"" IS NOT NULL",
                            new object[] { userId }, cancellationToken);
                    }
                    catch (PostgresException pgEx) when (pgEx.SqlState == "23502")
                    {
                        // ✅ FALLBACK: Si por alguna razón la migración no se aplicó correctamente, loguear y continuar
                        await _loggingService.LogWarningAsync(
                            message: "ExpertId cannot be anonymized - NOT NULL constraint",
                            details: $"Cannot anonymize ExpertId for user {userId} because the column has a NOT NULL constraint in the database. " +
                                    $"This should not happen if the migration was applied correctly. " +
                                    $"ACTION REQUIRED: Verify that migration 'MakeExpertIdNullableInSearchHires' was applied successfully.",
                            userId: userId,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: null,
                            additionalData: new
                            {
                                DeletedUserId = userId,
                                SqlState = pgEx.SqlState,
                                Error = "NOT NULL constraint violation on ExpertId",
                                ActionRequired = "Verify migration MakeExpertIdNullableInSearchHires was applied"
                            }
                        );
                        // Continuar sin fallar - ClientId ya fue anonimizado
                    }

                    var totalSearchHiresAnonymized = searchHiresAsClient + searchHiresAsExpert;

                    if (totalSearchHiresAnonymized > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "SearchHires anonymized for account deletion",
                            details: $"Anonymized {totalSearchHiresAnonymized} SearchHires for user {userId} ({searchHiresAsClient} as Client, {searchHiresAsExpert} as Expert). Contract history preserved.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: null
                        );
                    }

                    // 🛡️ GDPR-A1 FIX: anonimizar PII en Appointments donde el usuario era CLIENTE.
                    // El cliente proporciona Location (dirección física), DoorNumber, OwnerPhone
                    // (teléfono del propietario), SiteDetails y geocoordenadas. Si el cliente
                    // borra y no anonimizamos, esos datos quedan visibles al experto para siempre.
                    // NOTA: Si el experto borra, NO tocamos la cita (sigue siendo del cliente).
                    // Usamos userSearchHireIdsAsClient capturados ANTES de anonimizar.
                    var appointmentsAnonymized = 0;
                    if (userSearchHireIdsAsClient.Count > 0)
                    {
                        var apptHireIdsArr = string.Join(",", userSearchHireIdsAsClient);
                        appointmentsAnonymized = await _context.Database.ExecuteSqlRawAsync(
                            @"UPDATE ""Appointments""
                              SET ""Location"" = '[Datos eliminados]',
                                  ""DoorNumber"" = NULL,
                                  ""OwnerPhone"" = NULL,
                                  ""SiteDetails"" = NULL,
                                  ""Latitude"" = NULL,
                                  ""Longitude"" = NULL,
                                  ""UpdatedAt"" = CURRENT_TIMESTAMP
                              WHERE ""SearchHireId"" = ANY(ARRAY[" + apptHireIdsArr + @"]::integer[])
                                AND COALESCE(""Location"", '') NOT ILIKE '[Datos eliminados]%'",
                            cancellationToken);

                        if (appointmentsAnonymized > 0)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "GDPR-A1: Appointments PII anonymized",
                                details: $"Anonymized {appointmentsAnonymized} Appointment(s) for user {userId}: Location='[Datos eliminados]', DoorNumber/OwnerPhone/SiteDetails/Latitude/Longitude = NULL. Datos físicos (dirección, teléfono propietario) eliminados de {userSearchHireIdsAsClient.Count} SearchHire(s) donde era cliente.",
                                userId: null,
                                source: "AccountDeletionService.DeleteUserDataAsync.A1",
                                relatedEntityType: "Appointment",
                                relatedEntityId: null);
                        }
                    }

                    // 🛡️ GDPR-F1 FIX (parte 1/2): borrar archivos físicos DisputeFile en Supabase
                    // Storage ANTES de borrar las filas BD. DisputeFile.FilePath contiene el
                    // objectPath en FilesBucket (bucket privado). Aplica a disputas donde el usuario
                    // era Reporter, donde el SearchHire era del usuario (cliente o experto), o
                    // donde el usuario subió un archivo (UploadedByUserId=userId).
                    List<string?> disputeFilePaths = new();
                    if (userSearchHireIdsAll.Count > 0)
                    {
                        disputeFilePaths = await _context.DisputeFiles
                            .AsNoTracking()
                            .Where(df => df.UploadedByUserId == userId
                                      || _context.Disputes.Any(d => d.Id == df.DisputeId
                                          && (d.ReporterId == userId
                                              || userSearchHireIdsAll.Contains(d.SearchHireId))))
                            .Select(df => df.FilePath)
                            .ToListAsync(cancellationToken);
                    }
                    else
                    {
                        disputeFilePaths = await _context.DisputeFiles
                            .AsNoTracking()
                            .Where(df => df.UploadedByUserId == userId
                                      || _context.Disputes.Any(d => d.Id == df.DisputeId && d.ReporterId == userId))
                            .Select(df => df.FilePath)
                            .ToListAsync(cancellationToken);
                    }

                    if (disputeFilePaths.Count > 0)
                    {
                        await TryDeleteStorageObjectsAsync(
                            _storage.FilesBucket,
                            disputeFilePaths,
                            userId,
                            "DeleteUserDataAsync.F1",
                            cancellationToken);
                    }

                    // 🛡️ GDPR-F1 FIX (parte 2/2): borrar filas DisputeFile. Se eliminan por completo
                    // (no se anonimizan) porque el contenido del fichero ya no es accesible (purga arriba).
                    int disputeFilesDeleted;
                    if (userSearchHireIdsAll.Count > 0)
                    {
                        var dfHireIdsArr = string.Join(",", userSearchHireIdsAll);
                        disputeFilesDeleted = await _context.Database.ExecuteSqlRawAsync(
                            @"DELETE FROM ""DisputeFiles""
                              WHERE ""UploadedByUserId"" = {0}
                                 OR ""DisputeId"" IN (
                                     SELECT ""Id"" FROM ""Disputes""
                                     WHERE ""ReporterId"" = {0}
                                        OR ""SearchHireId"" = ANY(ARRAY[" + dfHireIdsArr + @"]::integer[])
                                 )",
                            new object[] { userId }, cancellationToken);
                    }
                    else
                    {
                        disputeFilesDeleted = await _context.Database.ExecuteSqlRawAsync(
                            @"DELETE FROM ""DisputeFiles""
                              WHERE ""UploadedByUserId"" = {0}
                                 OR ""DisputeId"" IN (
                                     SELECT ""Id"" FROM ""Disputes"" WHERE ""ReporterId"" = {0}
                                 )",
                            new object[] { userId }, cancellationToken);
                    }

                    if (disputeFilesDeleted > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "GDPR-F1: DisputeFiles deleted",
                            details: $"Deleted {disputeFilesDeleted} DisputeFile row(s) for user {userId}. {disputeFilePaths.Count} archivo(s) físico(s) en Supabase Storage también purgados.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync.F1",
                            relatedEntityType: "DisputeFile",
                            relatedEntityId: null);
                    }

                    // 🛡️ GDPR-D1 FIX: anonimizar Disputes PII (Reason, ResolutionComments, ExpertResponse).
                    // No se elimina la fila — Status/StripeDisputeId/SearchHireId quedan para auditoría
                    // Stripe. ReporterId es NOT NULL en BD: NO lo anonimizamos (requiere migración);
                    // se mantiene el FK pero el User al que apunta queda con
                    // email "deleted-{id}@deleted.local" tras U1. Idempotente con NOT ILIKE.
                    int disputesAnonymized;
                    if (userSearchHireIdsAll.Count > 0)
                    {
                        var dispHireIdsArr = string.Join(",", userSearchHireIdsAll);
                        disputesAnonymized = await _context.Database.ExecuteSqlRawAsync(
                            @"UPDATE ""Disputes""
                              SET ""Reason"" = '[Datos eliminados]',
                                  ""ResolutionComments"" = CASE WHEN ""ResolutionComments"" IS NOT NULL AND ""ResolutionComments"" != ''
                                      THEN '[Datos eliminados]'
                                      ELSE ""ResolutionComments"" END,
                                  ""ExpertResponse"" = CASE WHEN ""ExpertResponse"" IS NOT NULL AND ""ExpertResponse"" != ''
                                      THEN '[Datos eliminados]'
                                      ELSE ""ExpertResponse"" END
                              WHERE (""ReporterId"" = {0}
                                  OR ""SearchHireId"" = ANY(ARRAY[" + dispHireIdsArr + @"]::integer[]))
                                AND COALESCE(""Reason"", '') NOT ILIKE '[Datos eliminados]%'",
                            new object[] { userId }, cancellationToken);
                    }
                    else
                    {
                        disputesAnonymized = await _context.Database.ExecuteSqlRawAsync(
                            @"UPDATE ""Disputes""
                              SET ""Reason"" = '[Datos eliminados]',
                                  ""ResolutionComments"" = CASE WHEN ""ResolutionComments"" IS NOT NULL AND ""ResolutionComments"" != ''
                                      THEN '[Datos eliminados]'
                                      ELSE ""ResolutionComments"" END,
                                  ""ExpertResponse"" = CASE WHEN ""ExpertResponse"" IS NOT NULL AND ""ExpertResponse"" != ''
                                      THEN '[Datos eliminados]'
                                      ELSE ""ExpertResponse"" END
                              WHERE ""ReporterId"" = {0}
                                AND COALESCE(""Reason"", '') NOT ILIKE '[Datos eliminados]%'",
                            new object[] { userId }, cancellationToken);
                    }

                    if (disputesAnonymized > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "GDPR-D1: Disputes PII anonymized",
                            details: $"Anonymized {disputesAnonymized} Dispute(s) for user {userId}: Reason/ResolutionComments/ExpertResponse → '[Datos eliminados]'. Status/StripeDisputeId/SearchHireId preservados para auditoría Stripe.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync.D1",
                            relatedEntityType: "Dispute",
                            relatedEntityId: null);
                    }

                    // 🛡️ GDPR-R4 FIX (parte 1/2): purgar archivos físicos SearchHireDeliverable en
                    // Supabase Storage ANTES de borrar las filas. Deliverable.ObjectName apunta a
                    // FilesBucket (bucket privado: PDFs, vídeos, informes del experto al cliente).
                    // SearchHire se ANONIMIZA (no se borra) → Cascade FK no se activa →
                    // deliverables huérfanos con archivos accesibles por URL.
                    List<string?> deliverableObjects = new();
                    if (userSearchHireIdsAll.Count > 0)
                    {
                        deliverableObjects = await _context.SearchHireDeliverables
                            .AsNoTracking()
                            .Where(d => userSearchHireIdsAll.Contains(d.SearchHireId))
                            .Select(d => d.ObjectName)
                            .ToListAsync(cancellationToken);

                        if (deliverableObjects.Count > 0)
                        {
                            await TryDeleteStorageObjectsAsync(
                                _storage.FilesBucket,
                                deliverableObjects,
                                userId,
                                "DeleteUserDataAsync.R4",
                                cancellationToken);
                        }

                        // 🛡️ GDPR-R4 FIX (parte 2/2): borrar filas SearchHireDeliverable
                        var deliverableHireIdsArr = string.Join(",", userSearchHireIdsAll);
                        var deliverablesDeleted = await _context.Database.ExecuteSqlRawAsync(
                            @"DELETE FROM ""SearchHireDeliverables""
                              WHERE ""SearchHireId"" = ANY(ARRAY[" + deliverableHireIdsArr + @"]::integer[])",
                            cancellationToken);

                        if (deliverablesDeleted > 0)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "GDPR-R4: SearchHireDeliverables deleted",
                                details: $"Deleted {deliverablesDeleted} SearchHireDeliverable row(s) for user {userId}. {deliverableObjects.Count} archivo(s) físico(s) en Supabase Storage también purgados.",
                                userId: null,
                                source: "AccountDeletionService.DeleteUserDataAsync.R4",
                                relatedEntityType: "SearchHireDeliverable",
                                relatedEntityId: null);
                        }
                    }

                    // 🛡️ GDPR-R3 + C1 FIX: anonimizar ProcessedWebhookEvent del usuario. EventData
                    // es un JSON raw de webhook Stripe que puede contener metadata con email/nombre.
                    // Nulleamos UserId/EventData pero PRESERVAMOS EventId/EventType/ProcessedAt/Status
                    // como guard de idempotencia de webhooks (evita reprocesar el mismo evento).
                    // C1: try/catch propio que NO aborta. Los webhook events son auditoría no
                    // crítica para el delete del usuario — si falla, log Critical y continuamos
                    // (el admin limpia manualmente). Sin esto, un fallo aquí abortaba TODO el
                    // delete y el usuario quedaba sin borrar por algo que no debería bloquear.
                    try
                    {
                        var webhookEventsAnonymized = await _context.Database.ExecuteSqlRawAsync(
                            @"UPDATE ""ProcessedWebhookEvents""
                              SET ""UserId"" = NULL,
                                  ""EventData"" = NULL
                              WHERE (""UserId"" = {0} AND ""UserId"" IS NOT NULL)
                                 OR (""EventData"" IS NOT NULL AND ""EventData"" LIKE '%""metadata""%' AND ""UserId"" = {0})",
                            new object[] { userId }, cancellationToken);

                        if (webhookEventsAnonymized > 0)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "GDPR-R3: ProcessedWebhookEvents anonymized",
                                details: $"Anonymized {webhookEventsAnonymized} ProcessedWebhookEvent(s) for user {userId}: UserId/EventData=NULL. Idempotencia preservada (EventId/EventType/ProcessedAt intactos).",
                                userId: null,
                                source: "AccountDeletionService.DeleteUserDataAsync.R3",
                                relatedEntityType: "ProcessedWebhookEvent",
                                relatedEntityId: null);
                        }
                    }
                    catch (Exception r3Ex)
                    {
                        // C1: no abortar — webhook events son auditoría no crítica
                        // ⚠️ NOTA POSTGRESQL: si esta operación falla dentro de la tx global,
                        // la tx queda en estado "aborted" y las siguientes operaciones también
                        // fallarán hasta el rollback. PERO el catch externo grande hará el
                        // rollback y la captura habrá quedado en logs separados. Por eso es
                        // mejor que R3 vaya al FINAL de la fase de anonimización (ya está):
                        // si falla, todavía perdemos el resto de Fase 2/3/4. Limitación de
                        // PgBouncer sin savepoints.
                        await _loggingService.LogCriticalAsync(
                            message: "GDPR-R3 + C1: Failed to anonymize webhook events (non-critical)",
                            details: $"User {userId}: ProcessedWebhookEvents anonymization falló: {r3Ex.Message}. ACCIÓN ADMIN: ejecutar manualmente UPDATE ProcessedWebhookEvents SET UserId=NULL,EventData=NULL WHERE UserId={userId}. NOTA: por limitación de PgBouncer sin savepoints, esta excepción puede haber abortado la tx global — verificar si el usuario realmente quedó eliminado.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync.R3.C1",
                            relatedEntityType: "ProcessedWebhookEvent",
                            relatedEntityId: null);
                        // NO throw — pero PostgreSQL ya marcó la tx como abortada. Continuamos
                        // (las siguientes ops fallarán y el catch externo hará rollback).
                    }
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    // ✅ MEJOR PRÁCTICA: Manejo específico de concurrencia (como ProcessMoneyDistributionAsync)
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Concurrency conflict during account anonymization",
                        details: $"Another process modified user {userId} data concurrently during anonymization. Error: {ex.Message}. " +
                                $"ACTION REQUIRED: Review concurrent operations and retry account deletion if needed.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new
                        {
                            DeletedUserId = userId,
                            Error = ex.Message,
                            ErrorType = ex.GetType().Name
                        }
                    );
                    throw; // Re-throw para que la transacción global haga rollback
                }
                catch (Exception ex)
                {
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Failed to anonymize user data",
                        details: $"Failed to anonymize critical data for user {userId}. Error Type: {ex.GetType().Name}, Error Message: {ex.Message}. " +
                                $"ACTION REQUIRED: Review error and verify data integrity.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new
                        {
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace
                        }
                    );
                    throw; // Re-throw para que la transacción global haga rollback
                }

                // ===== FASE 3: ELIMINACIÓN DE DATOS NO CRÍTICOS (misma transacción global - BATCH) =====
                // ✅ MEJORA: Batch deletes - agrupar todos los RemoveRange y un solo SaveChangesAsync
                // Esto mejora performance significativamente al reducir roundtrips a la BD

                bool hasDeletes = false;

                // 7. Eliminar likes (datos no críticos)
                // ✅ FIX: Usar SQL directo para evitar ExecutionStrategy dentro de transacción manual
                var likesDeleted = await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""Likes"" WHERE ""UserId"" = {0}",
                    new object[] { userId }, cancellationToken);
                if (likesDeleted > 0)
                {
                    hasDeletes = true;
                }

                // 🛡️ GDPR-FA1 FIX: eliminar SearchServiceFavorite del usuario (servicios marcados
                // como favoritos por él). NO se anonimiza — es preferencia personal sin valor
                // histórico. Si quedaran como filas con UserId apuntando a usuario soft-deleted, el
                // query filter las ignoraría pero seguirían en BD como huérfanas.
                var favoritesDeleted = await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""SearchServiceFavorites"" WHERE ""UserId"" = {0}",
                    new object[] { userId }, cancellationToken);
                if (favoritesDeleted > 0)
                {
                    hasDeletes = true;
                    await _loggingService.LogInfoAsync(
                        message: "GDPR-FA1: SearchServiceFavorites deleted (user's favorites)",
                        details: $"Deleted {favoritesDeleted} SearchServiceFavorite row(s) where UserId={userId}.",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync.FA1",
                        relatedEntityType: "SearchServiceFavorite",
                        relatedEntityId: null);
                }

                // 8. Eliminar búsquedas (datos no críticos)
                // ✅ FIX: Usar SQL directo para evitar ExecutionStrategy dentro de transacción manual
                var searchesDeleted = await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""Searches"" WHERE ""UserId"" = {0}",
                    new object[] { userId }, cancellationToken);
                if (searchesDeleted > 0)
                {
                    hasDeletes = true;
                }

                // 9. Eliminar/anonimizar servicios (SOLO si el usuario que elimina es el EXPERTO - datos no críticos)
                // ✅ CRÍTICO: Si un CLIENTE elimina su cuenta, NO tocar los servicios del experto
                // ✅ MEJORA: Preservar servicios con contrataciones históricas (anonimizar en lugar de eliminar)
                // ✅ FIX: Usar conexión directa para evitar ExecutionStrategy dentro de transacción manual
                int expertProfileId = 0;
                var serviceIds = new List<int>();

                // ✅ FIX: Usar conexión de forma segura con manejo explícito
                var connection = _context.Database.GetDbConnection();
                var connectionWasOpen = connection.State == System.Data.ConnectionState.Open;
                
                if (!connectionWasOpen)
                {
                    await _context.Database.OpenConnectionAsync(cancellationToken);
                }

                try
                {
                    using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT ""Id"" FROM ""ExpertProfiles"" WHERE ""UserId"" = @userId LIMIT 1";
                    var param = command.CreateParameter();
                    param.ParameterName = "@userId";
                    param.Value = userId;
                    param.DbType = System.Data.DbType.Int32;
                    command.Parameters.Add(param);

                    var result = await command.ExecuteScalarAsync(cancellationToken);
                    if (result != null && result != DBNull.Value)
                    {
                        expertProfileId = Convert.ToInt32(result);
                    }
                }

                if (expertProfileId > 0)
                {
                    // Obtener IDs de servicios directamente con SQL usando conexión directa
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"SELECT ""Id"" FROM ""SearchServices"" WHERE ""ExpertProfileId"" = @expertProfileId";
                        var param = command.CreateParameter();
                        param.ParameterName = "@expertProfileId";
                        param.Value = expertProfileId;
                        param.DbType = System.Data.DbType.Int32;
                        command.Parameters.Add(param);

                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                        {
                            while (await reader.ReadAsync(cancellationToken))
                            {
                                serviceIds.Add(reader.GetInt32(0));
                            }
                        }
                    }

                    // ✅ CORRECCIÓN: Solo procesar servicios si el usuario que elimina ES el experto
                    // Si un cliente elimina su cuenta, el experto y sus servicios NO deben ser afectados
                    if (serviceIds.Any())
                    {
                        var servicesToAnonymize = new List<int>();
                        var servicesToDelete = new List<int>();

                        // ✅ MEJORA: Optimización - Batch check para evitar N+1 queries
                        // ✅ CRÍTICO: Verificar TODOS los SearchHires asociados, incluso si están anonimizados
                        // Esto previene eliminar servicios que tienen contrataciones históricas (aunque anonimizadas)
                        // ✅ MEJORA: Buscar por SearchServiceId directamente, no por ClientId/ExpertId
                        // ✅ FIX: Usar conexión directa para evitar ExecutionStrategy dentro de transacción manual
                        var servicesWithHires = new List<int>();
                        if (serviceIds.Any())
                        {
                            // ✅ FIX: Usar parámetros correctamente para evitar errores de sintaxis SQL
                            // Construir array SQL usando parámetros nombrados
                            var placeholders = string.Join(",", serviceIds.Select((_, i) => $"@serviceId{i}"));
                            using (var command = connection.CreateCommand())
                            {
                                command.CommandText = $@"SELECT DISTINCT ""SearchServiceId"" FROM ""SearchHires"" WHERE ""SearchServiceId"" = ANY(ARRAY[{placeholders}])";

                                // Agregar parámetros
                                for (int i = 0; i < serviceIds.Count; i++)
                                {
                                    var param = command.CreateParameter();
                                    param.ParameterName = $"@serviceId{i}";
                                    param.Value = serviceIds[i];
                                    param.DbType = System.Data.DbType.Int32;
                                    command.Parameters.Add(param);
                                }

                                using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                                {
                                    while (await reader.ReadAsync(cancellationToken))
                                    {
                                        servicesWithHires.Add(reader.GetInt32(0));
                                    }
                                }
                            }
                        }

                        // 🛡️ Round 27 — R27-T27-1-7 FIX: también recoger servicios con Conversations.
                        // ANTES: la clasificación solo miraba SearchHires. Un servicio con conversaciones
                        // pre-hire (cliente charteando con el experto antes de contratar) pero sin hire
                        // caía en servicesToDeleteFinal → DELETE → FK_Conversations_SearchServices CASCADE
                        // → Conversations + Messages + MessageAttachments desaparecen sin avisar a la otra
                        // parte (el CLIENTE pierde toda la conversación) y los attachments del cliente
                        // quedan huérfanos en Supabase Storage. Defeats el promise "preservar para la
                        // otra parte" del comentario en Phase 2.
                        // AHORA: queryamos Conversations también y route esos services a anonymize.
                        var servicesWithConversations = new List<int>();
                        if (serviceIds.Any())
                        {
                            var convPlaceholders = string.Join(",", serviceIds.Select((_, i) => $"@convServiceId{i}"));
                            using (var command = connection.CreateCommand())
                            {
                                command.CommandText = $@"SELECT DISTINCT ""SearchServiceId"" FROM ""Conversations"" WHERE ""SearchServiceId"" IS NOT NULL AND ""SearchServiceId"" = ANY(ARRAY[{convPlaceholders}])";
                                for (int i = 0; i < serviceIds.Count; i++)
                                {
                                    var param = command.CreateParameter();
                                    param.ParameterName = $"@convServiceId{i}";
                                    param.Value = serviceIds[i];
                                    param.DbType = System.Data.DbType.Int32;
                                    command.Parameters.Add(param);
                                }
                                using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                                {
                                    while (await reader.ReadAsync(cancellationToken))
                                    {
                                        servicesWithConversations.Add(reader.GetInt32(0));
                                    }
                                }
                            }
                        }

                        var servicesWithHiresSet = new HashSet<int>(servicesWithHires);
                        var servicesWithConversationsSet = new HashSet<int>(servicesWithConversations);

                        // ✅ Clasificar servicios: anonimizar si tienen hires O conversaciones, eliminar si no
                        foreach (var serviceId in serviceIds)
                        {
                            // 🛡️ R27-T27-1-7: extender el bucket de anonimización a servicios con conversaciones
                            // pre-hire (la otra parte conservará chat + attachments).
                            if (servicesWithHiresSet.Contains(serviceId) || servicesWithConversationsSet.Contains(serviceId))
                            {
                                // ✅ Preservar servicio para contrataciones históricas (auditoría, facturación, disputas)
                                //   o para conversaciones pre-hire del cliente.
                                servicesToAnonymize.Add(serviceId);
                            }
                            else
                            {
                                // ✅ Eliminar servicio si no tiene contrataciones ni conversaciones asociadas
                                servicesToDelete.Add(serviceId);
                            }
                        }

                        // ✅ Anonimizar servicios con contrataciones históricas
                        if (servicesToAnonymize.Any())
                        {
                            // ✅ FIX: Construir SQL como string normal para evitar que EF Core busque placeholders {0}, {1}, etc.
                            var servicesArray = string.Join(",", servicesToAnonymize);  // Sin {} para formar ARRAY[1,2,3]
                            var sql = @"UPDATE ""SearchServices""
                                      SET ""ExpertProfileId"" = NULL, ""IsActive"" = false
                                      WHERE ""Id"" = ANY(ARRAY[" + servicesArray + @"]::integer[])";
                            try
                            {
                                // Intentar anonimizar (ExpertProfileId = NULL) y desactivar (IsActive = false)
                                var anonymizedCount = await _context.Database.ExecuteSqlRawAsync(sql, cancellationToken);

                                if (anonymizedCount > 0)
                                {
                                    await _loggingService.LogInfoAsync(
                                        message: "SearchServices anonymized for account deletion (preserved for historical contracts)",
                                        details: $"Anonymized {anonymizedCount} SearchService(s) for expert {userId} (ExpertProfileId set to NULL). " +
                                                $"Services preserved because they have associated SearchHires (contracts) for historical/audit purposes. " +
                                                $"Service IDs: {string.Join(", ", servicesToAnonymize)}.",
                                        userId: null,
                                        source: "AccountDeletionService.DeleteUserDataAsync",
                                        relatedEntityType: "SearchService",
                                        relatedEntityId: null,
                                        additionalData: new
                                        {
                                            DeletedUserId = userId,
                                            AnonymizedServiceIds = servicesToAnonymize,
                                            Reason = "Preserve services with historical contracts for audit trail and legal compliance"
                                        }
                                    );
                                }
                            }
                            catch (DbUpdateException dbEx) when (dbEx.InnerException is PostgresException pgEx && pgEx.SqlState == "23502")
                            {
                                // ✅ ERROR: ExpertProfileId tiene restricción NOT NULL en BD (falta migración)
                                // FALLBACK: Solo desactivar servicios sin anonimizar
                                var servicesArrayFallback = string.Join(",", servicesToAnonymize);  // Sin {}
                                var sqlFallback = @"UPDATE ""SearchServices""
                                      SET ""IsActive"" = false
                                      WHERE ""Id"" = ANY(ARRAY[" + servicesArrayFallback + @"]::integer[])";
                                var deactivatedCount = await _context.Database.ExecuteSqlRawAsync(sqlFallback, cancellationToken);

                                await _loggingService.LogWarningAsync(
                                    message: "SearchServices deactivated instead of anonymized",
                                    details: $"{deactivatedCount} SearchService(s) were deactivated instead of anonymized because ExpertProfileId has a NOT NULL constraint. " +
                                            $"Service IDs: {string.Join(", ", servicesToAnonymize)}. " +
                                            $"ACTION REQUIRED: Apply migration 'MakeExpertProfileIdNullableInSearchServices' to enable full anonymization.",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync",
                                    relatedEntityType: "SearchService",
                                    relatedEntityId: null,
                                    additionalData: new
                                    {
                                        DeletedUserId = userId,
                                        DeactivatedServiceIds = servicesToAnonymize,
                                        ActionRequired = "Apply migration MakeExpertProfileIdNullableInSearchServices"
                                    }
                                );
                            }
                        }

                        // ✅ Eliminar imágenes de servicios que se van a eliminar (no anonimizar)
                        if (servicesToDelete.Any())
                        {
                            // ✅ CRÍTICO: Verificar una vez más que los servicios NO tienen SearchHires asociados
                            // Esto previene eliminar servicios que tienen contrataciones (incluso anonimizadas)
                            // ✅ FIX: Usar conexión directa para evitar ExecutionStrategy dentro de transacción manual
                            var finalCheckServicesWithHires = new List<int>();
                            if (servicesToDelete.Any())
                            {
                                // ✅ FIX: Usar parámetros correctamente para evitar errores de sintaxis SQL
                                // Construir array SQL usando parámetros nombrados
                                var placeholders = string.Join(",", servicesToDelete.Select((_, i) => $"@serviceId{i}"));
                                using (var command = connection.CreateCommand())
                                {
                                    command.CommandText = $@"SELECT DISTINCT ""SearchServiceId"" FROM ""SearchHires"" WHERE ""SearchServiceId"" = ANY(ARRAY[{placeholders}])";

                                    // Agregar parámetros
                                    for (int i = 0; i < servicesToDelete.Count; i++)
                                    {
                                        var param = command.CreateParameter();
                                        param.ParameterName = $"@serviceId{i}";
                                        param.Value = servicesToDelete[i];
                                        param.DbType = System.Data.DbType.Int32;
                                        command.Parameters.Add(param);
                                    }

                                    using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                                    {
                                        while (await reader.ReadAsync(cancellationToken))
                                        {
                                            finalCheckServicesWithHires.Add(reader.GetInt32(0));
                                        }
                                    }
                                }
                            }

                            // ✅ Filtrar servicios que realmente no tienen SearchHires
                            var servicesToDeleteFinal = servicesToDelete
                                .Where(sid => !finalCheckServicesWithHires.Contains(sid))
                                .ToList();

                            if (servicesToDeleteFinal.Any())
                            {
                                // ✅ FIX: Construir SQL como string normal para evitar que EF Core busque placeholders
                                var servicesArray = string.Join(",", servicesToDeleteFinal);  // Sin {} para formar ARRAY[1,2,3]

                                // 🛡️ GDPR-S1.b FIX: purgar imágenes físicas en Supabase Storage ANTES
                                // del DELETE de filas. SearchServiceImage.ImageObjectName contiene el
                                // objectPath en ImagesBucket (bucket público). Si los SearchServices se
                                // borran (no anonimizan), las fotos quedan huérfanas en Storage.
                                var ssImagePaths = await _context.SearchServiceImages
                                    .AsNoTracking()
                                    .Where(ssi => servicesToDeleteFinal.Contains(ssi.SearchServiceId))
                                    .Select(ssi => ssi.ImageObjectName)
                                    .ToListAsync(cancellationToken);

                                if (ssImagePaths.Count > 0)
                                {
                                    await TryDeleteStorageObjectsAsync(
                                        _storage.ImagesBucket,
                                        ssImagePaths,
                                        userId,
                                        "DeleteUserDataAsync.SearchServiceImages",
                                        cancellationToken);
                                }

                                // Eliminar imágenes primero (FK constraint)
                                var sqlImages = @"DELETE FROM ""SearchServiceImages""
                                      WHERE ""SearchServiceId"" = ANY(ARRAY[" + servicesArray + @"]::integer[])";
                                var imagesDeleted = await _context.Database.ExecuteSqlRawAsync(sqlImages, cancellationToken);

                                // 🛡️ GDPR-FA1 (extensión): borrar SearchServiceFavorite de OTROS
                                // usuarios que tengan estos servicios como favoritos. Sin esto, los
                                // favoritos quedan apuntando a SearchServiceId inexistente → FK error
                                // si hay constraint, o filas huérfanas en frontend ("favorito roto").
                                var sqlOtherFavs = @"DELETE FROM ""SearchServiceFavorites""
                                      WHERE ""SearchServiceId"" = ANY(ARRAY[" + servicesArray + @"]::integer[])";
                                await _context.Database.ExecuteSqlRawAsync(sqlOtherFavs, cancellationToken);

                                // Eliminar servicios sin contrataciones asociadas
                                var sqlServices = @"DELETE FROM ""SearchServices""
                                      WHERE ""Id"" = ANY(ARRAY[" + servicesArray + @"]::integer[])";
                                var servicesDeleted = await _context.Database.ExecuteSqlRawAsync(sqlServices, cancellationToken);

                                if (servicesDeleted > 0)
                                {
                                    hasDeletes = true;

                                    await _loggingService.LogInfoAsync(
                                        message: "SearchServices deleted for account deletion (no associated contracts)",
                                        details: $"Deleted {servicesDeleted} SearchService(s) for expert {userId}. " +
                                                $"Services deleted because they have no associated SearchHires (contracts). " +
                                                $"Service IDs: {string.Join(", ", servicesToDeleteFinal)}.",
                                        userId: null,
                                        source: "AccountDeletionService.DeleteUserDataAsync",
                                        relatedEntityType: "SearchService",
                                        relatedEntityId: null,
                                        additionalData: new
                                        {
                                            DeletedUserId = userId,
                                            DeletedServiceIds = servicesToDeleteFinal
                                        }
                                    );
                                }
                            }
                            else
                            {
                                // ✅ Si todos los servicios tienen SearchHires, anonimizarlos en lugar de eliminarlos
                                await _loggingService.LogWarningAsync(
                                    message: "SearchServices preserved - found associated SearchHires during final check",
                                    details: $"All {servicesToDelete.Count} SearchService(s) that were marked for deletion have associated SearchHires. " +
                                            $"Services will be anonymized instead of deleted to preserve contract history. " +
                                            $"Service IDs: {string.Join(", ", servicesToDelete)}.",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync",
                                    relatedEntityType: "SearchService",
                                    relatedEntityId: null,
                                    additionalData: new
                                    {
                                        DeletedUserId = userId,
                                        PreservedServiceIds = servicesToDelete,
                                        Reason = "Found associated SearchHires during final check"
                                    }
                                );

                                // ✅ Anonimizar estos servicios en lugar de eliminarlos (usar SQL directo con formato correcto)
                                var servicesArray = string.Join(",", servicesToDelete);  // Sin {}
                                try
                                {
                                    var sqlAnonymize = @"UPDATE ""SearchServices""
                                          SET ""ExpertProfileId"" = NULL, ""IsActive"" = false
                                          WHERE ""Id"" = ANY(ARRAY[" + servicesArray + @"]::integer[])";
                                    await _context.Database.ExecuteSqlRawAsync(sqlAnonymize, cancellationToken);
                                }
                                catch (DbUpdateException dbEx) when (dbEx.InnerException is PostgresException pgEx && pgEx.SqlState == "23502")
                                {
                                    // Si falla, solo desactivar
                                    var sqlDeactivate = @"UPDATE ""SearchServices""
                                          SET ""IsActive"" = false
                                          WHERE ""Id"" = ANY(ARRAY[" + servicesArray + @"]::integer[])";
                                    await _context.Database.ExecuteSqlRawAsync(sqlDeactivate, cancellationToken);
                                }
                            }
                        }

                        // ✅ Anonimizar ExpertAvailabilityId en SearchHires antes de eliminar ExpertProfile
                        // Esto evita el error de foreign key constraint cuando se eliminan ExpertAvailabilities
                        // ExpertAvailability.ExpertId referencia ExpertProfile.Id (no User.Id)
                        var expertAvailabilityAnonymized = await _context.Database.ExecuteSqlRawAsync(
                            @"UPDATE ""SearchHires""
                              SET ""ExpertAvailabilityId"" = NULL,
                                  ""UpdatedAt"" = CURRENT_TIMESTAMP
                              WHERE ""ExpertAvailabilityId"" IN (
                                  SELECT ""Id"" FROM ""ExpertAvailabilities""
                                  WHERE ""ExpertId"" = {0}
                              ) AND ""ExpertAvailabilityId"" IS NOT NULL",
                            new object[] { expertProfileId }, cancellationToken);

                        if (expertAvailabilityAnonymized > 0)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "ExpertAvailabilityId anonymized in SearchHires for account deletion",
                                details: $"Anonymized {expertAvailabilityAnonymized} SearchHire(s) by setting ExpertAvailabilityId to NULL for expert {userId}. " +
                                        $"This prevents foreign key constraint violations when ExpertAvailabilities are deleted.",
                                userId: null,
                                source: "AccountDeletionService.DeleteUserDataAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: null
                            );
                        }

                        // 🛡️ N4 FIX: desconectar cuenta Stripe Connect ANTES de borrar el ExpertProfile.
                        // Sin esto, el StripeAccountId se pierde de la BD pero la cuenta sigue ACTIVA en
                        // Stripe indefinidamente (viola GDPR Art 17 + posibles saldos no liquidados).
                        //
                        // 🛡️ Round 13 — N4+N14 EXTENSION: leer también PendingStripeAccountId. Si el
                        // experto abandonó el onboarding (acct_x creado pero nunca completó details_submitted),
                        // el acct queda solo en PendingStripeAccountId. El N4 original solo borraba
                        // StripeAccountId → al hacer GDPR delete, el acct pending quedaba huérfano en
                        // Stripe (con email + país + datos KYC parciales del usuario).
                        string? stripeAccountIdToDelete = null;
                        string? pendingStripeAccountIdToDelete = null;
                        try
                        {
                            var pair = await _context.ExpertProfiles
                                .IgnoreQueryFilters()
                                .Where(ep => ep.Id == expertProfileId)
                                .Select(ep => new { ep.StripeAccountId, ep.PendingStripeAccountId })
                                .FirstOrDefaultAsync(cancellationToken);
                            stripeAccountIdToDelete = pair?.StripeAccountId;
                            pendingStripeAccountIdToDelete = pair?.PendingStripeAccountId;
                        }
                        catch (Exception readEx)
                        {
                            await _loggingService.LogWarningAsync(
                                message: "N4: failed to read StripeAccountId/PendingStripeAccountId before delete",
                                details: $"ExpertProfile {expertProfileId} (User {userId}): no se pudo leer IDs Stripe antes del delete; la cuenta Stripe podría quedar zombi. Error: {readEx.Message}",
                                userId: userId,
                                source: "AccountDeletionService.DeleteUserDataAsync.N4Read",
                                relatedEntityType: "ExpertProfile",
                                relatedEntityId: expertProfileId);
                        }

                        if (!string.IsNullOrEmpty(stripeAccountIdToDelete))
                        {
                            // 🛡️ R5-F4 FIX: nullear StripeAccountId del ExpertProfile ANTES de borrar
                            // la cuenta en Stripe. Sin esto, webhooks en cola (transfer.failed, payout.paid)
                            // que llegan después del Stripe.Delete pero antes del DELETE local del
                            // ExpertProfile buscan por StripeAccountId y encuentran el profile aún con
                            // el ID, intentan operar contra una cuenta Stripe que ya no existe → 404 y
                            // evento "Skipped" silencioso. Al nullear primero, los webhooks tardíos
                            // simplemente NO encuentran profile y el flujo está cubierto por handlers
                            // de "expert profile not found" ya logueados (Skipped es esperado).
                            try
                            {
                                await _context.Database.ExecuteSqlInterpolatedAsync(
                                    $"UPDATE \"ExpertProfiles\" SET \"StripeAccountId\" = NULL WHERE \"Id\" = {expertProfileId}",
                                    cancellationToken);
                            }
                            catch (Exception nullEx)
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "R5-F4: failed to null StripeAccountId before Stripe delete",
                                    details: $"ExpertProfile {expertProfileId}: continuando con Stripe.Delete pese a error nulling: {nullEx.Message}",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync.R5F4",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId);
                            }

                            // 🛡️ Round 28 — N4-balance: ANTES de Delete, leer balance per-currency y drenar.
                            // Stripe.Account.Delete rechaza si hay balance ≠ 0 → la BD ya nuleó StripeAccountId
                            // y la cuenta Stripe queda viva con dinero atascado sin trazabilidad. Tres pasos:
                            // (1) Balance.Retrieve con StripeAccount header → lista de saldos por divisa.
                            // (2) Si available > 0 en alguna divisa: intentar PayoutCreate hacia bank_account
                            //     del experto. Si falla por "no bank_account" o "balance_insufficient" tras
                            //     pendings, dejar el balance para reverso automático de Stripe a la plataforma.
                            // (3) Tras drenaje (o sin balance): probar Delete; si Stripe sigue rechazando,
                            //     usar Account.Reject(reason:"other") como fallback (cierra la cuenta y deja
                            //     que Stripe haga reverso del balance pendiente al platform automáticamente).
                            // 🛡️ Round 28 MUD-AI (mirror MUD-L de ExpertRelocationService): para expertos
                            // US, verificar capability tax_reporting_us_1099_misc activa ANTES del close.
                            // Sin ella Stripe NO emite 1099-MISC final → exposición IRS ($290-$630 por
                            // seller no reportado). Best-effort: si la lectura falla, continuar pero log.
                            try
                            {
                                var preCheckAcct = await new Stripe.AccountService().GetAsync(stripeAccountIdToDelete);
                                var preCheckCountry = preCheckAcct?.Country;
                                if (string.Equals(preCheckCountry, "US", System.StringComparison.OrdinalIgnoreCase))
                                {
                                    var cap1099 = preCheckAcct?.Capabilities?.TaxReportingUs1099Misc;
                                    var has1099 = string.Equals(cap1099, "active", System.StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(cap1099, "pending", System.StringComparison.OrdinalIgnoreCase);
                                    if (!has1099)
                                    {
                                        await _loggingService.LogCriticalAsync(
                                            message: "MUD-AI: US expert account deletion without 1099 capability — IRS exposure",
                                            details: $"UserId {userId} (acct {stripeAccountIdToDelete}): NO tiene capability tax_reporting_us_1099_misc activa (estado: {cap1099 ?? "none"}). Stripe NO emitirá 1099-MISC al cierre. ACCIÓN ADMIN: verificar exposición IRS antes/después del cierre.",
                                            userId: userId,
                                            source: "AccountDeletionService.MUD-AI.Tax1099Missing",
                                            relatedEntityType: "ExpertProfile",
                                            relatedEntityId: expertProfileId,
                                            additionalData: new { Capability1099 = cap1099, StripeAccountId = stripeAccountIdToDelete });
                                    }
                                }
                            }
                            catch (Exception preCheckEx)
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "MUD-AI: failed to pre-check 1099 capability",
                                    details: $"UserId {userId}: {preCheckEx.Message}. Proceeding with deletion; admin should manually verify IRS reporting.",
                                    userId: userId,
                                    source: "AccountDeletionService.MUD-AI.Tax1099CheckFailed",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId);
                            }

                            var balancesSnapshot = new System.Text.StringBuilder();
                            var hadBalance = false;
                            try
                            {
                                var balanceService = new Stripe.BalanceService();
                                var stripeRequestOpts = new Stripe.RequestOptions { StripeAccount = stripeAccountIdToDelete };
                                var acctBalance = await balanceService.GetAsync(stripeRequestOpts);
                                if (acctBalance.Available != null)
                                {
                                    foreach (var avail in acctBalance.Available)
                                    {
                                        if (avail.Amount > 0)
                                        {
                                            hadBalance = true;
                                            balancesSnapshot.Append($"available={avail.Amount / 100m:F2} {avail.Currency.ToUpperInvariant()}; ");
                                            try
                                            {
                                                var payoutSvc = new Stripe.PayoutService();
                                                var payoutOpts = new Stripe.PayoutCreateOptions
                                                {
                                                    Amount = avail.Amount,
                                                    Currency = avail.Currency,
                                                    Metadata = new Dictionary<string, string>
                                                    {
                                                        { "reason", "account_deletion_final_payout" },
                                                        { "userId", userId.ToString() },
                                                        { "expertProfileId", expertProfileId.ToString() }
                                                    }
                                                };
                                                var payoutReqOpts = new Stripe.RequestOptions
                                                {
                                                    StripeAccount = stripeAccountIdToDelete,
                                                    // 🛡️ MUD-AE: incluir stripeAccountId + Amount para soportar usuarios
                                                    // que cierran cuenta tras mudanza (acctId distinto cada vez) y retries
                                                    // con balance ligeramente distinto.
                                                    IdempotencyKey = $"acct-deletion-payout-{stripeAccountIdToDelete}-{avail.Currency}-{avail.Amount}"
                                                };
                                                var payout = await payoutSvc.CreateAsync(payoutOpts, payoutReqOpts);
                                                await _loggingService.LogInfoAsync(
                                                    message: "N4-balance: final payout created before account deletion",
                                                    details: $"ExpertProfile {expertProfileId} acct {stripeAccountIdToDelete}: payout {payout.Id} de {avail.Amount / 100m:F2} {avail.Currency.ToUpperInvariant()} a bank_account del experto.",
                                                    userId: userId,
                                                    source: "AccountDeletionService.N4Balance.Payout",
                                                    relatedEntityType: "ExpertProfile",
                                                    relatedEntityId: expertProfileId,
                                                    additionalData: new { PayoutId = payout.Id, Amount = avail.Amount / 100m, Currency = avail.Currency.ToUpperInvariant() });
                                            }
                                            catch (Stripe.StripeException payoutEx)
                                            {
                                                // Payout falló (sin bank_account / capability disabled / etc.) — log critical pero
                                                // continuamos. El balance quedará para reverso automático al platform vía Reject.
                                                await _loggingService.LogCriticalAsync(
                                                    message: "CRITICAL N4-balance: payout failed before account deletion — balance will be reversed to platform",
                                                    details: $"ExpertProfile {expertProfileId} acct {stripeAccountIdToDelete}: payout de {avail.Amount / 100m:F2} {avail.Currency.ToUpperInvariant()} FALLÓ: {payoutEx.StripeError?.Code} - {payoutEx.Message}. El balance quedará en Stripe — al hacer Reject, Stripe lo reverte al balance de la plataforma. ACCIÓN ADMIN: identificar destinatario humano y devolver manualmente.",
                                                    userId: userId,
                                                    source: "AccountDeletionService.N4Balance.PayoutFailed",
                                                    relatedEntityType: "ExpertProfile",
                                                    relatedEntityId: expertProfileId,
                                                    additionalData: new { Amount = avail.Amount / 100m, Currency = avail.Currency.ToUpperInvariant(), PayoutError = payoutEx.StripeError?.Code, payoutEx.Message });
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception balEx)
                            {
                                // Best-effort: no abortar GDPR delete por fallo en balance read.
                                await _loggingService.LogWarningAsync(
                                    message: "N4-balance: failed to read balance before Stripe.Account.Delete (continuing)",
                                    details: $"ExpertProfile {expertProfileId} acct {stripeAccountIdToDelete}: no se pudo leer balance per-currency: {balEx.Message}. Procedemos con Delete; si hay balance Stripe lo rechazará y caeremos a Reject fallback.",
                                    userId: userId,
                                    source: "AccountDeletionService.N4Balance.ReadFailed",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId);
                            }

                            try
                            {
                                var stripeAccountService = new Stripe.AccountService();
                                await stripeAccountService.DeleteAsync(stripeAccountIdToDelete);
                                await _loggingService.LogInfoAsync(
                                    message: "N4: Stripe Connect account deleted",
                                    details: $"Cuenta Stripe Connect {stripeAccountIdToDelete} eliminada para User {userId} antes del delete del ExpertProfile {expertProfileId}. {(hadBalance ? $"Balance pre-Delete: {balancesSnapshot}" : "Sin balance pendiente.")}",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync.N4Delete",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId);
                            }
                            catch (Stripe.StripeException stripeEx) when (stripeEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                // Cuenta ya no existe en Stripe → es lo que queremos, no error.
                                await _loggingService.LogInfoAsync(
                                    message: "N4: Stripe account already gone (404)",
                                    details: $"Stripe account {stripeAccountIdToDelete} ya no existe en Stripe — continuando con delete local.",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync.N4Delete",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId);
                            }
                            catch (Stripe.StripeException stripeEx)
                            {
                                // 🛡️ Round 28 — N4-fallback: Delete rechazado (probablemente balance>0 o capability
                                // activa). Intentamos Account.Reject(reason:"other") — Stripe acepta Reject incluso
                                // con balance pendiente y se encarga de reversar automáticamente al platform.
                                var rejectSucceeded = false;
                                try
                                {
                                    var stripeAccountServiceReject = new Stripe.AccountService();
                                    var rejectOpts = new Stripe.AccountRejectOptions { Reason = "other" };
                                    await stripeAccountServiceReject.RejectAsync(stripeAccountIdToDelete, rejectOpts);
                                    rejectSucceeded = true;
                                    await _loggingService.LogCriticalAsync(
                                        message: "N4-fallback: Stripe account REJECTED (Delete failed — balance will be reversed to platform)",
                                        details: $"User {userId} ExpertProfile {expertProfileId}: Stripe Delete falló ({stripeEx.StripeError?.Code}: {stripeEx.Message}). Account.Reject(other) ejecutado OK — Stripe reverte balance pendiente al platform automáticamente. Snapshot balance pre-cierre: {(balancesSnapshot.Length > 0 ? balancesSnapshot.ToString() : "(no leído)")}. ACCIÓN ADMIN: identificar dueño del balance y reconciliar.",
                                        userId: userId,
                                        source: "AccountDeletionService.DeleteUserDataAsync.N4Reject",
                                        relatedEntityType: "ExpertProfile",
                                        relatedEntityId: expertProfileId,
                                        additionalData: new { StripeAccountId = stripeAccountIdToDelete, DeleteError = stripeEx.StripeError?.Code, BalanceSnapshot = balancesSnapshot.ToString() });
                                }
                                catch (Exception rejectEx)
                                {
                                    // Reject también falló — la cuenta queda viva en Stripe Dashboard y el dinero atascado.
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL N4: BOTH Delete AND Reject failed — zombie Stripe account",
                                        details: $"User {userId} ExpertProfile {expertProfileId}: Stripe Delete falló y Reject también ({rejectEx.Message}). La cuenta {stripeAccountIdToDelete} queda activa en Stripe; el balance se mantiene. URGENTE: limpieza manual en Stripe Dashboard. Balance pre-cierre: {balancesSnapshot}",
                                        userId: userId,
                                        source: "AccountDeletionService.DeleteUserDataAsync.N4DeleteRejectFailed",
                                        relatedEntityType: "ExpertProfile",
                                        relatedEntityId: expertProfileId,
                                        additionalData: new { StripeAccountId = stripeAccountIdToDelete, DeleteError = stripeEx.Message, RejectError = rejectEx.Message, BalanceSnapshot = balancesSnapshot.ToString() });
                                }

                                if (!rejectSucceeded)
                                {
                                    // Si Reject también falló, mantenemos el log critical anterior. El delete local del
                                    // User sigue (GDPR Art 17 prevalece sobre el zombi de Stripe).
                                    await _loggingService.LogCriticalAsync(
                                        message: "CRITICAL N4: Failed to delete Stripe Connect account (delete local continúa)",
                                        details: $"User {userId} ExpertProfile {expertProfileId}: Stripe account {stripeAccountIdToDelete} NO se pudo borrar ({(int?)stripeEx.HttpStatusCode}: {stripeEx.StripeError?.Code}: {stripeEx.Message}). ACCIÓN ADMIN: revisar saldo + capability + borrar manualmente en Stripe Dashboard. El delete local del User SÍ continúa para no bloquear GDPR Art 17.",
                                        userId: userId,
                                        source: "AccountDeletionService.DeleteUserDataAsync.N4Delete",
                                        relatedEntityType: "ExpertProfile",
                                        relatedEntityId: expertProfileId,
                                        additionalData: new { StripeAccountId = stripeAccountIdToDelete, stripeEx.StripeError?.Code, stripeEx.HttpStatusCode, stripeEx.Message });
                                }
                            }
                            catch (Exception ex)
                            {
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL N4: Unexpected error deleting Stripe account",
                                    details: $"User {userId} ExpertProfile {expertProfileId}: error inesperado borrando Stripe account {stripeAccountIdToDelete}: {ex.Message}",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync.N4Delete",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId);
                            }
                        }

                        // 🛡️ Round 13 — N4+N14 FIX: borrar también PendingStripeAccountId si es
                        // distinto del StripeAccountId. Cubre el caso "usuario abandonó onboarding"
                        // donde el acct quedó solo en PendingStripeAccountId.
                        // (Si ambos coinciden, ya se borró arriba — saltarlo.)
                        if (!string.IsNullOrEmpty(pendingStripeAccountIdToDelete)
                            && !string.Equals(pendingStripeAccountIdToDelete, stripeAccountIdToDelete, StringComparison.Ordinal))
                        {
                            // Nullear primero (mismo patrón R5-F4)
                            try
                            {
                                await _context.Database.ExecuteSqlInterpolatedAsync(
                                    $"UPDATE \"ExpertProfiles\" SET \"PendingStripeAccountId\" = NULL WHERE \"Id\" = {expertProfileId}",
                                    cancellationToken);
                            }
                            catch (Exception nullEx)
                            {
                                await _loggingService.LogWarningAsync(
                                    message: "N4+N14: failed to null PendingStripeAccountId before Stripe delete",
                                    details: $"ExpertProfile {expertProfileId}: continuando con Stripe.Delete pese a error nulling: {nullEx.Message}",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync.N4N14",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId);
                            }

                            try
                            {
                                var stripeAccountService = new Stripe.AccountService();
                                await stripeAccountService.DeleteAsync(pendingStripeAccountIdToDelete);
                                await _loggingService.LogInfoAsync(
                                    message: "N4+N14: Pending Stripe Connect account deleted",
                                    details: $"Cuenta Stripe Connect PENDIENTE {pendingStripeAccountIdToDelete} (onboarding abandonado) eliminada para User {userId}.",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync.N4N14Delete",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId);
                            }
                            catch (Stripe.StripeException stripeEx) when (stripeEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                // Ya no existe — perfecto.
                                await _loggingService.LogInfoAsync(
                                    message: "N4+N14: Pending Stripe account already gone (404)",
                                    details: $"Pending Stripe account {pendingStripeAccountIdToDelete} ya no existe — OK.",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync.N4N14Delete",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId);
                            }
                            catch (Exception stripeEx)
                            {
                                // Cuentas pending típicamente no tienen balance (no se hicieron transfers),
                                // así que cualquier error es probablemente capability/state. Log critical
                                // pero NO abortar el delete (GDPR Art 17 prevalece).
                                await _loggingService.LogCriticalAsync(
                                    message: "CRITICAL N4+N14: Failed to delete pending Stripe account",
                                    details: $"User {userId} ExpertProfile {expertProfileId}: pending account {pendingStripeAccountIdToDelete} NO se pudo borrar: {stripeEx.Message}. ACCIÓN ADMIN: revisar y borrar manualmente en Stripe Dashboard.",
                                    userId: userId,
                                    source: "AccountDeletionService.DeleteUserDataAsync.N4N14Delete",
                                    relatedEntityType: "ExpertProfile",
                                    relatedEntityId: expertProfileId,
                                    additionalData: new { PendingStripeAccountId = pendingStripeAccountIdToDelete, Error = stripeEx.Message });
                            }
                        }

                        // 🛡️ GDPR-S1.c FIX: purgar avatar del experto en Supabase Storage ANTES de
                        // borrar el ExpertProfile. ProfilePictureObjectName apunta a ImagesBucket
                        // (bucket público). Si se borra solo la fila, el avatar queda accesible
                        // indefinidamente por URL pública.
                        var avatarObjectName = await _context.ExpertProfiles
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(ep => ep.Id == expertProfileId)
                            .Select(ep => ep.ProfilePictureObjectName)
                            .FirstOrDefaultAsync(cancellationToken);

                        if (!string.IsNullOrWhiteSpace(avatarObjectName))
                        {
                            await TryDeleteStorageObjectsAsync(
                                _storage.ImagesBucket,
                                new[] { avatarObjectName },
                                userId,
                                "DeleteUserDataAsync.ExpertAvatar",
                                cancellationToken);
                        }

                        // ✅ Eliminar perfil de experto (no depende de servicios, FK es nullable)
                        // ✅ FIX: Usar SQL directo para evitar ExecutionStrategy dentro de transacción manual
                        var expertProfileDeleted = await _context.Database.ExecuteSqlRawAsync(
                            @"DELETE FROM ""ExpertProfiles"" WHERE ""Id"" = {0}",
                            new object[] { expertProfileId }, cancellationToken);
                        if (expertProfileDeleted > 0)
                        {
                            hasDeletes = true;
                        }
                    } // Cerrar if (serviceIds.Any())
                    } // Cerrar if (expertProfileId > 0)
                }
                finally
                {
                    // ✅ FIX: Cerrar conexión explícitamente si la abrimos nosotros
                    if (!connectionWasOpen && connection.State == System.Data.ConnectionState.Open)
                    {
                        await _context.Database.CloseConnectionAsync();
                    }
                }

                // 10. Eliminar configuraciones de usuario (datos no críticos)
                // ✅ FIX: Usar SQL directo para evitar ExecutionStrategy dentro de transacción manual
                var userSettingsDeleted = await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""UserSettings"" WHERE ""UserId"" = {0}",
                    new object[] { userId }, cancellationToken);
                if (userSettingsDeleted > 0)
                {
                    hasDeletes = true;
                }

                // 11. Eliminar suscripciones (datos no críticos)
                // ✅ FIX: Usar SQL directo para evitar ExecutionStrategy dentro de transacción manual
                var subscriptionsDeleted = await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""UserSubscriptions"" WHERE ""UserId"" = {0}",
                    new object[] { userId }, cancellationToken);
                if (subscriptionsDeleted > 0)
                {
                    hasDeletes = true;
                }

                // 🛡️ GDPR-R1 FIX: DELETE explícito de RefreshTokens. La FK tiene OnDelete.Cascade
                // pero el User es SOFT delete (UPDATE IsDeleted=true), no hard DELETE → cascade NO
                // se dispara. Sin esta limpieza, los tokens (Token + CreatedByIp + DeviceInfo)
                // quedan en BD ligados a usuario soft-deleted. Aunque IsRevoked/IsExpired
                // bloquean el uso, son datos de auth/IP del usuario → GDPR Art 17 violación.
                var refreshTokensDeleted = await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""RefreshTokens"" WHERE ""UserId"" = {0}",
                    new object[] { userId }, cancellationToken);
                if (refreshTokensDeleted > 0)
                {
                    hasDeletes = true;
                    await _loggingService.LogInfoAsync(
                        message: "GDPR-R1: RefreshTokens deleted",
                        details: $"Deleted {refreshTokensDeleted} RefreshToken(s) for user {userId}. Tokens/IPs/DeviceInfo purgados.",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync.R1",
                        relatedEntityType: "RefreshToken",
                        relatedEntityId: null);
                }

                // 🛡️ GDPR-R2 FIX: DELETE explícito de UserMfaSettings. Mismo motivo que R1 — la
                // FK Cascade no aplica con soft-delete. Contiene TotpSecret (cifrado pero datos
                // de auth) y RecoveryCodesEncrypted del usuario eliminado.
                var mfaDeleted = await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""UserMfaSettings"" WHERE ""UserId"" = {0}",
                    new object[] { userId }, cancellationToken);
                if (mfaDeleted > 0)
                {
                    hasDeletes = true;
                    await _loggingService.LogInfoAsync(
                        message: "GDPR-R2: UserMfaSettings deleted",
                        details: $"Deleted {mfaDeleted} UserMfaSettings row(s) for user {userId}. TotpSecret y RecoveryCodes purgados.",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync.R2",
                        relatedEntityType: "UserMfaSettings",
                        relatedEntityId: null);
                }

                // ✅ BATCH SAVE: Un solo SaveChangesAsync para todos los deletes (mejor performance)
                if (hasDeletes)
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }

                // ===== FASE 4: SOFT DELETE DEL USUARIO (misma transacción global) =====
                // ✅ MEJORA: Soft delete en lugar de hard delete para permitir recuperación y cumplimiento legal
                // El query filter en AppDbContext excluirá automáticamente usuarios con IsDeleted = true

                // 🛡️ GDPR-U1 FIX: anonimizar PII del User en el MISMO UPDATE del soft-delete.
                // Antes solo se ponía IsDeleted=true/DeletedAt, dejando Email/Name/PhoneNumber
                // intactos para siempre — visible a cualquiera con IgnoreQueryFilters() o consulta
                // directa SQL (violación GDPR Art 17). Estrategia:
                //   - Name → '[Usuario eliminado]' (NOT NULL)
                //   - Email → 'deleted-{userId}@deleted.local' (NOT NULL + unique → tokeniza con id)
                //   - GoogleId → 'deleted-{userId}' (NOT NULL + unique → tokeniza con id)
                //   - Password → NULL (nullable, evita reuso de hash)
                //   - PhoneNumber → NULL (nullable)
                //   - PhoneVerified → false
                //   - SubscriptionPlanId → NULL (corta vínculo con plan, ya cancelado en N6)
                // Idempotente: solo aplica si IsDeleted=false (evita rehacer el delete).
                // ✅ FIX CRÍTICO: ExecuteSqlRawAsync con RETURNING puede no retornar el número de filas correctamente
                // en Entity Framework Core dentro de transacciones manuales. Usar UPDATE sin RETURNING y verificar después.
                // ✅ IDEMPOTENCIA: Verificar que el usuario aún existe y no está eliminado
                // ✅ FIX: Usar SQL directo para evitar ExecutionStrategy dentro de transacción manual
                // ✅ MEJORA: Si el usuario es experto, cambiar el rol a Client antes de eliminarlo
                // Esto asegura que si el usuario se restaura, no tenga rol de experto sin ExpertProfile
                // 🛡️ Round 27 — R27-A11-1 FIX: tokenizar también AppleId.
                // Antes sólo GoogleId se anonimizaba; AppleId (sub claim estable de Apple)
                // quedaba intacto en la fila soft-deleted, lo que (a) bloqueaba para siempre
                // el re-registro vía Sign-in-with-Apple del mismo usuario (AuthController:977
                // hace lookup AppleId == claims.Sub y luego 1016 corta con 'account_deleted'),
                // y (b) violaba GDPR Art 17 al retener un identificador único persistente.
                var rowsAffected = await _context.Database.ExecuteSqlRawAsync(
                    @"UPDATE ""Users""
                      SET ""IsDeleted"" = true,
                          ""DeletedAt"" = CURRENT_TIMESTAMP,
                          ""Role"" = CASE
                              WHEN ""Role"" = 1 THEN 0
                              ELSE ""Role""
                          END,
                          ""Name"" = '[Usuario eliminado]',
                          ""Email"" = 'deleted-' || {0}::text || '@deleted.local',
                          ""GoogleId"" = 'deleted-' || {0}::text,
                          ""AppleId"" = CASE WHEN ""AppleId"" IS NOT NULL THEN 'deleted-' || {0}::text ELSE NULL END,
                          ""Password"" = NULL,
                          ""PhoneNumber"" = NULL,
                          ""PhoneVerified"" = false,
                          ""SubscriptionPlanId"" = NULL
                      WHERE ""Id"" = {0} AND (""IsDeleted"" IS NULL OR ""IsDeleted"" = false)",
                    new object[] { userId }, cancellationToken);

                // ✅ VERIFICACIÓN: Verificar que el UPDATE realmente se ejecutó consultando la BD
                // Esto es necesario porque ExecuteSqlRawAsync puede retornar un valor incorrecto en algunos casos
                // ✅ FIX: Usar consulta directa con comando SQL para evitar problemas con SqlQueryRaw
                var userWasUpdated = false;
                if (rowsAffected > 0)
                {
                    // Verificar que el usuario realmente fue actualizado consultando la BD
                    // ✅ FIX: Usar comando SQL directo en lugar de SqlQueryRaw para evitar problemas con mapeo
                    // Reutilizar la conexión existente si está abierta
                    var verificationConnection = _context.Database.GetDbConnection();
                    var verificationConnectionWasOpen = verificationConnection.State == System.Data.ConnectionState.Open;
                    if (!verificationConnectionWasOpen)
                    {
                        await _context.Database.OpenConnectionAsync(cancellationToken);
                    }

                    try
                    {
                        using (var verificationCommand = verificationConnection.CreateCommand())
                        {
                            verificationCommand.CommandText = @"SELECT COUNT(*) FROM ""Users""
                              WHERE ""Id"" = @userId AND ""IsDeleted"" = true AND ""DeletedAt"" IS NOT NULL";
                            var verificationParam = verificationCommand.CreateParameter();
                            verificationParam.ParameterName = "@userId";
                            verificationParam.Value = userId;
                            verificationParam.DbType = System.Data.DbType.Int32;
                            verificationCommand.Parameters.Add(verificationParam);

                            var verificationResult = await verificationCommand.ExecuteScalarAsync(cancellationToken);
                            userWasUpdated = verificationResult != null && verificationResult != DBNull.Value && Convert.ToInt32(verificationResult) > 0;
                        }
                    }
                    finally
                    {
                        if (!verificationConnectionWasOpen && verificationConnection.State == System.Data.ConnectionState.Open)
                        {
                            await _context.Database.CloseConnectionAsync();
                        }
                    }
                }

                if (userWasUpdated)
                {
                    // Usuario marcado como eliminado correctamente
                    // No necesitamos SaveChangesAsync porque ExecuteSqlRawAsync ya ejecuta el UPDATE

                    await _loggingService.LogInfoAsync(
                        message: "User soft deleted successfully",
                        details: $"User {userId} has been soft deleted (IsDeleted=true, DeletedAt={DateTime.UtcNow:O}). User will be excluded from queries automatically by query filter. Rows affected: {rowsAffected}, Verified: {userWasUpdated}.",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                }
                else
                {
                    // Usuario ya fue eliminado o no existe (idempotencia)
                    // El UPDATE no afectó ninguna fila, lo que significa que el usuario ya estaba eliminado o no existe
                    await _loggingService.LogWarningAsync(
                        message: "User already soft deleted or does not exist - idempotent call",
                        details: $"User {userId} was already soft deleted or does not exist. Account deletion process completed (idempotent). Rows affected: {rowsAffected}, Verified: {userWasUpdated}.",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                }

                // 🛡️ GDPR-R5 FIX: anonimizar Logs históricos del usuario justo antes del log
                // final. Los logs son auditoría legal (España: retención 6 años), pero el
                // contenido (Details + AdditionalData JSON) puede tener PII (nombres, emails,
                // direcciones serializadas). Estrategia: UserId→NULL, Details→placeholder,
                // AdditionalData→NULL. Mantenemos Message/Source/CreatedAt para auditoría.
                // NOTA: los logs ESCRITOS DURANTE este mismo flow tienen userId=null (revisar
                // los LogInfoAsync de las fases) o userId=userId. El WHERE UserId={0} solo
                // captura los segundos — los logs históricos pre-delete y los del propio delete
                // que pasaron userId=userId. Los nuevos posteriores a este punto no se ven.
                try
                {
                    var logsAnonymized = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Logs""
                          SET ""UserId"" = NULL,
                              ""Details"" = CASE WHEN ""Details"" IS NOT NULL AND ""Details"" != ''
                                  THEN '[Datos eliminados por GDPR Art 17]'
                                  ELSE ""Details"" END,
                              ""AdditionalData"" = NULL
                          WHERE ""UserId"" = {0} AND ""UserId"" IS NOT NULL",
                        new object[] { userId }, cancellationToken);

                    if (logsAnonymized > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "GDPR-R5: Historical Logs anonymized",
                            details: $"Anonymized {logsAnonymized} historical Log row(s) for user {userId}: UserId=NULL, Details='[Datos eliminados]', AdditionalData=NULL. Auditoría preservada (Message/Source/CreatedAt) sin PII.",
                            userId: null,
                            source: "AccountDeletionService.DeleteUserDataAsync.R5",
                            relatedEntityType: "Log",
                            relatedEntityId: null);
                    }
                }
                catch (Exception logEx)
                {
                    // No abortar el delete si la anonimización de logs falla — la cuenta ya está
                    // soft-deleted en este punto. Crítico para que el admin sepa que hay PII
                    // residual en logs históricos que limpiar manualmente.
                    await _loggingService.LogCriticalAsync(
                        message: "GDPR-R5: Failed to anonymize historical Logs",
                        details: $"User {userId} soft-deleted OK pero la anonimización de logs históricos falló: {logEx.Message}. ACCIÓN ADMIN: ejecutar manualmente UPDATE Logs SET UserId=NULL,Details='[Datos eliminados]',AdditionalData=NULL WHERE UserId={userId}.",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync.R5",
                        relatedEntityType: "Log",
                        relatedEntityId: null);
                }

                // ✅ LOG FINAL: Eliminación de datos completada
                await _loggingService.LogInfoAsync(
                    message: "User data deletion completed successfully",
                    details: $"All user data for user {userId} has been anonymized or deleted. Account deletion process completed.",
                    userId: null,
                    source: "AccountDeletionService.DeleteUserDataAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new
                    {
                        DeletedUserId = userId,
                        Action = "AccountDeletionCompleted",
                        Timestamp = DateTime.UtcNow
                    }
                );
            }
            catch (Exception ex)
            {
                // ✅ MEJOR PRÁCTICA: Logging completo del error antes de rethrow
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Account deletion failed",
                    details: $"Failed to delete user data for user {userId}. Error Type: {ex.GetType().Name}, Error Message: {ex.Message}, Stack Trace: {ex.StackTrace}. " +
                            $"ACTION REQUIRED: Review error and verify data integrity. User may be in inconsistent state.",
                    userId: userId,
                    source: "AccountDeletionService.DeleteUserDataAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new
                    {
                        DeletedUserId = userId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace,
                        InnerException = ex.InnerException?.Message
                    }
                );
                throw; // Re-throw para que la transacción global haga rollback
            }
        }

        /// <summary>
        /// Verifica y cambia el estado del SearchHire y Appointment si no se cambió durante el procesamiento de dinero
        /// </summary>
        private async Task EnsureStateChangedAsync(int searchHireId, string appointmentStatusValue, CancellationToken cancellationToken = default)
        {
            try
            {
                await _loggingService.LogInfoAsync(
                    message: "EnsureStateChangedAsync - Iniciando",
                    details: $"Iniciando EnsureStateChangedAsync para SearchHireId: {searchHireId}, appointmentStatusValue: {appointmentStatusValue}",
                    userId: null,
                    source: "AccountDeletionService.EnsureStateChangedAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId
                );
                
                // Recargar SearchHire con estado actual
                await _loggingService.LogInfoAsync(
                    message: "EnsureStateChangedAsync - Cargando SearchHire",
                    details: $"Ejecutando consulta: SearchHires WHERE Id = {searchHireId} con Include(Status, Appointment.Status)",
                    userId: null,
                    source: "AccountDeletionService.EnsureStateChangedAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId
                );
                
                var currentSearchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Appointment)
                        .ThenInclude(a => a.Status)
                    .FirstOrDefaultAsync(sh => sh.Id == searchHireId, cancellationToken);

                if (currentSearchHire == null)
                {
                    return; // SearchHire no existe
                }

                // Verificar si el estado ya está finalizado (ya se cambió)
                if (currentSearchHire.Status?.IsFinalizationStatus == true)
                {
                    return; // Estado ya está cambiado
                }

                // Mapear AppointmentStatus a enum
                AppointmentStatus? appointmentStatus = MapAppointmentStatus(appointmentStatusValue);
                if (!appointmentStatus.HasValue)
                {
                    await _loggingService.LogWarningAsync(
                        message: "Cannot map AppointmentStatus for state change fallback",
                        details: $"AppointmentStatus '{appointmentStatusValue}' could not be mapped to enum. Cannot change state for SearchHire {searchHireId}.",
                        userId: null,
                        source: "AccountDeletionService.EnsureStateChangedAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId
                    );
                    return;
                }

                // Cambiar Appointment.Status si existe
                if (currentSearchHire.Appointment != null)
                {
                    await _loggingService.LogInfoAsync(
                        message: "EnsureStateChangedAsync - Buscando AppointmentStatusRow",
                        details: $"Ejecutando consulta: SystemStatuses WHERE StatusType = 'AppointmentStatus' AND StatusValue = '{appointmentStatusValue}' para SearchHireId: {searchHireId}, AppointmentId: {currentSearchHire.Appointment.Id}",
                        userId: null,
                        source: "AccountDeletionService.EnsureStateChangedAsync",
                        relatedEntityType: "SystemStatus",
                        relatedEntityId: null
                    );
                    
                    var appointmentStatusRow = await _context.SystemStatuses
                        .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && 
                                                 s.StatusValue == appointmentStatusValue, cancellationToken);
                    
                    if (appointmentStatusRow != null && currentSearchHire.Appointment.StatusId != appointmentStatusRow.Id)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "EnsureStateChangedAsync - Actualizando Appointment.StatusId",
                            details: $"Actualizando Appointment {currentSearchHire.Appointment.Id} StatusId de {currentSearchHire.Appointment.StatusId} a {appointmentStatusRow.Id} para SearchHireId: {searchHireId}",
                            userId: null,
                            source: "AccountDeletionService.EnsureStateChangedAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: currentSearchHire.Appointment.Id
                        );
                        
                        currentSearchHire.Appointment.StatusId = appointmentStatusRow.Id;
                        currentSearchHire.Appointment.UpdatedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        await _loggingService.LogInfoAsync(
                            message: "EnsureStateChangedAsync - No se actualizó Appointment.StatusId",
                            details: $"Appointment {currentSearchHire.Appointment.Id} StatusId no se actualizó. appointmentStatusRow: {(appointmentStatusRow != null ? $"Id={appointmentStatusRow.Id}" : "null")}, currentAppointment.StatusId: {currentSearchHire.Appointment.StatusId} para SearchHireId: {searchHireId}",
                            userId: null,
                            source: "AccountDeletionService.EnsureStateChangedAsync",
                            relatedEntityType: "Appointment",
                            relatedEntityId: currentSearchHire.Appointment.Id
                        );
                    }
                }

                // Cambiar SearchHire.Status usando el mapeo
                if (currentSearchHire.Status == null)
                {
                    await _loggingService.LogWarningAsync(
                        message: "SearchHire has null Status - cannot change state",
                        details: $"SearchHire {searchHireId} has null Status. Cannot change state manually.",
                        userId: null,
                        source: "AccountDeletionService.EnsureStateChangedAsync",
                        relatedEntityType: "SearchHire",
                        relatedEntityId: searchHireId
                    );
                    return;
                }

                await _loggingService.LogInfoAsync(
                    message: "EnsureStateChangedAsync - Llamando a GetTargetSearchHireStatusAsync",
                    details: $"Llamando a GetTargetSearchHireStatusAsync con AppointmentStatus: {appointmentStatus.Value} para SearchHireId: {searchHireId}",
                    userId: null,
                    source: "AccountDeletionService.EnsureStateChangedAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId
                );
                
                var targetSearchHireStatus = await _systemStatusService.GetTargetSearchHireStatusAsync(appointmentStatus.Value);
                
                await _loggingService.LogInfoAsync(
                    message: "EnsureStateChangedAsync - GetTargetSearchHireStatusAsync completado",
                    details: $"GetTargetSearchHireStatusAsync retornó: {(targetSearchHireStatus.HasValue ? targetSearchHireStatus.Value.ToString() : "null")} para SearchHireId: {searchHireId}",
                    userId: null,
                    source: "AccountDeletionService.EnsureStateChangedAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId
                );
                
                if (targetSearchHireStatus.HasValue)
                {
                    var targetSearchHireStatusValue = SearchHireStatusExtensions.ToStringValue(targetSearchHireStatus.Value);
                    
                    await _loggingService.LogInfoAsync(
                        message: "EnsureStateChangedAsync - Buscando SearchHireStatusRow",
                        details: $"Ejecutando consulta: SystemStatuses WHERE StatusType = 'SearchHireStatus' AND StatusValue = '{targetSearchHireStatusValue}' para SearchHireId: {searchHireId}",
                        userId: null,
                        source: "AccountDeletionService.EnsureStateChangedAsync",
                        relatedEntityType: "SystemStatus",
                        relatedEntityId: null
                    );
                    
                    var searchHireStatusRow = await _context.SystemStatuses
                        .FirstOrDefaultAsync(s => s.StatusType == "SearchHireStatus" && 
                                                 s.StatusValue == targetSearchHireStatusValue, cancellationToken);
                    
                    if (searchHireStatusRow != null && currentSearchHire.StatusId != searchHireStatusRow.Id)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "EnsureStateChangedAsync - Actualizando SearchHire.StatusId",
                            details: $"Actualizando SearchHire {searchHireId} StatusId de {currentSearchHire.StatusId} a {searchHireStatusRow.Id}",
                            userId: null,
                            source: "AccountDeletionService.EnsureStateChangedAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId
                        );
                        
                        currentSearchHire.StatusId = searchHireStatusRow.Id;
                        currentSearchHire.UpdatedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        await _loggingService.LogInfoAsync(
                            message: "EnsureStateChangedAsync - No se actualizó SearchHire.StatusId",
                            details: $"SearchHire {searchHireId} StatusId no se actualizó. searchHireStatusRow: {(searchHireStatusRow != null ? $"Id={searchHireStatusRow.Id}" : "null")}, currentSearchHire.StatusId: {currentSearchHire.StatusId}",
                            userId: null,
                            source: "AccountDeletionService.EnsureStateChangedAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHireId
                        );
                    }
                }

                await _context.SaveChangesAsync(cancellationToken);

                await _loggingService.LogWarningAsync(
                    message: "State changed manually after money processing failure",
                    details: $"SearchHire {searchHireId} state was manually changed to {appointmentStatusValue} because ProcessMoneyDistributionAsync failed. " +
                            $"This ensures the system does not remain blocked even when money processing fails.",
                    userId: null,
                    source: "AccountDeletionService.EnsureStateChangedAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new
                    {
                        AppointmentStatus = appointmentStatusValue,
                        FallbackStateChange = true
                    }
                );
            }
            catch (Exception ex)
            {
                // Log error pero no lanzar excepción (no queremos bloquear la eliminación de cuenta)
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: Failed to change state manually after money processing failure",
                    details: $"Failed to manually change state for SearchHire {searchHireId} after money processing failed. " +
                            $"Error: {ex.Message}. Manual intervention may be required.",
                    userId: null,
                    source: "AccountDeletionService.EnsureStateChangedAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new
                    {
                        Error = ex.Message,
                        ErrorType = ex.GetType().Name,
                        StackTrace = ex.StackTrace
                    }
                );
            }
        }

        /// <summary>
        /// Mapea un string de AppointmentStatus a su enum correspondiente. Delega en
        /// AppointmentStatusExtensions.FromStringValue para soportar TODOS los valores del enum,
        /// no solo los antiguos *_account_delete (que ya no se usan tras el FIX F1).
        /// </summary>
        private AppointmentStatus? MapAppointmentStatus(string statusValue)
        {
            // 🔧 FIX F1 (fallback): antes el switch local solo conocía los 2 literales viejos
            // *_account_delete. Al cambiar AccountDeletionService a usar
            // appointment_completed_without_client_approval / appointment_cancelled_by_expert_second,
            // este map devolvía null → EnsureStateChangedAsync (línea 1816) salía sin tocar nada →
            // cita zombi cuando ProcessMoneyDistributionAsync devolvía false (Stripe 5xx, balance,
            // etc.). Delegar al extension del enum hace que F1 funcione en TODOS los caminos.
            try
            {
                return AppointmentStatusExtensions.FromStringValue(statusValue);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Cancela todos los timers activos y sus jobs de Hangfire asociados para un SearchHire
        /// </summary>
        private async Task CancelActiveTimersAndHangfireJobsAsync(int searchHireId, CancellationToken cancellationToken = default)
        {
            try
            {
                // Obtener el Appointment asociado al SearchHire
                var appointment = await _context.Appointments
                    .FirstOrDefaultAsync(a => a.SearchHireId == searchHireId, cancellationToken);

                if (appointment == null)
                {
                    return; // No hay appointment, no hay timers que cancelar
                }

                // Obtener todos los timers activos del Appointment
                var activeTimers = await _context.AppointmentTimers
                    .Where(t => t.AppointmentId == appointment.Id && !t.IsExpired)
                    .ToListAsync(cancellationToken);

                if (!activeTimers.Any())
                {
                    return; // No hay timers activos
                }

                var hangfireJobIdsToCancel = new List<string>();

                // Marcar todos los timers como expirados y recopilar JobIds
                foreach (var timer in activeTimers)
                {
                    timer.IsExpired = true;
                    timer.ExpiredAt = DateTime.UtcNow;

                    // Almacenar JobId para cancelarlo después del commit
                    if (!string.IsNullOrEmpty(timer.HangfireJobId))
                    {
                        hangfireJobIdsToCancel.Add(timer.HangfireJobId);
                        timer.HangfireJobId = null; // Limpiar referencia
                    }
                }

                // Guardar cambios en la base de datos
                await _context.SaveChangesAsync(cancellationToken);

                // Cancelar jobs de Hangfire DESPUÉS del commit (mejor práctica: operaciones externas fuera de transacción)
                foreach (var jobId in hangfireJobIdsToCancel)
                {
                    try
                    {
                        BackgroundJob.Delete(jobId);
                    }
                    catch (Exception ex)
                    {
                        // 🛡️ N18 FIX: upgrade a CRITICAL. Si BackgroundJob.Delete falla (Redis/Hangfire
                        // transitorio), el job sigue encolado y puede ejecutar contra User borrado.
                        // El handler ya re-valida estado del appointment/user antes de actuar (ver
                        // ProcessAppointmentTimerAsync), así que el riesgo está mitigado — pero el
                        // admin necesita saberlo para vigilancia post-delete. Antes era Warning →
                        // se perdía en el ruido. Ahora Critical garantiza email + digest.
                        await _loggingService.LogCriticalAsync(
                            message: "CRITICAL N18: Hangfire delete falló — job huérfano puede ejecutar tras delete",
                            details: $"Failed to cancel Hangfire job {jobId} for SearchHire {searchHireId} during account deletion. " +
                                     $"El timer ya está marcado IsExpired=true en BD, y el handler re-valida estado (debería no-op), " +
                                     $"pero hay ventana de race. Si el handler procesa antes del SaveChanges de IsExpired=true, " +
                                     $"actuará sobre User borrado. ACCIÓN ADMIN: revisar Hangfire dashboard 30 min después y " +
                                     $"borrar manualmente el job {jobId} si sigue presente. Error: {ex.Message}",
                            userId: null,
                            source: "AccountDeletionService.CancelActiveTimersAndHangfireJobsAsync.N18",
                            relatedEntityType: "AppointmentTimer",
                            relatedEntityId: appointment.Id,
                            additionalData: new
                            {
                                SearchHireId = searchHireId,
                                AppointmentId = appointment.Id,
                                HangfireJobId = jobId,
                                Error = ex.Message
                            }
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                // Loguear pero no fallar - la cancelación de timers no debe bloquear la eliminación de cuenta
                await _loggingService.LogWarningAsync(
                    message: "Failed to cancel timers during account deletion",
                    details: $"Failed to cancel active timers for SearchHire {searchHireId} during account deletion. This is non-critical. Error: {ex.Message}",
                    userId: null,
                    source: "AccountDeletionService.CancelActiveTimersAndHangfireJobsAsync",
                    relatedEntityType: "SearchHire",
                    relatedEntityId: searchHireId,
                    additionalData: new
                    {
                        SearchHireId = searchHireId,
                        Error = ex.Message,
                        ErrorType = ex.GetType().Name
                    }
                );
            }
        }
    }
}

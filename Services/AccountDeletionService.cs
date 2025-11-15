using Microsoft.EntityFrameworkCore;
using Npgsql;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Common;

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

        // Estados de contratación que requieren atención especial
        private readonly string[] _activeStatuses = { "pending", "awaiting_client_decision", "disputed" };

        // ✅ MEJORA: Cache de estados para evitar consultas repetidas a la BD
        private static readonly Dictionary<string, int> _statusCache = new Dictionary<string, int>();
        private static readonly object _cacheLock = new object();
        private static DateTime _cacheLastRefresh = DateTime.MinValue;
        private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(30); // Cache válido por 30 minutos

        // ✅ MEJORA: Timeout para transacciones (5 minutos)
        private static readonly TimeSpan _transactionTimeout = TimeSpan.FromMinutes(5);

        public AccountDeletionService(
            AppDbContext context,
            IAccountDeletionNotificationService notificationService,
            StripeRefundService refundService,
            ILoggingService loggingService)
        {
            _context = context;
            _notificationService = notificationService;
            _refundService = refundService;
            _loggingService = loggingService;
        }

        /// <summary>
        /// Helper method to get StatusId from StatusValue with caching
        /// ✅ MEJORA: Cache de estados para mejorar performance
        /// </summary>
        private async Task<int> GetStatusIdByValueAsync(string statusValue, CancellationToken cancellationToken = default)
        {
            // Verificar si el cache está expirado
            bool cacheExpired = DateTime.UtcNow - _cacheLastRefresh > _cacheExpiration;
            
            lock (_cacheLock)
            {
                // Si el cache está expirado, limpiarlo
                if (cacheExpired)
                {
                    _statusCache.Clear();
                    _cacheLastRefresh = DateTime.UtcNow;
                }

                // Intentar obtener del cache
                if (_statusCache.TryGetValue(statusValue, out int cachedId))
                {
                    return cachedId;
                }
            }

            // Si no está en cache, consultar BD
            var systemStatus = await _context.SystemStatuses
                .FirstOrDefaultAsync(s => s.StatusValue == statusValue && s.StatusType == "SearchHireStatus", cancellationToken);
            
            int statusId;
            if (systemStatus == null)
            {
                // Default to "pending" (ID = 1)
                statusId = 1;
            }
            else
            {
                statusId = systemStatus.Id;
            }

            // Guardar en cache
            lock (_cacheLock)
            {
                _statusCache[statusValue] = statusId;
            }
            
            return statusId;
        }

        public async Task<AccountDeletionStatusDto> CheckDeletionStatusAsync(int userId, CancellationToken cancellationToken = default)
        {
            try
            {
                var user = await _context.Users
                    .Include(u => u.SearchHiresAsClient)
                    .Include(u => u.SearchHiresAsExpert)
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
                    additionalData: new { 
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
            
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await _context.Database.BeginTransactionAsync(linkedCts.Token);
                try
            {
                // 1. Verificar usuario y contraseña
                // ✅ MEJORA: IgnoreQueryFilters() para poder acceder a usuarios eliminados si es necesario
                var user = await _context.Users
                    .IgnoreQueryFilters() // Ignorar query filter para poder acceder a usuarios eliminados
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

                // No se requiere verificación de contraseña ya que el sistema solo usa autenticación con Google

                // 2. Obtener contrataciones activas
                var activeContracts = await GetActiveContractsAsync(userId, linkedCts.Token);
                var disputesCreated = new List<DisputeCreatedInfo>();
                // ✅ MEJORA: Acumular errores de procesamiento para determinar RequiresManualReview
                var processingErrors = new List<(int SearchHireId, string ErrorMessage, string ErrorType, decimal Amount)>();

                // 3. Procesar contrataciones activas
                if (activeContracts.Any())
                {
                    var result = await ProcessActiveContractsAsync(userId, activeContracts, request.Reason, linkedCts.Token);
                    disputesCreated = result.TransactionsProcessed;
                    processingErrors = result.ProcessingErrors;
                }

                // 4. Eliminar datos del usuario
                await DeleteUserDataAsync(userId, linkedCts.Token);

                // 5. Confirmar transacción PRIMERO (antes de notificaciones)
                // ✅ MEJORA: Las notificaciones no deberían bloquear la eliminación de la cuenta
                await transaction.CommitAsync(linkedCts.Token);

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
                    // ✅ Log pero no fallar - las notificaciones no son críticas para la eliminación
                    await _loggingService.LogWarningAsync(
                        message: "Failed to send notifications to affected users after account deletion",
                        details: $"Account deletion succeeded for user {userId}, but failed to notify affected users. Error: {notificationEx.Message}. Notifications can be sent manually if needed.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "Notification",
                        relatedEntityId: null,
                        additionalData: new { 
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
                    // ✅ Log pero no fallar - la notificación no es crítica
                    await _loggingService.LogWarningAsync(
                        message: "Failed to send account deletion notification",
                        details: $"Account deletion succeeded for user {userId}, but failed to send deletion notification. Error: {notificationEx.Message}.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "Notification",
                        relatedEntityId: null,
                        additionalData: new { 
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
                catch (DbUpdateConcurrencyException ex)
                {
                    // ✅ MEJORA: Manejo específico de conflictos de concurrencia (PostgreSQL MVCC)
                    await transaction.RollbackAsync(linkedCts.Token);
                    
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Account deletion failed - concurrency conflict",
                        details: $"Account deletion transaction for user {userId} was rolled back due to concurrency conflict. " +
                                $"Another process modified the user data concurrently. Error: {ex.Message}. " +
                                $"All changes have been rolled back. User account remains intact. ACTION REQUIRED: Retry account deletion.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace,
                            InnerException = ex.InnerException?.Message,
                            TransactionRolledBack = true,
                            ErrorCategory = "ConcurrencyConflict",
                            RetryRecommended = true
                        }
                    );
                    
                    throw;
                }
                catch (DbUpdateException dbEx) when (dbEx.InnerException is PostgresException pgEx)
                {
                    // ✅ MEJORA: Manejo específico de errores de PostgreSQL
                    await transaction.RollbackAsync(linkedCts.Token);
                    
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
                        message: $"CRITICAL: Account deletion failed - PostgreSQL error ({pgEx.SqlState})",
                        details: $"Account deletion transaction for user {userId} was rolled back due to PostgreSQL error. " +
                                $"SQL State: {pgEx.SqlState}, Constraint: {pgEx.ConstraintName}, " +
                                $"Message: {pgEx.Message}. All changes have been rolled back. User account remains intact. " +
                                $"ACTION REQUIRED: {actionRequired}",
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
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
                            RetryRecommended = errorCategory == "Deadlock" || errorCategory == "ConnectionError"
                        }
                    );
                    
                    throw;
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == linkedCts.Token || ex.CancellationToken == timeoutCts.Token)
                {
                    // ✅ MEJORA: Manejo específico de timeout o cancelación
                    await transaction.RollbackAsync(linkedCts.Token);
                    
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Account deletion failed - transaction timeout",
                        details: $"Account deletion transaction for user {userId} was cancelled due to timeout (5 minutes) or cancellation request. " +
                                $"All changes have been rolled back. User account remains intact. " +
                                $"ACTION REQUIRED: Review if operation should be retried or if there are performance issues.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace,
                            TransactionRolledBack = true,
                            ErrorCategory = "Timeout",
                            RetryRecommended = true,
                            TimeoutDuration = _transactionTimeout.TotalMinutes
                        }
                    );
                    
                    throw;
                }
                catch (Exception ex)
                {
                    // ✅ MEJOR PRÁCTICA: Logging completo del error antes de rethrow (catch-all para otros errores)
                    await transaction.RollbackAsync(linkedCts.Token);
                    
                    await _loggingService.LogCriticalAsync(
                        message: "CRITICAL: Account deletion transaction rolled back",
                        details: $"Account deletion transaction for user {userId} was rolled back due to error. Error Type: {ex.GetType().Name}, Error Message: {ex.Message}, Stack Trace: {ex.StackTrace}. " +
                                $"All changes have been rolled back. User account remains intact.",
                        userId: userId,
                        source: "AccountDeletionService.DeleteAccountAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId,
                        additionalData: new { 
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace,
                            InnerException = ex.InnerException?.Message,
                            TransactionRolledBack = true,
                            ErrorCategory = "Unknown"
                        }
                    );
                    
                    throw; // Re-throw para que el controller maneje el error
                }
            });
        }

        private async Task<List<ActiveContractInfo>> GetActiveContractsAsync(int userId, CancellationToken cancellationToken = default)
        {
            var activeContracts = new List<ActiveContractInfo>();

            // Buscar como cliente
            var clientContracts = await _context.SearchHires
                .Where(sh => sh.ClientId == userId && _activeStatuses.Contains(sh.Status.StatusValue))
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
                    CreatedAt = contract.CreatedAt,
                    OtherPartyName = contract.Expert?.Name ?? "Experto",
                    OtherPartyEmail = contract.Expert?.Email ?? "",
                    HasAppointment = contract.Appointment != null,
                    AppointmentDate = contract.Appointment?.ProposedDate
                });
            }

            // Buscar como experto
            var expertContracts = await _context.SearchHires
                .Where(sh => sh.ExpertId == userId && _activeStatuses.Contains(sh.Status.StatusValue))
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

            foreach (var contract in activeContracts)
            {
                var searchHire = await _context.SearchHires
                    .Include(sh => sh.Status)
                    .Include(sh => sh.Client)
                    .Include(sh => sh.Expert)
                    .FirstOrDefaultAsync(sh => sh.Id == contract.SearchHireId, cancellationToken);

                if (searchHire == null) continue;

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
                     if (searchHire.Status.IsFinalizationStatus)
                     {
                         continue; // Saltar al siguiente SearchHire - NO tocar nada
                     }

                     // Verificar si hay subestado de finalización en appointment
                     var existingAppointment = await _context.Appointments
                         .Include(a => a.Status)
                         .FirstOrDefaultAsync(a => a.SearchHireId == searchHire.Id, cancellationToken);
                     
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
                            "cancelled_by_client_account_delete",
                            "Client account deletion - transfer to expert",
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
                                additionalData: new { 
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    ExpertId = searchHire.ExpertId,
                                    DeletionReason = deletionReason
                                }
                            );
                            
                            // ✅ Acumular error para log crítico final
                            processingErrors.Add((searchHire.Id, errorMessage, errorType, searchHire.Amount));
                            
                            // Continuar con siguiente contratación (no lanzar excepción)
                            continue;
                        }
                        
                        // ✅ Notificar al experto que recibió el pago
                        if (searchHire.ExpertId.HasValue)
                        {
                            await _loggingService.LogInfoAsync(
                                message: "Pago procesado por eliminación de cuenta del cliente",
                                details: $"El cliente del servicio #{searchHire.Id} eliminó su cuenta. Se procesó automáticamente el pago de {searchHire.Amount:F2}€ a tu favor. El dinero está disponible en tu cuenta de Stripe.",
                                userId: searchHire.ExpertId.Value,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true,
                                additionalData: new { 
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
                            details: $"Al eliminar tu cuenta, el servicio #{searchHire.Id} fue cancelado y el pago de {searchHire.Amount:F2}€ fue transferido automáticamente al experto.",
                            userId: userId,
                            source: "AccountDeletionService.ProcessActiveContractsAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id,
                            notifyUser: true,
                            additionalData: new { 
                                SearchHireId = searchHire.Id,
                                Amount = searchHire.Amount,
                                Action = "AccountDeletion_ClientDeleted",
                                DeletionReason = deletionReason
                            }
                        );
                     }
                     else
                     {
                         // Si el experto elimina su cuenta, reembolsar al cliente
                        var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                            searchHire.Id,
                            "cancelled_by_expert_account_delete",
                            reasonText,
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
                                additionalData: new { 
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    ClientId = searchHire.ClientId,
                                    Reason = "Account deletion",
                                    DeletionReason = deletionReason
                                }
                            );
                            
                            // ✅ Acumular error para log crítico final
                            processingErrors.Add((searchHire.Id, errorMessage, errorType, searchHire.Amount));
                            
                            // Continuar con siguiente contratación (no lanzar excepción)
                            continue;
                        }
                        
                        // ✅ Notificar al cliente que recibió el reembolso
                        await _loggingService.LogInfoAsync(
                            message: "Reembolso procesado por eliminación de cuenta del experto",
                            details: $"El experto del servicio #{searchHire.Id} eliminó su cuenta. Se procesó automáticamente tu reembolso de {searchHire.Amount:F2}€. El dinero llegará a tu cuenta en 5-10 días hábiles.",
                            userId: searchHire.ClientId,
                            source: "AccountDeletionService.ProcessActiveContractsAsync",
                            relatedEntityType: "SearchHire",
                            relatedEntityId: searchHire.Id,
                            notifyUser: true,
                            additionalData: new { 
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
                                details: $"Al eliminar tu cuenta, el servicio #{searchHire.Id} fue cancelado y se procesó automáticamente el reembolso de {searchHire.Amount:F2}€ al cliente.",
                                userId: userId,
                                source: "AccountDeletionService.ProcessActiveContractsAsync",
                                relatedEntityType: "SearchHire",
                                relatedEntityId: searchHire.Id,
                                notifyUser: true,
                                additionalData: new { 
                                    SearchHireId = searchHire.Id,
                                    Amount = searchHire.Amount,
                                    Action = "AccountDeletion_ExpertDeleted",
                                    DeletionReason = deletionReason
                                }
                            );
                        }
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
                        additionalData: new { 
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
                            $"Total failed amount: {totalFailedAmount:F2}€. " +
                            $"Error summary: {errorSummary}. " +
                            $"Error types: {string.Join(", ", errorTypes)}. " +
                            $"ACTION REQUIRED: Manual review and processing required for failed contracts. " +
                            $"SearchHire IDs: {string.Join(", ", processingErrors.Select(e => e.SearchHireId))}.",
                    userId: userId,
                    source: "AccountDeletionService.ProcessActiveContractsAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
                        DeletedUserId = userId,
                        FailedContractsCount = processingErrors.Count,
                        TotalFailedAmount = totalFailedAmount,
                        FailedSearchHireIds = processingErrors.Select(e => e.SearchHireId).ToList(),
                        ErrorDetails = processingErrors.Select(e => new { 
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

            await _context.SaveChangesAsync(cancellationToken);
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
                var pendingTransactions = await _context.FinancialTransactions
                    .Where(ft => ft.UserId == userId && 
                                (ft.TransactionType == "ServicePayment" || ft.TransactionType == "Deposit"))
                    .AnyAsync(cancellationToken);
                
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
                    // 1. ✅ ANONIMIZAR mensajes (NO ELIMINAR - preservar para la otra parte)
                    // PostgreSQL + C# Best Practice: Anonimizar en lugar de eliminar para preservar contexto
                    // SenderId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si SenderId no es NULL (no anonimizado ya)
                    var messagesCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Messages"" 
                          SET ""SenderId"" = NULL, 
                              ""Content"" = '[Usuario eliminado] ' || COALESCE(""Content"", '')
                          WHERE ""SenderId"" = {0} AND ""SenderId"" IS NOT NULL", userId, cancellationToken);
                    
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
                             OR (""ExpertId"" = {0} AND ""ExpertId"" IS NOT NULL)", userId, cancellationToken);
                    
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

                    // 3. ✅ ANONIMIZAR reseñas dadas (NO ELIMINAR - preservar para mantener calificaciones)
                    // PostgreSQL + C# Best Practice: Anonimizar pero preservar rating para mantener promedios
                    // ReviewerId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si ReviewerId no es NULL (no anonimizado ya)
                    // ✅ MEJORA: Agregar UpdatedAt para trazabilidad (aunque Review no tiene UpdatedAt, se preserva CreatedAt)
                    var reviewsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Reviews"" 
                          SET ""ReviewerId"" = NULL, 
                              ""Description"" = CASE WHEN ""Description"" IS NOT NULL AND ""Description"" != '' 
                                  THEN '[Usuario eliminado] ' || ""Description"" 
                                  ELSE ""Description"" END
                          WHERE ""ReviewerId"" = {0} AND ""ReviewerId"" IS NOT NULL", userId, cancellationToken);
                    
                    if (reviewsCount > 0)
                    {
                        await _loggingService.LogInfoAsync(
                            message: "Reviews anonymized for account deletion",
                            details: $"Anonymized {reviewsCount} reviews for user {userId}. Ratings and averages preserved.",
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
                    var transactionsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""FinancialTransactions"" 
                          SET ""UserId"" = NULL,
                              ""UpdatedAt"" = CURRENT_TIMESTAMP
                          WHERE ""UserId"" = {0} AND ""UserId"" IS NOT NULL", userId, cancellationToken);
                    
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
                            additionalData: new { 
                                DeletedUserId = userId,
                                TransactionsAnonymized = transactionsCount,
                                Action = "AnonymizeFinancialTransactions",
                                LegalCompliance = "6 years retention (Spain accounting law)",
                                StripeReconciliation = "Preserved StripeRefundId, StripeTransferId, StripePaymentIntentId, Amount, TransactionType, CreatedAt"
                            }
                        );
                    }

                    // 5. ✅ ANONIMIZAR notificaciones (NO ELIMINAR - preservar para auditoría)
                    // PostgreSQL + C# Best Practice: Anonimizar pero preservar para trazabilidad
                    // Notification.UserId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si UserId no es NULL (no anonimizado ya)
                    var notificationsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Notifications"" 
                          SET ""UserId"" = NULL, 
                              ""Message"" = '[Usuario eliminado] ' || COALESCE(""Message"", '')
                          WHERE ""UserId"" = {0} AND ""UserId"" IS NOT NULL", userId, cancellationToken);
                    
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

                    // 6. ✅ ANONIMIZAR SearchHires (NO ELIMINAR - preservar contrataciones históricas)
                    // PostgreSQL + C# Best Practice: Anonimizar referencias pero mantener historial de contrataciones
                    // ClientId y ExpertId son ahora nullable, permitiendo anonimización completa
                    // ✅ MEJORA: Anonimización completa de SearchHires (ClientId y ExpertId)
                    var searchHiresAsClient = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""SearchHires"" 
                          SET ""ClientId"" = NULL,
                              ""UpdatedAt"" = CURRENT_TIMESTAMP 
                          WHERE ""ClientId"" = {0} AND ""ClientId"" IS NOT NULL", userId, cancellationToken);
                    
                    var searchHiresAsExpert = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""SearchHires"" 
                          SET ""ExpertId"" = NULL,
                              ""UpdatedAt"" = CURRENT_TIMESTAMP 
                          WHERE ""ExpertId"" = {0} AND ""ExpertId"" IS NOT NULL", userId, cancellationToken);
                    
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
                        additionalData: new { 
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
                        additionalData: new { 
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
                var likes = await _context.Likes
                    .Where(l => l.UserId == userId)
                    .ToListAsync(cancellationToken);
                if (likes.Any())
                {
                    _context.Likes.RemoveRange(likes);
                    hasDeletes = true;
                }

                // 8. Eliminar búsquedas (datos no críticos)
                var searches = await _context.Searches
                    .Where(s => s.UserId == userId)
                    .ToListAsync(cancellationToken);
                if (searches.Any())
                {
                    _context.Searches.RemoveRange(searches);
                    hasDeletes = true;
                }

                // 9. Eliminar/anonimizar servicios (si es experto - datos no críticos)
                // ✅ MEJORA: Preservar servicios con contrataciones históricas (anonimizar en lugar de eliminar)
                var expertProfile = await _context.ExpertProfiles
                    .Include(ep => ep.SearchServices)
                        .ThenInclude(ss => ss.Images)
                    .FirstOrDefaultAsync(ep => ep.UserId == userId, cancellationToken);

                if (expertProfile != null)
                {
                    var servicesToAnonymize = new List<int>();
                    var servicesToDelete = new List<int>();

                    // ✅ MEJORA: Optimización - Batch check para evitar N+1 queries
                    // En lugar de verificar cada servicio individualmente, hacemos una sola query
                    var allServiceIds = expertProfile.SearchServices.Select(ss => ss.Id).ToList();
                    
                    if (allServiceIds.Any())
                    {
                        // ✅ Una sola query para obtener todos los SearchHires asociados a los servicios
                        var servicesWithHires = await _context.SearchHires
                            .Where(sh => allServiceIds.Contains(sh.SearchServiceId))
                            .Select(sh => sh.SearchServiceId)
                            .Distinct()
                            .ToListAsync(cancellationToken);

                        var servicesWithHiresSet = new HashSet<int>(servicesWithHires);

                        // ✅ Clasificar servicios: anonimizar si tienen hires, eliminar si no
                        foreach (var service in expertProfile.SearchServices)
                        {
                            if (servicesWithHiresSet.Contains(service.Id))
                            {
                                // ✅ Preservar servicio para contrataciones históricas (auditoría, facturación, disputas)
                                servicesToAnonymize.Add(service.Id);
                            }
                            else
                            {
                                // ✅ Eliminar servicio si no tiene contrataciones asociadas
                                servicesToDelete.Add(service.Id);
                            }
                        }
                    }
                    else
                    {
                        // Si no hay servicios, todos van a delete (aunque no habrá nada que eliminar)
                        servicesToDelete.AddRange(allServiceIds);
                    }

                    // ✅ Anonimizar servicios con contrataciones históricas
                    if (servicesToAnonymize.Any())
                    {
                        // ✅ Usar EF Core para anonimizar de forma segura (evita SQL injection)
                        var servicesToAnonymizeEntities = expertProfile.SearchServices
                            .Where(ss => servicesToAnonymize.Contains(ss.Id))
                            .ToList();

                        foreach (var service in servicesToAnonymizeEntities)
                        {
                            service.ExpertProfileId = null;
                            // ✅ IMPORTANTE: Desactivar servicio al anonimizar para que no aparezca en búsquedas
                            // IsActive y ExpertProfileId son campos diferentes:
                            // - IsActive: desactiva temporalmente (vacaciones, mantenimiento)
                            // - ExpertProfileId = NULL: anonimiza (eliminación de cuenta)
                            service.IsActive = false;
                        }

                        var anonymizedCount = servicesToAnonymizeEntities.Count;

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
                                additionalData: new { 
                                    DeletedUserId = userId,
                                    AnonymizedServiceIds = servicesToAnonymize,
                                    Reason = "Preserve services with historical contracts for audit trail and legal compliance"
                                }
                            );
                        }
                    }

                    // ✅ Eliminar imágenes de servicios que se van a eliminar (no anonimizar)
                    if (servicesToDelete.Any())
                    {
                        var servicesToDeleteImages = expertProfile.SearchServices
                            .Where(ss => servicesToDelete.Contains(ss.Id))
                            .SelectMany(ss => ss.Images)
                            .ToList();

                        if (servicesToDeleteImages.Any())
                        {
                            _context.SearchServiceImages.RemoveRange(servicesToDeleteImages);
                            hasDeletes = true;
                        }

                        // ✅ Eliminar servicios sin contrataciones asociadas
                        var servicesToRemove = expertProfile.SearchServices
                            .Where(ss => servicesToDelete.Contains(ss.Id))
                            .ToList();

                        if (servicesToRemove.Any())
                        {
                            _context.SearchServices.RemoveRange(servicesToRemove);
                            hasDeletes = true;

                            await _loggingService.LogInfoAsync(
                                message: "SearchServices deleted for account deletion (no associated contracts)",
                                details: $"Deleted {servicesToRemove.Count} SearchService(s) for expert {userId}. " +
                                        $"Services deleted because they have no associated SearchHires (contracts). " +
                                        $"Service IDs: {string.Join(", ", servicesToDelete)}.",
                                userId: null,
                                source: "AccountDeletionService.DeleteUserDataAsync",
                                relatedEntityType: "SearchService",
                                relatedEntityId: null,
                                additionalData: new { 
                                    DeletedUserId = userId,
                                    DeletedServiceIds = servicesToDelete
                                }
                            );
                        }
                    }

                    // ✅ Eliminar perfil de experto (no depende de servicios, FK es nullable)
                    _context.ExpertProfiles.Remove(expertProfile);
                    hasDeletes = true;
                }

                // 10. Eliminar configuraciones de usuario (datos no críticos)
                var userSettings = await _context.UserSettings
                    .Where(us => us.UserId == userId)
                    .ToListAsync(cancellationToken);
                if (userSettings.Any())
                {
                    _context.UserSettings.RemoveRange(userSettings);
                    hasDeletes = true;
                }

                // 11. Eliminar suscripciones (datos no críticos)
                var subscriptions = await _context.UserSubscriptions
                    .Where(us => us.UserId == userId)
                    .ToListAsync(cancellationToken);
                if (subscriptions.Any())
                {
                    _context.UserSubscriptions.RemoveRange(subscriptions);
                    hasDeletes = true;
                }
                
                // ✅ BATCH SAVE: Un solo SaveChangesAsync para todos los deletes (mejor performance)
                if (hasDeletes)
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }

                // ===== FASE 4: SOFT DELETE DEL USUARIO (misma transacción global) =====
                // ✅ MEJORA: Soft delete en lugar de hard delete para permitir recuperación y cumplimiento legal
                // El query filter en AppDbContext excluirá automáticamente usuarios con IsDeleted = true
                
                // ✅ IDEMPOTENCIA: Verificar que el usuario aún existe y no está eliminado
                // NOTA: Usar IgnoreQueryFilters() para acceder a usuarios eliminados si es necesario
                var userToDelete = await _context.Users
                    .IgnoreQueryFilters() // Ignorar query filter para poder acceder a usuarios eliminados
                    .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
                
                if (userToDelete != null && !userToDelete.IsDeleted)
                {
                    // ✅ SOFT DELETE: Marcar como eliminado en lugar de remover físicamente
                    userToDelete.IsDeleted = true;
                    userToDelete.DeletedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                    
                    await _loggingService.LogInfoAsync(
                        message: "User soft deleted successfully",
                        details: $"User {userId} has been soft deleted (IsDeleted=true, DeletedAt={userToDelete.DeletedAt:O}). User will be excluded from queries automatically by query filter.",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                }
                else if (userToDelete != null && userToDelete.IsDeleted)
                {
                    // Usuario ya fue eliminado (idempotencia)
                    await _loggingService.LogWarningAsync(
                        message: "User already soft deleted - idempotent call",
                        details: $"User {userId} was already soft deleted at {userToDelete.DeletedAt:O}. Account deletion process completed (idempotent).",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                }
                else
                {
                    // Usuario no existe
                    await _loggingService.LogWarningAsync(
                        message: "User not found for deletion",
                        details: $"User {userId} not found. Account deletion process completed (idempotent).",
                        userId: null,
                        source: "AccountDeletionService.DeleteUserDataAsync",
                        relatedEntityType: "User",
                        relatedEntityId: userId
                    );
                }
                
                // ✅ LOG FINAL: Eliminación de datos completada
                await _loggingService.LogInfoAsync(
                    message: "User data deletion completed successfully",
                    details: $"All user data for user {userId} has been anonymized or deleted. Account deletion process completed.",
                    userId: null,
                    source: "AccountDeletionService.DeleteUserDataAsync",
                    relatedEntityType: "User",
                    relatedEntityId: userId,
                    additionalData: new { 
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
                    additionalData: new { 
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

    }
}

using Microsoft.EntityFrameworkCore;
using Npgsql;
using newApi.DataLayer;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;
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
            using var transaction = await _context.Database.BeginTransactionAsync(linkedCts.Token);
            try
            {
                // 4. Eliminar datos del usuario (dentro de transacción)
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
                        additionalData: new { 
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
                            TransfersFailed = processingErrors.Count
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
                            RetryRecommended = errorCategory == "Deadlock" || errorCategory == "ConnectionError",
                            MoneyAlreadyProcessed = moneyAlreadyProcessed,
                            TransfersCompleted = disputesCreated.Count,
                            TransfersFailed = processingErrors.Count
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
                        additionalData: new { 
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
                            TransfersFailed = processingErrors.Count
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
                        additionalData: new { 
                            DeletedUserId = userId,
                            ErrorType = ex.GetType().Name,
                            ErrorMessage = ex.Message,
                            StackTrace = ex.StackTrace,
                            InnerException = ex.InnerException?.Message,
                            TransactionRolledBack = true,
                            ErrorCategory = "Unknown",
                            MoneyAlreadyProcessed = moneyAlreadyProcessed,
                            TransfersCompleted = disputesCreated.Count,
                            TransfersFailed = processingErrors.Count
                        }
                    );
                    
                    throw; // Re-throw para que el controller maneje el error
                }
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
                            "cancelled_by_client_account_delete",
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
                        
                        // ✅ CANCELAR timers activos y jobs de Hangfire
                        await CancelActiveTimersAndHangfireJobsAsync(searchHire.Id, cancellationToken);
                     }
                     else
                     {
                         // Si el experto elimina su cuenta, reembolsar al cliente
                        var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
                            searchHire.Id,
                            "cancelled_by_expert_account_delete",
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

                    // 3. ✅ ANONIMIZAR reseñas dadas (NO ELIMINAR - preservar para mantener calificaciones)
                    // PostgreSQL + C# Best Practice: Anonimizar pero preservar rating para mantener promedios
                    // ReviewerId es nullable, usar NULL directamente
                    // ✅ IDEMPOTENCIA: Solo actualizar si ReviewerId no es NULL (no anonimizado ya)
                    // ✅ MEJORA: Agregar UpdatedAt para trazabilidad (aunque Review no tiene UpdatedAt, se preserva CreatedAt)
                    // ✅ CRÍTICO: Anonimizar Reviews ANTES de anonimizar/eliminar SearchHires para evitar violaciones de FK
                    var reviewsCount = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Reviews"" 
                          SET ""ReviewerId"" = NULL, 
                              ""Description"" = CASE WHEN ""Description"" IS NOT NULL AND ""Description"" != '' 
                                  THEN '[Usuario eliminado] ' || ""Description"" 
                                  ELSE ""Description"" END
                          WHERE ""ReviewerId"" = {0} AND ""ReviewerId"" IS NOT NULL", 
                        new object[] { userId }, cancellationToken);
                    
                    // ✅ CRÍTICO: También anonimizar Reviews que referencian SearchHires del usuario
                    // Esto previene violaciones de FK cuando se anonimizan/eliminan SearchHires
                    var reviewsForUserSearchHires = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Reviews"" 
                          SET ""Description"" = CASE WHEN ""Description"" IS NOT NULL AND ""Description"" != '' 
                                  THEN '[Usuario eliminado] ' || ""Description"" 
                                  ELSE ""Description"" END
                          WHERE ""SearchHireId"" IN (
                              SELECT ""Id"" FROM ""SearchHires"" 
                              WHERE ""ClientId"" = {0} OR ""ExpertId"" = {0}
                          ) AND ""Description"" NOT LIKE '[Usuario eliminado]%'", 
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
                          WHERE ""UserId"" = {0} AND ""UserId"" IS NOT NULL", 
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
                            additionalData: new { 
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
                // ✅ FIX: Usar SQL directo para evitar ExecutionStrategy dentro de transacción manual
                var likesDeleted = await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""Likes"" WHERE ""UserId"" = {0}",
                    new object[] { userId }, cancellationToken);
                if (likesDeleted > 0)
                {
                    hasDeletes = true;
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
                // ✅ FIX: Usar FromSqlRaw para evitar ExecutionStrategy dentro de transacción manual
                var expertProfile = await _context.ExpertProfiles
                    .FromSqlRaw(@"SELECT * FROM ""ExpertProfiles"" WHERE ""UserId"" = {0}", userId)
                    .FirstOrDefaultAsync(cancellationToken);
                
                if (expertProfile != null)
                {
                    // Cargar servicios con imágenes usando FromSqlRaw
                    expertProfile.SearchServices = await _context.SearchServices
                        .FromSqlRaw(@"SELECT * FROM ""SearchServices"" WHERE ""ExpertProfileId"" = {0}", expertProfile.Id)
                        .Include(ss => ss.Images)
                        .ToListAsync(cancellationToken);
                }

                // ✅ CORRECCIÓN: Solo procesar servicios si el usuario que elimina ES el experto
                // Si un cliente elimina su cuenta, el experto y sus servicios NO deben ser afectados
                if (expertProfile != null && expertProfile.UserId == userId)
                {
                    var servicesToAnonymize = new List<int>();
                    var servicesToDelete = new List<int>();

                    // ✅ MEJORA: Optimización - Batch check para evitar N+1 queries
                    // En lugar de verificar cada servicio individualmente, hacemos una sola query
                    var allServiceIds = expertProfile.SearchServices.Select(ss => ss.Id).ToList();
                    
                    if (allServiceIds.Any())
                    {
                        // ✅ CRÍTICO: Verificar TODOS los SearchHires asociados, incluso si están anonimizados
                        // Esto previene eliminar servicios que tienen contrataciones históricas (aunque anonimizadas)
                        // ✅ MEJORA: Buscar por SearchServiceId directamente, no por ClientId/ExpertId
                        // ✅ FIX: Usar SqlQueryRaw para evitar ExecutionStrategy dentro de transacción manual
                        var serviceIdsParam = string.Join(",", allServiceIds);
                        var servicesWithHires = await _context.Database
                            .SqlQueryRaw<int>(@"SELECT DISTINCT ""SearchServiceId"" FROM ""SearchHires"" WHERE ""SearchServiceId"" = ANY(ARRAY[{0}])", serviceIdsParam)
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
                            // ✅ IMPORTANTE: Desactivar servicio al anonimizar SOLO cuando el EXPERTO elimina su cuenta
                            // Si un CLIENTE elimina su cuenta, los servicios del experto NO deben desactivarse
                            // IsActive y ExpertProfileId son campos diferentes:
                            // - IsActive: desactiva temporalmente (vacaciones, mantenimiento)
                            // - ExpertProfileId = NULL: anonimiza (eliminación de cuenta del experto)
                            // ✅ CORRECCIÓN: Solo desactivar si el usuario que elimina ES el experto
                            service.IsActive = false; // Solo se ejecuta si expertProfile.UserId == userId (experto eliminando su cuenta)
                        }

                        // ✅ Guardar cambios con manejo de error si la migración no está aplicada
                        try
                        {
                            await _context.SaveChangesAsync(cancellationToken);
                            
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
                        catch (DbUpdateException dbEx) when (dbEx.InnerException is PostgresException pgEx && pgEx.SqlState == "23502")
                        {
                            // ✅ ERROR: ExpertProfileId tiene restricción NOT NULL en BD (falta migración)
                            await _loggingService.LogWarningAsync(
                                message: "ExpertProfileId cannot be anonymized - NOT NULL constraint",
                                details: $"Cannot anonymize ExpertProfileId for SearchServices because the column has a NOT NULL constraint in the database. " +
                                        $"The model has ExpertProfileId as nullable, but the database migration has not been applied yet. " +
                                        $"Service IDs affected: {string.Join(", ", servicesToAnonymize)}. " +
                                        $"Services will be deactivated instead. " +
                                        $"ACTION REQUIRED: Apply migration 'MakeExpertProfileIdNullableInSearchServices'.",
                                userId: userId,
                                source: "AccountDeletionService.DeleteUserDataAsync",
                                relatedEntityType: "SearchService",
                                relatedEntityId: null,
                                additionalData: new { 
                                    DeletedUserId = userId,
                                    SqlState = pgEx.SqlState,
                                    Error = "NOT NULL constraint violation on ExpertProfileId",
                                    AffectedServiceIds = servicesToAnonymize,
                                    ActionRequired = "Apply migration MakeExpertProfileIdNullableInSearchServices"
                                }
                            );
                            
                            // ✅ FALLBACK: Solo desactivar servicios sin anonimizar
                            foreach (var service in servicesToAnonymizeEntities)
                            {
                                // Revertir el cambio de ExpertProfileId para evitar el error
                                _context.Entry(service).Property(s => s.ExpertProfileId).IsModified = false;
                                // Mantener IsActive = false para desactivar el servicio
                                service.IsActive = false;
                            }
                            
                            // Guardar solo la desactivación
                            await _context.SaveChangesAsync(cancellationToken);
                            
                            await _loggingService.LogWarningAsync(
                                message: "SearchServices deactivated instead of anonymized",
                                details: $"{servicesToAnonymizeEntities.Count} SearchService(s) were deactivated instead of anonymized because ExpertProfileId has a NOT NULL constraint. " +
                                        $"Service IDs: {string.Join(", ", servicesToAnonymize)}. " +
                                        $"ACTION REQUIRED: Apply migration 'MakeExpertProfileIdNullableInSearchServices' to enable full anonymization.",
                                userId: userId,
                                source: "AccountDeletionService.DeleteUserDataAsync",
                                relatedEntityType: "SearchService",
                                relatedEntityId: null,
                                additionalData: new { 
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
                        // ✅ FIX: Usar SqlQueryRaw para evitar ExecutionStrategy dentro de transacción manual
                        var servicesToDeleteParam = string.Join(",", servicesToDelete);
                        var finalCheckServicesWithHires = await _context.Database
                            .SqlQueryRaw<int>(@"SELECT DISTINCT ""SearchServiceId"" FROM ""SearchHires"" WHERE ""SearchServiceId"" = ANY(ARRAY[{0}])", servicesToDeleteParam)
                            .ToListAsync(cancellationToken);
                        
                        // ✅ Filtrar servicios que realmente no tienen SearchHires
                        var servicesToDeleteFinal = servicesToDelete
                            .Where(sid => !finalCheckServicesWithHires.Contains(sid))
                            .ToList();
                        
                        if (servicesToDeleteFinal.Any())
                        {
                            var servicesToDeleteImages = expertProfile.SearchServices
                                .Where(ss => servicesToDeleteFinal.Contains(ss.Id))
                                .SelectMany(ss => ss.Images)
                                .ToList();

                            if (servicesToDeleteImages.Any())
                            {
                                _context.SearchServiceImages.RemoveRange(servicesToDeleteImages);
                                hasDeletes = true;
                            }

                            // ✅ Eliminar servicios sin contrataciones asociadas
                            var servicesToRemove = expertProfile.SearchServices
                                .Where(ss => servicesToDeleteFinal.Contains(ss.Id))
                                .ToList();

                            if (servicesToRemove.Any())
                            {
                                _context.SearchServices.RemoveRange(servicesToRemove);
                                hasDeletes = true;

                                await _loggingService.LogInfoAsync(
                                    message: "SearchServices deleted for account deletion (no associated contracts)",
                                    details: $"Deleted {servicesToRemove.Count} SearchService(s) for expert {userId}. " +
                                            $"Services deleted because they have no associated SearchHires (contracts). " +
                                            $"Service IDs: {string.Join(", ", servicesToDeleteFinal)}.",
                                    userId: null,
                                    source: "AccountDeletionService.DeleteUserDataAsync",
                                    relatedEntityType: "SearchService",
                                    relatedEntityId: null,
                                    additionalData: new { 
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
                                additionalData: new { 
                                    DeletedUserId = userId,
                                    PreservedServiceIds = servicesToDelete,
                                    Reason = "Found associated SearchHires during final check"
                                }
                            );
                            
                            // ✅ Anonimizar estos servicios en lugar de eliminarlos
                            var servicesToAnonymizeFinal = expertProfile.SearchServices
                                .Where(ss => servicesToDelete.Contains(ss.Id))
                                .ToList();
                            
                            foreach (var service in servicesToAnonymizeFinal)
                            {
                                service.ExpertProfileId = null;
                                service.IsActive = false;
                            }
                            
                            try
                            {
                                await _context.SaveChangesAsync(cancellationToken);
                            }
                            catch (DbUpdateException dbEx) when (dbEx.InnerException is PostgresException pgEx && pgEx.SqlState == "23502")
                            {
                                // Si falla, solo desactivar
                                foreach (var service in servicesToAnonymizeFinal)
                                {
                                    _context.Entry(service).Property(s => s.ExpertProfileId).IsModified = false;
                                    service.IsActive = false;
                                }
                                await _context.SaveChangesAsync(cancellationToken);
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
                        new object[] { expertProfile.Id }, cancellationToken);
                    
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

                    // ✅ Eliminar perfil de experto (no depende de servicios, FK es nullable)
                    _context.ExpertProfiles.Remove(expertProfile);
                    hasDeletes = true;
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
                        // Si el job ya no existe o fue procesado, continuar sin error
                        // Loguear pero no fallar - la cancelación de Hangfire no es crítica
                        await _loggingService.LogWarningAsync(
                            message: "Failed to cancel Hangfire job during account deletion",
                            details: $"Failed to cancel Hangfire job {jobId} for SearchHire {searchHireId} during account deletion. Job may have already been processed. Error: {ex.Message}",
                            userId: null,
                            source: "AccountDeletionService.CancelActiveTimersAndHangfireJobsAsync",
                            relatedEntityType: "AppointmentTimer",
                            relatedEntityId: appointment.Id,
                            additionalData: new { 
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
                    additionalData: new { 
                        SearchHireId = searchHireId,
                        Error = ex.Message,
                        ErrorType = ex.GetType().Name
                    }
                );
            }
        }

    }
}

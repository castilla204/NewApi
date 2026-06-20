using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.enums;
using newApi.Common;

namespace newApi.Services
{
    /// <summary>
    /// Servicio para gestionar la nueva arquitectura centralizada de estados
    /// </summary>
    public class SystemStatusService
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;
        
        public SystemStatusService(AppDbContext context, ILoggingService loggingService)
        {
            _context = context;
            _loggingService = loggingService;
        }

        /// <summary>
        /// Obtiene la configuración de distribución de dinero para un estado específico
        /// </summary>
        public async Task<StatusConfiguration?> GetMoneyDistributionAsync(
            string statusValue, 
            int? categoryId, 
            int? serviceTypeCategoryId)
        {
            try
            {
                await _loggingService.LogInfoAsync(
                    message: "GetMoneyDistributionAsync - Iniciando búsqueda",
                    details: $"Buscando configuración para statusValue: {statusValue}, categoryId: {categoryId}, serviceTypeCategoryId: {serviceTypeCategoryId}",
                    userId: null,
                    source: "SystemStatusService.GetMoneyDistributionAsync",
                    relatedEntityType: "StatusConfiguration",
                    relatedEntityId: null
                );
                
                // Obtener el StatusId primero para evitar problemas con Include en Where
                await _loggingService.LogInfoAsync(
                    message: "GetMoneyDistributionAsync - Buscando SystemStatus",
                    details: $"Ejecutando consulta: SystemStatuses WHERE StatusValue = {statusValue} AND IsActive = true",
                    userId: null,
                    source: "SystemStatusService.GetMoneyDistributionAsync",
                    relatedEntityType: "SystemStatus",
                    relatedEntityId: null
                );
                
                var status = await _context.SystemStatuses
                    .FirstOrDefaultAsync(ss => ss.StatusValue == statusValue && ss.IsActive);
                
                if (status == null)
                {
                    await _loggingService.LogWarningAsync(
                        message: "GetMoneyDistributionAsync - SystemStatus no encontrado",
                        details: $"No se encontró SystemStatus con StatusValue: {statusValue}",
                        userId: null,
                        source: "SystemStatusService.GetMoneyDistributionAsync",
                        relatedEntityType: "SystemStatus",
                        relatedEntityId: null
                    );
                    return null;
                }
                
                await _loggingService.LogInfoAsync(
                    message: "GetMoneyDistributionAsync - SystemStatus encontrado",
                    details: $"SystemStatus encontrado: Id = {status.Id}, StatusValue = {status.StatusValue}",
                    userId: null,
                    source: "SystemStatusService.GetMoneyDistributionAsync",
                    relatedEntityType: "SystemStatus",
                    relatedEntityId: status.Id
                );
                
                // 1. Buscar configuración específica (categoría + tipo)
                await _loggingService.LogInfoAsync(
                    message: "GetMoneyDistributionAsync - Buscando configuración específica",
                    details: $"Ejecutando consulta: StatusConfigurations WHERE StatusId = {status.Id}, CategoryId = {categoryId}, ServiceTypeCategoryId = {serviceTypeCategoryId}",
                    userId: null,
                    source: "SystemStatusService.GetMoneyDistributionAsync",
                    relatedEntityType: "StatusConfiguration",
                    relatedEntityId: null
                );
                
                var config = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Where(sc => sc.StatusId == status.Id &&
                                sc.CategoryId == categoryId &&
                                sc.ServiceTypeCategoryId == serviceTypeCategoryId &&
                                sc.IsActive)
                    .FirstOrDefaultAsync();

                if (config != null)
                {
                    await _loggingService.LogInfoAsync(
                        message: "GetMoneyDistributionAsync - Configuración específica encontrada",
                        details: $"Configuración encontrada: Id = {config.Id}",
                        userId: null,
                        source: "SystemStatusService.GetMoneyDistributionAsync",
                        relatedEntityType: "StatusConfiguration",
                        relatedEntityId: config.Id
                    );
                    return config;
                }
                
                // 2. Buscar configuración por categoría (tipo = NULL)
                await _loggingService.LogInfoAsync(
                    message: "GetMoneyDistributionAsync - Buscando configuración por categoría",
                    details: $"Ejecutando consulta: StatusConfigurations WHERE StatusId = {status.Id}, CategoryId = {categoryId}, ServiceTypeCategoryId = NULL",
                    userId: null,
                    source: "SystemStatusService.GetMoneyDistributionAsync",
                    relatedEntityType: "StatusConfiguration",
                    relatedEntityId: null
                );
                
                config = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Where(sc => sc.StatusId == status.Id &&
                                sc.CategoryId == categoryId &&
                                sc.ServiceTypeCategoryId == null &&
                                sc.IsActive)
                    .FirstOrDefaultAsync();

                if (config != null)
                {
                    await _loggingService.LogInfoAsync(
                        message: "GetMoneyDistributionAsync - Configuración por categoría encontrada",
                        details: $"Configuración encontrada: Id = {config.Id}",
                        userId: null,
                        source: "SystemStatusService.GetMoneyDistributionAsync",
                        relatedEntityType: "StatusConfiguration",
                        relatedEntityId: config.Id
                    );
                    return config;
                }
                
                // 3. Buscar configuración global (categoría = NULL, tipo = NULL)
                await _loggingService.LogInfoAsync(
                    message: "GetMoneyDistributionAsync - Buscando configuración global",
                    details: $"Ejecutando consulta: StatusConfigurations WHERE StatusId = {status.Id}, CategoryId = NULL, ServiceTypeCategoryId = NULL",
                    userId: null,
                    source: "SystemStatusService.GetMoneyDistributionAsync",
                    relatedEntityType: "StatusConfiguration",
                    relatedEntityId: null
                );
                
                config = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Where(sc => sc.StatusId == status.Id &&
                                sc.CategoryId == null &&
                                sc.ServiceTypeCategoryId == null &&
                                sc.IsActive)
                    .FirstOrDefaultAsync();

                await _loggingService.LogInfoAsync(
                    message: "GetMoneyDistributionAsync - Búsqueda completada",
                    details: $"Resultado: {(config != null ? $"Configuración encontrada: Id = {config.Id}" : "No se encontró configuración")}",
                    userId: null,
                    source: "SystemStatusService.GetMoneyDistributionAsync",
                    relatedEntityType: "StatusConfiguration",
                    relatedEntityId: config?.Id
                );

                return config;
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: GetMoneyDistributionAsync - Error en consulta",
                    details: $"Error al buscar configuración para statusValue: {statusValue}, categoryId: {categoryId}, serviceTypeCategoryId: {serviceTypeCategoryId}. Error: {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: null,
                    source: "SystemStatusService.GetMoneyDistributionAsync",
                    relatedEntityType: "StatusConfiguration",
                    relatedEntityId: null,
                    additionalData: new
                    {
                        StatusValue = statusValue,
                        CategoryId = categoryId,
                        ServiceTypeCategoryId = serviceTypeCategoryId,
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                return null;
            }
        }

        /// <summary>
        /// Obtiene la configuración de distribución de dinero como DTO (wrapper para compatibilidad)
        /// </summary>
        public async Task<MoneyDistributionConfigDto?> GetMoneyDistributionConfigAsync(
            string statusValue, 
            int? categoryId, 
            int? serviceTypeCategoryId)
        {
            try
            {
                var config = await GetMoneyDistributionAsync(statusValue, categoryId, serviceTypeCategoryId);
                
                if (config != null)
                {
                    return new MoneyDistributionConfigDto
                    {
                        ClientPercentage = config.ClientPercentage,
                        ExpertPercentage = config.ExpertPercentage,
                        PlatformPercentage = config.PlatformPercentage,
                        Source = "centralized_system"
                    };
                }
                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// Obtiene el estado principal de SearchHire basado en un AppointmentStatus
        /// </summary>
        public async Task<SearchHireStatus?> GetTargetSearchHireStatusAsync(AppointmentStatus appointmentStatus)
        {
            try
            {
                var appointmentStatusValue = appointmentStatus.ToStringValue();
                
                await _loggingService.LogInfoAsync(
                    message: "GetTargetSearchHireStatusAsync - Iniciando búsqueda",
                    details: $"Buscando mapeo para AppointmentStatus: {appointmentStatus}, StatusValue: {appointmentStatusValue}",
                    userId: null,
                    source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                    relatedEntityType: "StatusMapping",
                    relatedEntityId: null
                );
                
                // Obtener el SourceStatus primero para evitar problemas con Include
                await _loggingService.LogInfoAsync(
                    message: "GetTargetSearchHireStatusAsync - Buscando SourceStatus",
                    details: $"Ejecutando consulta: SystemStatuses WHERE StatusValue = {appointmentStatusValue} AND StatusType = 'AppointmentStatus' AND IsActive = true",
                    userId: null,
                    source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                    relatedEntityType: "SystemStatus",
                    relatedEntityId: null
                );
                
                var sourceStatus = await _context.SystemStatuses
                    .FirstOrDefaultAsync(ss => ss.StatusValue == appointmentStatusValue && 
                                               ss.StatusType == "AppointmentStatus" && 
                                               ss.IsActive);
                
                if (sourceStatus == null)
                {
                    await _loggingService.LogWarningAsync(
                        message: "GetTargetSearchHireStatusAsync - SourceStatus no encontrado, usando mapeo por defecto",
                        details: $"No se encontró SystemStatus con StatusValue: {appointmentStatusValue}, usando mapeo por defecto",
                        userId: null,
                        source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                        relatedEntityType: "SystemStatus",
                        relatedEntityId: null
                    );
                    // Fallback a mapeo por defecto si no existe en BD
                    return GetDefaultMapping(appointmentStatus);
                }
                
                await _loggingService.LogInfoAsync(
                    message: "GetTargetSearchHireStatusAsync - SourceStatus encontrado",
                    details: $"SourceStatus encontrado: Id = {sourceStatus.Id}, StatusValue = {sourceStatus.StatusValue}",
                    userId: null,
                    source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                    relatedEntityType: "SystemStatus",
                    relatedEntityId: sourceStatus.Id
                );
                
                // Buscar el mapeo usando el SourceStatusId
                // Obtener el TargetStatusId primero para evitar problemas con Include en Where
                await _loggingService.LogInfoAsync(
                    message: "GetTargetSearchHireStatusAsync - Buscando TargetStatusIds",
                    details: $"Ejecutando consulta: SystemStatuses WHERE StatusType = 'SearchHireStatus' AND IsActive = true",
                    userId: null,
                    source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                    relatedEntityType: "SystemStatus",
                    relatedEntityId: null
                );
                
                var targetStatusIds = await _context.SystemStatuses
                    .Where(ss => ss.StatusType == "SearchHireStatus" && ss.IsActive)
                    .Select(ss => ss.Id)
                    .ToListAsync();
                
                await _loggingService.LogInfoAsync(
                    message: "GetTargetSearchHireStatusAsync - TargetStatusIds obtenidos",
                    details: $"Se obtuvieron {targetStatusIds.Count} TargetStatusIds: [{string.Join(", ", targetStatusIds)}]",
                    userId: null,
                    source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                    relatedEntityType: "SystemStatus",
                    relatedEntityId: null
                );
                
                await _loggingService.LogInfoAsync(
                    message: "GetTargetSearchHireStatusAsync - Buscando StatusMapping",
                    details: $"Ejecutando consulta: StatusMappings WHERE SourceStatusId = {sourceStatus.Id} AND TargetStatusId IN ({string.Join(", ", targetStatusIds)}) AND IsActive = true",
                    userId: null,
                    source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                    relatedEntityType: "StatusMapping",
                    relatedEntityId: null
                );
                
                var mapping = await _context.StatusMappings
                    .Include(sm => sm.TargetStatus)
                    .Where(sm => sm.SourceStatusId == sourceStatus.Id &&
                                targetStatusIds.Contains(sm.TargetStatusId) &&
                                sm.IsActive)
                    .FirstOrDefaultAsync();
                    
                if (mapping?.TargetStatus?.StatusValue != null)
                {
                    await _loggingService.LogInfoAsync(
                        message: "GetTargetSearchHireStatusAsync - StatusMapping encontrado",
                        details: $"StatusMapping encontrado: Id = {mapping.Id}, TargetStatus.StatusValue = {mapping.TargetStatus.StatusValue}",
                        userId: null,
                        source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                        relatedEntityType: "StatusMapping",
                        relatedEntityId: mapping.Id
                    );
                    
                    // Convertir string a enum usando el método personalizado
                    try
                    {
                        var result = SearchHireStatusExtensions.FromStringValue(mapping.TargetStatus.StatusValue);
                        await _loggingService.LogInfoAsync(
                            message: "GetTargetSearchHireStatusAsync - Conversión exitosa",
                            details: $"SearchHireStatus convertido: {result}",
                            userId: null,
                            source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                            relatedEntityType: "StatusMapping",
                            relatedEntityId: mapping.Id
                        );
                        return result;
                    }
                    catch (ArgumentException ex)
                    {
                        await _loggingService.LogErrorAsync(
                            message: "GetTargetSearchHireStatusAsync - Error en conversión",
                            details: $"Error al convertir StatusValue '{mapping.TargetStatus.StatusValue}' a SearchHireStatus: {ex.Message}",
                            userId: null,
                            source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                            relatedEntityType: "StatusMapping",
                            relatedEntityId: mapping.Id
                        );
                        return null;
                    }
                }

                await _loggingService.LogWarningAsync(
                    message: "GetTargetSearchHireStatusAsync - StatusMapping no encontrado, usando mapeo por defecto",
                    details: $"No se encontró StatusMapping para SourceStatusId: {sourceStatus.Id}, usando mapeo por defecto",
                    userId: null,
                    source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                    relatedEntityType: "StatusMapping",
                    relatedEntityId: null
                );

                // Fallback a mapeo por defecto si no existe en BD
                var defaultMapping = GetDefaultMapping(appointmentStatus);
                
                return defaultMapping;
            }
            catch (Exception ex)
            {
                await _loggingService.LogCriticalAsync(
                    message: "CRITICAL: GetTargetSearchHireStatusAsync - Error en consulta",
                    details: $"Error al buscar mapeo para AppointmentStatus: {appointmentStatus}. Error: {ex.Message}, StackTrace: {ex.StackTrace}",
                    userId: null,
                    source: "SystemStatusService.GetTargetSearchHireStatusAsync",
                    relatedEntityType: "StatusMapping",
                    relatedEntityId: null,
                    additionalData: new
                    {
                        AppointmentStatus = appointmentStatus.ToString(),
                        ErrorType = ex.GetType().Name,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace
                    }
                );
                return GetDefaultMapping(appointmentStatus);
            }
        }

        /// <summary>
        /// Obtiene todos los estados de un tipo específico
        /// </summary>
        public async Task<List<SystemStatus>> GetStatusesByTypeAsync(string statusType)
        {
            try
            {
                return await _context.SystemStatuses
                    .Where(ss => ss.StatusType == statusType && ss.IsActive)
                    .OrderBy(ss => ss.SortOrder)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                return new List<SystemStatus>();
            }
        }

        /// <summary>
        /// Obtiene un estado por su valor
        /// </summary>
        public async Task<SystemStatus?> GetStatusByValueAsync(string statusValue, string statusType)
        {
            try
            {
                return await _context.SystemStatuses
                    .Where(ss => ss.StatusValue == statusValue && 
                                ss.StatusType == statusType && 
                                ss.IsActive)
                    .FirstOrDefaultAsync();
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// Obtiene todos los mapeos de estados
        /// </summary>
        public async Task<List<StatusMapping>> GetStatusMappingsAsync()
        {
            try
            {
                // Cargar los mappings primero
                var mappings = await _context.StatusMappings
                    .Where(sm => sm.IsActive)
                    .ToListAsync();
                
                // Cargar los SystemStatuses relacionados
                var statusIds = mappings.SelectMany(m => new[] { m.SourceStatusId, m.TargetStatusId }).Distinct().ToList();
                var statuses = await _context.SystemStatuses
                    .Where(ss => statusIds.Contains(ss.Id))
                    .ToDictionaryAsync(ss => ss.Id);
                
                // Asignar las referencias de navegación manualmente
                foreach (var mapping in mappings)
                {
                    if (statuses.TryGetValue(mapping.SourceStatusId, out var sourceStatus))
                    {
                        mapping.SourceStatus = sourceStatus;
                    }
                    if (statuses.TryGetValue(mapping.TargetStatusId, out var targetStatus))
                    {
                        mapping.TargetStatus = targetStatus;
                    }
                }
                
                // Ordenar en memoria
                return mappings
                    .OrderBy(sm => sm.SourceStatus?.StatusType ?? "")
                    .ThenBy(sm => sm.SourceStatus?.SortOrder ?? 0)
                    .ToList();
            }
            catch (Exception ex)
            {
                return new List<StatusMapping>();
            }
        }

        /// <summary>
        /// Mapeo por defecto (fallback) para AppointmentStatus → SearchHireStatus
        /// </summary>
        private static SearchHireStatus? GetDefaultMapping(AppointmentStatus appointmentStatus)
        {
            return appointmentStatus switch
            {
                AppointmentStatus.AppointmentAwaitingReport => SearchHireStatus.AwaitingClientDecision,
                AppointmentStatus.AppointmentReportSent => SearchHireStatus.AwaitingClientDecision,
                // AppointmentStatus.AppointmentCancelledByClient => null, // No cambiar estado del SearchHire en primer rechazo
                AppointmentStatus.AppointmentCancelledByClientSecond => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCancelledByClientNoProposal => SearchHireStatus.Cancelled,
                // AppointmentStatus.AppointmentCancelledByExpert => null, // No cambiar estado del SearchHire en primer rechazo del experto
                AppointmentStatus.AppointmentCancelledByExpertSecond => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCancelledByExpertNoResponse => SearchHireStatus.Cancelled,
                // 🛡️ B2 FIX: tramos de cancelación + strike (FASE D / confirmación del experto). Aunque la fila
                // de StatusMappings en BD ya los cubre, este fallback C# NO los tenía → si el seed no corre tras
                // un rebuild (drift de migraciones), GetTargetSearchHireStatus devolvía null y el hire quedaba
                // desincronizado del appointment (patrón FRENTE 8). Todos finalizan → Cancelled.
                AppointmentStatus.AppointmentCancelledByClientGt24h => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCancelledByClient6to24h => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCancelledByClientLt6h => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCancelledByExpertStrike => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCancelledByNoResponse => SearchHireStatus.Cancelled, // [DEPRECATED] Mantener por compatibilidad
                AppointmentStatus.AppointmentCancelledByExpertRejection => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCancelledByNoReport => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCompleted => SearchHireStatus.Completed, // FIX #3: cita completada -> hire completed (no awaiting_client_decision)
                AppointmentStatus.AppointmentCompletedWithoutClientApproval => SearchHireStatus.Completed,
                // 🔁 Borrado de cuenta: se mapea a estados YA CONFIGURADOS en BD en vez de a los dedicados
                // *_account_delete (que NO tienen StatusConfiguration → el dinero quedaba en no-op y el hire
                // ATASCADO). Respeta la política ya codificada en AccountDeletionService:
                //   cliente borra  → "dar el dinero al experto"  → Completed (0/95/5, experto cobra)
                //   experto borra  → "reembolsar al cliente"     → Cancelled (100/0/0, cliente reembolsado)
                // Reutiliza configs existentes (sin inventar porcentajes). Esto también desatasca el fallback
                // EnsureStateChangedAsync, que resuelve el estado destino vía esta misma función.
                AppointmentStatus.AppointmentCancelledByClientAccountDelete => SearchHireStatus.Completed,
                AppointmentStatus.AppointmentCancelledByExpertAccountDelete => SearchHireStatus.Cancelled,
                _ => null // Otros estados no tienen mapeo directo
            };
        }
    }
}

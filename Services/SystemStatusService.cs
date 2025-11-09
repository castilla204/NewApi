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
        public SystemStatusService(AppDbContext context)
        {
            _context = context;
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
                // 1. Buscar configuración específica (categoría + tipo)
                var config = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Where(sc => sc.Status.StatusValue == statusValue &&
                                sc.CategoryId == categoryId &&
                                sc.ServiceTypeCategoryId == serviceTypeCategoryId &&
                                sc.IsActive)
                    .FirstOrDefaultAsync();

                if (config != null)
                {
                    return config;
                }
                else
                {
                }
                // 2. Buscar configuración por categoría (tipo = NULL)
                config = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Where(sc => sc.Status.StatusValue == statusValue &&
                                sc.CategoryId == categoryId &&
                                sc.ServiceTypeCategoryId == null &&
                                sc.IsActive)
                    .FirstOrDefaultAsync();

                if (config != null)
                {
                    return config;
                }
                else
                {
                }
                // 3. Buscar configuración global (categoría = NULL, tipo = NULL)
                config = await _context.StatusConfigurations
                    .Include(sc => sc.Status)
                    .Where(sc => sc.Status.StatusValue == statusValue &&
                                sc.CategoryId == null &&
                                sc.ServiceTypeCategoryId == null &&
                                sc.IsActive)
                    .FirstOrDefaultAsync();

                if (config != null)
                {
                    return config;
                }
                else
                {
                }
                return null;
            }
            catch (Exception ex)
            {
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
                
                var mapping = await _context.StatusMappings
                    .Include(sm => sm.SourceStatus)
                    .Include(sm => sm.TargetStatus)
                    .Where(sm => sm.SourceStatus.StatusValue == appointmentStatusValue &&
                                sm.SourceStatus.StatusType == "AppointmentStatus" &&
                                sm.TargetStatus.StatusType == "SearchHireStatus" &&
                                sm.IsActive)
                    .FirstOrDefaultAsync();
                if (mapping?.TargetStatus?.StatusValue != null)
                {
                    // Convertir string a enum usando el método personalizado
                    try
                    {
                        var result = SearchHireStatusExtensions.FromStringValue(mapping.TargetStatus.StatusValue);
                        return result;
                    }
                    catch (ArgumentException ex)
                    {
                        return null;
                    }
                }

                // Fallback a mapeo por defecto si no existe en BD
                var defaultMapping = GetDefaultMapping(appointmentStatus);
                
                return defaultMapping;
            }
            catch (Exception ex)
            {
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
                return await _context.StatusMappings
                    .Include(sm => sm.SourceStatus)
                    .Include(sm => sm.TargetStatus)
                    .Where(sm => sm.IsActive)
                    .OrderBy(sm => sm.SourceStatus.StatusType)
                    .ThenBy(sm => sm.SourceStatus.SortOrder)
                    .ToListAsync();
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
                // AppointmentStatus.AppointmentCancelledByExpert => null, // No cambiar estado del SearchHire en primer rechazo del experto
                AppointmentStatus.AppointmentCancelledByExpertSecond => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCancelledByNoResponse => SearchHireStatus.Cancelled,
                AppointmentStatus.AppointmentCancelledByExpertRejection => SearchHireStatus.Cancelled,
                _ => null // Otros estados no tienen mapeo directo
            };
        }
    }
}

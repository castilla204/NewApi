using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    /// <summary>
    /// Servicio centralizado para logging de acciones de usuario
    /// </summary>
    public interface IUserActionLoggingService
    {
        Task LogUserActionAsync(int userId, string action, string details, string? relatedEntityType = null, int? relatedEntityId = null);
        Task LogUserActionAsync(int userId, string action, object details, string? relatedEntityType = null, int? relatedEntityId = null);
        Task LogAdminActionAsync(int adminUserId, string action, string details, string? relatedEntityType = null, int? relatedEntityId = null);
        Task LogSystemActionAsync(string action, string details, string? relatedEntityType = null, int? relatedEntityId = null);
    }

    public class UserActionLoggingService : IUserActionLoggingService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<UserActionLoggingService> _logger;

        public UserActionLoggingService(AppDbContext context, ILogger<UserActionLoggingService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Registra una acción de usuario en la base de datos y en los logs
        /// </summary>
        public async Task LogUserActionAsync(int userId, string action, string details, string? relatedEntityType = null, int? relatedEntityId = null)
        {
            try
            {
                // Log en base de datos
                var logEntry = new Log
                {
                    LogLevel = "Information",
                    Message = $"User Action: {action}",
                    Details = details,
                    UserId = userId,
                    Source = "UserAction",
                    CreatedAt = DateTime.UtcNow
                };

                _context.Logs.Add(logEntry);
                await _context.SaveChangesAsync();

                // Log en sistema de logging
                _logger.LogInformation("USER_ACTION: UserId={UserId}, Action={Action}, Details={Details}, RelatedEntity={RelatedEntityType}:{RelatedEntityId}", 
                    userId, action, details, relatedEntityType, relatedEntityId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging user action: UserId={UserId}, Action={Action}", userId, action);
            }
        }

        /// <summary>
        /// Registra una acción de usuario con objeto de detalles
        /// </summary>
        public async Task LogUserActionAsync(int userId, string action, object details, string? relatedEntityType = null, int? relatedEntityId = null)
        {
            var detailsJson = System.Text.Json.JsonSerializer.Serialize(details);
            await LogUserActionAsync(userId, action, detailsJson, relatedEntityType, relatedEntityId);
        }

        /// <summary>
        /// Registra una acción de administrador
        /// </summary>
        public async Task LogAdminActionAsync(int adminUserId, string action, string details, string? relatedEntityType = null, int? relatedEntityId = null)
        {
            try
            {
                // Log en base de datos
                var logEntry = new Log
                {
                    LogLevel = "Warning", // Admin actions are more critical
                    Message = $"Admin Action: {action}",
                    Details = details,
                    UserId = adminUserId,
                    Source = "AdminAction",
                    CreatedAt = DateTime.UtcNow
                };

                _context.Logs.Add(logEntry);
                await _context.SaveChangesAsync();

                // Log en sistema de logging
                _logger.LogWarning("ADMIN_ACTION: AdminUserId={AdminUserId}, Action={Action}, Details={Details}, RelatedEntity={RelatedEntityType}:{RelatedEntityId}", 
                    adminUserId, action, details, relatedEntityType, relatedEntityId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging admin action: AdminUserId={AdminUserId}, Action={Action}", adminUserId, action);
            }
        }

        /// <summary>
        /// Registra una acción del sistema
        /// </summary>
        public async Task LogSystemActionAsync(string action, string details, string? relatedEntityType = null, int? relatedEntityId = null)
        {
            try
            {
                // Log en base de datos
                var logEntry = new Log
                {
                    LogLevel = "Information",
                    Message = $"System Action: {action}",
                    Details = details,
                    UserId = null, // System action
                    Source = "SystemAction",
                    CreatedAt = DateTime.UtcNow
                };

                _context.Logs.Add(logEntry);
                await _context.SaveChangesAsync();

                // Log en sistema de logging
                _logger.LogInformation("SYSTEM_ACTION: Action={Action}, Details={Details}, RelatedEntity={RelatedEntityType}:{RelatedEntityId}", 
                    action, details, relatedEntityType, relatedEntityId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging system action: Action={Action}", action);
            }
        }
    }
}

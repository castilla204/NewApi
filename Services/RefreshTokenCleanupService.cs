using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    /// <summary>
    /// ✅ SEGURIDAD 2025: Servicio para limpieza automática de refresh tokens expirados
    /// Se ejecuta periódicamente con Hangfire
    /// </summary>
    public class RefreshTokenCleanupService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<RefreshTokenCleanupService> _logger;

        public RefreshTokenCleanupService(AppDbContext context, ILogger<RefreshTokenCleanupService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Eliminar tokens expirados o revocados que tengan más de 30 días
        /// </summary>
        public async Task CleanupExpiredTokensAsync()
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-30);

                var tokensToDelete = await _context.RefreshTokens
                    .Where(rt => 
                        (rt.IsRevoked && rt.RevokedAt < cutoffDate) ||  // Revocados hace más de 30 días
                        (rt.ExpiresAt < cutoffDate)                      // Expirados hace más de 30 días
                    )
                    .ToListAsync();

                if (tokensToDelete.Any())
                {
                    _context.RefreshTokens.RemoveRange(tokensToDelete);
                    await _context.SaveChangesAsync();

                    _logger.LogInformation(
                        "Refresh token cleanup completed. Deleted {Count} expired/revoked tokens older than 30 days.",
                        tokensToDelete.Count
                    );
                }
                else
                {
                    _logger.LogInformation("Refresh token cleanup completed. No tokens to delete.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during refresh token cleanup");
                throw;
            }
        }

        /// <summary>
        /// Obtener estadísticas de tokens para monitoreo
        /// </summary>
        public async Task<RefreshTokenStats> GetTokenStatsAsync()
        {
            var now = DateTime.UtcNow;

            var stats = new RefreshTokenStats
            {
                TotalTokens = await _context.RefreshTokens.CountAsync(),
                ActiveTokens = await _context.RefreshTokens.CountAsync(rt => !rt.IsRevoked && rt.ExpiresAt > now),
                ExpiredTokens = await _context.RefreshTokens.CountAsync(rt => rt.ExpiresAt <= now),
                RevokedTokens = await _context.RefreshTokens.CountAsync(rt => rt.IsRevoked),
                OldestActiveToken = await _context.RefreshTokens
                    .Where(rt => !rt.IsRevoked && rt.ExpiresAt > now)
                    .OrderBy(rt => rt.CreatedAt)
                    .Select(rt => rt.CreatedAt)
                    .FirstOrDefaultAsync(),
                NewestActiveToken = await _context.RefreshTokens
                    .Where(rt => !rt.IsRevoked && rt.ExpiresAt > now)
                    .OrderByDescending(rt => rt.CreatedAt)
                    .Select(rt => rt.CreatedAt)
                    .FirstOrDefaultAsync()
            };

            return stats;
        }
    }

    public class RefreshTokenStats
    {
        public int TotalTokens { get; set; }
        public int ActiveTokens { get; set; }
        public int ExpiredTokens { get; set; }
        public int RevokedTokens { get; set; }
        public DateTime? OldestActiveToken { get; set; }
        public DateTime? NewestActiveToken { get; set; }
    }
}


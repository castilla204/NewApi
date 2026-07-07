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
        // 🛡️ Round 28 MUD-CG: lock multi-réplica. Cleanup diario; sin lock 2+ workers HPA
        // hacen DELETE concurrente — no catastrófico pero genera contention. 5min sobra.
        [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 300)]
        public async Task CleanupExpiredTokensAsync()
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-30);

                // 🛡️ FIX (auditoría 2026-07-06): DELETE en el servidor por lotes, en vez de materializar
                // TODA la tabla en memoria + RemoveRange + un único SaveChanges. RefreshTokens es de alta
                // rotación (1 fila/login-refresh); si el job se salta un tramo o hay pico de logins, el
                // backlog de filas viejas podía llegar a cientos de miles → pico de memoria y SaveChanges
                // masivo con riesgo de timeout (que el throw reintentaba con la MISMA carga). Era el único
                // cleanup sin batchear (logs/notificaciones/codes ya usan set-based). Se batchea con ctid
                // + LIMIT (idiom Postgres, mismo enfoque que CleanupOldLogsAndNotifications) para no
                // sostener un lock largo sobre la tabla; se repite hasta drenar.
                const int batchSize = 50000;
                int totalDeleted = 0;
                int deletedThisBatch;
                do
                {
                    deletedThisBatch = await _context.Database.ExecuteSqlInterpolatedAsync(
                        $@"DELETE FROM ""RefreshTokens"" WHERE ctid IN (
                             SELECT ctid FROM ""RefreshTokens""
                             WHERE (""IsRevoked"" = true AND ""RevokedAt"" < {cutoffDate})
                                OR (""ExpiresAt"" < {cutoffDate})
                             LIMIT {batchSize})");
                    totalDeleted += deletedThisBatch;
                } while (deletedThisBatch == batchSize);

                _logger.LogInformation(
                    "Refresh token cleanup completed. Deleted {Count} expired/revoked tokens older than 30 days.",
                    totalDeleted
                );
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


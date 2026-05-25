using Microsoft.EntityFrameworkCore;

namespace newApi.Services
{
    public static class ConcurrencyRetryHelper
    {
        // P2-4: helper para flujos críticos con xmin como concurrency token.
        // DbUpdateConcurrencyException NO es transient y por tanto el ExecutionStrategy
        // de EF Core no la cubre. Este helper recarga las entidades en conflicto y
        // reintenta la acción una vez. Para escenarios donde el caller no puede
        // re-derivar la mutación con datos frescos, debe responder HTTP 409 al cliente.
        public static async Task<T> SaveChangesWithRetryAsync<T>(
            DbContext context,
            Func<Task<T>> action,
            int maxRetries = 1)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    return await action();
                }
                catch (DbUpdateConcurrencyException ex) when (attempt < maxRetries)
                {
                    foreach (var entry in ex.Entries)
                    {
                        await entry.ReloadAsync();
                    }
                    attempt++;
                }
            }
        }

        public static Task SaveChangesWithRetryAsync(
            DbContext context,
            Func<Task> action,
            int maxRetries = 1)
        {
            return SaveChangesWithRetryAsync<object?>(context, async () =>
            {
                await action();
                return null;
            }, maxRetries);
        }
    }
}

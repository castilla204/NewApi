using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using Stripe;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 28 MUD-BF: sincroniza los datos legales DAC7 del experto desde un
    /// Stripe.Account a Dac7SellerSnapshots del año actual.
    ///
    /// Se llama desde el webhook handler account.updated (SubscriptionController:1779)
    /// después de ApplyStripeAccountState. Esto garantiza que cada vez que Stripe envía
    /// datos actualizados del onboarding del experto (DOB, address, external_accounts,
    /// individual.id_number), tenemos snapshot DAC7 inmediato.
    ///
    /// El job anual MUD-BB (@20-enero) luego completa los agregados (GrossSales/Fees/Count)
    /// encima de estos datos legales ya snapshoteados.
    ///
    /// Idempotente: si el snapshot del año actual ya existe, actualiza solo los campos
    /// legales y NO toca los agregados (que el job anual rellena al cierre del año).
    /// </summary>
    public class Dac7DataSyncService
    {
        private readonly AppDbContext _context;
        private readonly LoggingService _loggingService;

        public Dac7DataSyncService(AppDbContext context, LoggingService loggingService)
        {
            _context = context;
            _loggingService = loggingService;
        }

        public async Task SyncFromAccountAsync(
            Account? account,
            int expertProfileId,
            int? year = null,
            CancellationToken ct = default)
        {
            if (account == null || expertProfileId <= 0) return;
            int targetYear = year ?? DateTime.UtcNow.Year;

            try
            {
                var snapshot = await _context.Dac7SellerSnapshots
                    .FirstOrDefaultAsync(s => s.ExpertProfileId == expertProfileId && s.Year == targetYear, ct);

                bool isNew = snapshot == null;
                if (isNew)
                {
                    snapshot = new Dac7SellerSnapshot
                    {
                        ExpertProfileId = expertProfileId,
                        Year = targetYear,
                    };
                }

                // ----- Identity (individual o business) -----
                var individual = account.Individual;
                if (individual != null)
                {
                    snapshot!.LegalFirstName = individual.FirstName ?? snapshot.LegalFirstName;
                    snapshot.LegalLastName = individual.LastName ?? snapshot.LegalLastName;
                    if (individual.Dob != null && individual.Dob.Year.HasValue
                        && individual.Dob.Month.HasValue && individual.Dob.Day.HasValue)
                    {
                        try
                        {
                            snapshot.DateOfBirth = new DateOnly(
                                (int)individual.Dob.Year.Value,
                                (int)individual.Dob.Month.Value,
                                (int)individual.Dob.Day.Value);
                        }
                        catch { /* fecha inválida del lado Stripe, ignorar */ }
                    }
                    // individual.IdNumber se devuelve REDACTED por Stripe (last4 si está disponible)
                    // — no podemos persistirlo aquí en plaintext. El TIN real viene de Users.TaxId
                    // (cliente) o se introduce manualmente por admin desde Stripe Dashboard.

                    var addr = individual.Address;
                    if (addr != null)
                    {
                        snapshot.AddressLine = addr.Line1 ?? snapshot.AddressLine;
                        snapshot.AddressCity = addr.City ?? snapshot.AddressCity;
                        snapshot.AddressPostalCode = addr.PostalCode ?? snapshot.AddressPostalCode;
                        if (!string.IsNullOrEmpty(addr.Country))
                        {
                            snapshot.FiscalCountry = addr.Country.ToUpperInvariant();
                        }
                    }
                }

                // ----- Business profile fallback -----
                if (string.IsNullOrEmpty(snapshot!.FiscalCountry) && !string.IsNullOrEmpty(account.Country))
                {
                    snapshot.FiscalCountry = account.Country.ToUpperInvariant();
                }

                // ----- External accounts (bank account → IBAN last4) -----
                if (account.ExternalAccounts != null && account.ExternalAccounts.Data != null)
                {
                    var bankAccount = account.ExternalAccounts.Data
                        .OfType<BankAccount>()
                        .FirstOrDefault();
                    if (bankAccount != null && !string.IsNullOrEmpty(bankAccount.Last4))
                    {
                        snapshot.IbanLast4 = bankAccount.Last4;
                    }
                }

                // ----- Currency: si solo hay snapshot nuevo, dejamos ReportingCurrency=EUR.
                // El job anual MUD-BB añadirá los agregados en EUR (convertidos vía FX).

                snapshot.SnapshotCreatedAt = DateTime.UtcNow;

                if (isNew)
                {
                    _context.Dac7SellerSnapshots.Add(snapshot);
                }
                await _context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Non-blocking — el account.updated debe procesar incluso si el sync DAC7 falla.
                await _loggingService.LogWarningAsync(
                    message: "MUD-BF: failed to sync DAC7 data from Stripe Account (non-blocking)",
                    details: $"ExpertProfile {expertProfileId} acct {account?.Id}: {ex.Message}. El snapshot DAC7 del año {targetYear} no se actualizó con los datos del webhook; admin puede completar manualmente.",
                    source: "Dac7DataSyncService.SyncFromAccountAsync",
                    relatedEntityType: "Dac7SellerSnapshot",
                    relatedEntityId: expertProfileId);
            }
        }
    }
}

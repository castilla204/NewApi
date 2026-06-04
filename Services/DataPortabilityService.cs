using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ Round 28 — Sprint 2 (S2-P0-13): GDPR Art. 20 — Derecho a la Portabilidad.
    ///
    /// REQUISITO LEGAL
    /// ---------------
    /// RGPD Art. 20 (Reglamento UE 2016/679): el interesado tiene derecho a recibir sus datos
    /// personales en "formato estructurado, de uso común y de lectura mecánica" (JSON cumple).
    /// Plazo de respuesta: máximo 30 días desde la solicitud (Art. 12.3).
    ///
    /// AMBITO
    /// ------
    /// El export incluye los datos que el usuario PROPORCIONÓ o que se GENERARON sobre él durante
    /// el uso de la plataforma. NO incluye:
    ///  - Datos derivados/inferidos (NO obligatorio bajo Art. 20).
    ///  - Datos de terceros (otros usuarios mencionados en reviews/messages) — solo datos del propio.
    ///  - PCI/sensibles (StripeAccountId completo, password hash, etc. — sí incluido el StripeAccountId
    ///    redactado para que el usuario sepa que existe).
    ///
    /// USO
    /// ---
    /// GET /api/users/me/export?format=json
    /// Devuelve UserDataPortabilityDto con todos los datos del usuario autenticado.
    /// Logging: cada llamada se loguea como Information para auditoría AEPD.
    /// </summary>
    public class DataPortabilityService
    {
        private readonly AppDbContext _context;
        private readonly ILoggingService _loggingService;

        public DataPortabilityService(AppDbContext context, ILoggingService loggingService)
        {
            _context = context;
            _loggingService = loggingService;
        }

        public class UserDataPortabilityDto
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public string? Email { get; set; }
            public bool EmailVerified { get; set; }
            public System.DateTime? EmailVerifiedAt { get; set; }
            public string? PhoneNumber { get; set; }
            public bool PhoneVerified { get; set; }
            public string? Role { get; set; }
            public string? PreferredCurrency { get; set; }
            public System.DateTime CreatedAt { get; set; }
            public System.DateTime? UpdatedAt { get; set; }
            public bool IsBlocked { get; set; }
            public bool IsDeleted { get; set; }
            // Solo IDs externos (no la fila completa de Stripe — no es nuestro dato).
            public string? StripeAccountIdRedacted { get; set; }
            public string? GoogleIdRedacted { get; set; }
            public string? AppleIdRedacted { get; set; }

            public ExpertProfileExport? ExpertProfile { get; set; }
            public List<SearchHireExport> HiresAsClient { get; set; } = new();
            public List<SearchHireExport> HiresAsExpert { get; set; } = new();
            public List<ServiceExport> Services { get; set; } = new();
            public List<ReviewExport> ReviewsGiven { get; set; } = new();
            public List<ReviewExport> ReviewsReceived { get; set; } = new();
            public List<FinancialTransactionExport> FinancialTransactions { get; set; } = new();
            public List<DisputeExport> DisputesReported { get; set; } = new();
            public List<MessageExport> MessagesSent { get; set; } = new();
            public List<AppointmentExport> Appointments { get; set; } = new();
            // 🛡️ Round 28 MUD-BC: nuevos exports GDPR Art.20 completos.
            public List<ServiceImageExport> ServiceImages { get; set; } = new();
            public List<DeliverableExport> Deliverables { get; set; } = new();
            // Si MessagesSent fue truncado (Take cap), avisamos explícitamente al usuario.
            public bool MessagesTruncated { get; set; }
            public int? MessagesTotalAvailable { get; set; }

            // Metadata
            public System.DateTime ExportedAt { get; set; } = System.DateTime.UtcNow;
            public string DataController { get; set; } = "Inspecciono (Marketplace)";
            public string LegalBasis { get; set; } = "RGPD Art. 20 — Derecho a la Portabilidad de los datos personales";
            public string Format { get; set; } = "JSON estructurado, lectura mecánica";
            public string Notes { get; set; } =
                "Este export contiene los datos personales que la plataforma posee sobre ti. " +
                "Datos sensibles (password, StripeAccountId completo) se incluyen redactados. " +
                "Para una eliminación completa de tu cuenta (RGPD Art. 17), usa el flujo de eliminación en Ajustes.";
        }

        public class ExpertProfileExport
        {
            public int Id { get; set; }
            public string? Description { get; set; }
            public string? Country { get; set; }
            public string? City { get; set; }
            public string? Timezone { get; set; }
            public string? Latitude { get; set; }
            public string? Longitude { get; set; }
            public string? ProfilePictureUrl { get; set; }
            public bool OnboardingCompleted { get; set; }
            public string? StripeStatus { get; set; }
            public System.DateTime CreatedAt { get; set; }
        }

        public class SearchHireExport
        {
            public int Id { get; set; }
            public string? Status { get; set; }
            public decimal? Amount { get; set; }
            public decimal? BaseAmount { get; set; }
            public decimal? TaxAmount { get; set; }
            public string? Currency { get; set; }
            public string? ExpertCountry { get; set; }
            public string? ExpertTimezone { get; set; }
            public System.DateTime CreatedAt { get; set; }
            public System.DateTime? UpdatedAt { get; set; }
        }

        public class ServiceExport
        {
            public int Id { get; set; }
            public int CategoryId { get; set; }
            public int ServiceTypeId { get; set; }
            public decimal Price { get; set; }
            public string? Currency { get; set; }
            public string? Conditions { get; set; }
            public int? DurationInHours { get; set; }
            public bool IsActive { get; set; }
            public System.DateTime CreatedAt { get; set; }
        }

        public class ReviewExport
        {
            public int Id { get; set; }
            public int Score { get; set; }
            public string? Description { get; set; }
            public System.DateTime CreatedAt { get; set; }
            public string? Country { get; set; }
        }

        public class FinancialTransactionExport
        {
            public int Id { get; set; }
            public string? TransactionType { get; set; }
            public decimal AmountCents { get; set; }
            public string? Currency { get; set; }
            public string? StripePaymentIntentId { get; set; }
            public System.DateTime CreatedAt { get; set; }
        }

        public class DisputeExport
        {
            public int Id { get; set; }
            public string? Status { get; set; }
            public string? Description { get; set; }
            public System.DateTime CreatedAt { get; set; }
        }

        public class MessageExport
        {
            public int Id { get; set; }
            public string? ContentRedacted { get; set; }
            public System.DateTime CreatedAt { get; set; }
        }

        public class AppointmentExport
        {
            public int Id { get; set; }
            public string? Status { get; set; }
            public System.DateTime? ScheduledAt { get; set; }
            public System.DateTime CreatedAt { get; set; }
        }

        // 🛡️ Round 28 MUD-BC: imágenes de servicios y entregables que el usuario subió.
        // URLs públicas (Supabase Storage) — el usuario puede descargarlas con el link.
        public class ServiceImageExport
        {
            public int ServiceId { get; set; }
            public string? Url { get; set; }
            public System.DateTime UploadedAt { get; set; }
        }

        public class DeliverableExport
        {
            public int Id { get; set; }
            public int SearchHireId { get; set; }
            public string? Url { get; set; }
            public string? FileName { get; set; }
            public string? Description { get; set; }
            public System.DateTime CreatedAt { get; set; }
        }

        /// <summary>
        /// Compila el snapshot de datos del usuario para portabilidad GDPR Art. 20.
        /// Solo incluye datos del propio usuario (no de terceros). Cada llamada se audita.
        /// </summary>
        public async Task<UserDataPortabilityDto?> ExportUserDataAsync(int userId, CancellationToken ct = default)
        {
            var user = await _context.Users
                .AsNoTracking()
                .Include(u => u.ExpertProfile)
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user == null) return null;

            // Helper para redactar IDs externos (mantener visibilidad sin exponer el valor completo).
            string? Redact(string? id)
            {
                if (string.IsNullOrEmpty(id)) return null;
                if (id.Length <= 6) return "***";
                return id.Substring(0, 4) + "..." + id.Substring(id.Length - 4);
            }

            var dto = new UserDataPortabilityDto
            {
                Id = user.Id,
                Name = user.Name,
                Email = user.Email,
                EmailVerified = user.EmailVerified,
                EmailVerifiedAt = user.EmailVerifiedAt,
                PhoneNumber = user.PhoneNumber,
                PhoneVerified = user.PhoneVerified,
                Role = user.Role.ToString(), // enum → string
                PreferredCurrency = user.PreferredCurrency,
                CreatedAt = user.CreatedAt,
                UpdatedAt = null, // User no tiene UpdatedAt en modelo actual
                IsBlocked = user.IsBlocked,
                IsDeleted = user.IsDeleted,
                StripeAccountIdRedacted = Redact(user.ExpertProfile?.StripeAccountId),
                GoogleIdRedacted = Redact(user.GoogleId),
                AppleIdRedacted = Redact(user.AppleId),
            };

            if (user.ExpertProfile != null)
            {
                dto.ExpertProfile = new ExpertProfileExport
                {
                    Id = user.ExpertProfile.Id,
                    Description = user.ExpertProfile.Description,
                    Country = user.ExpertProfile.Country,
                    City = user.ExpertProfile.City,
                    Timezone = user.ExpertProfile.Timezone,
                    Latitude = user.ExpertProfile.Latitude,
                    Longitude = user.ExpertProfile.Longitude,
                    ProfilePictureUrl = user.ExpertProfile.ProfilePictureUrl,
                    OnboardingCompleted = user.ExpertProfile.OnboardingCompleted,
                    StripeStatus = user.ExpertProfile.StripeStatus.ToString(),
                    CreatedAt = user.ExpertProfile.CreatedAt
                };

                // Servicios publicados por este usuario como experto.
                dto.Services = await _context.SearchServices
                    .AsNoTracking()
                    .Where(s => s.ExpertProfileId == user.ExpertProfile.Id)
                    .Select(s => new ServiceExport
                    {
                        Id = s.Id,
                        CategoryId = s.CategoryId,
                        ServiceTypeId = s.ServiceTypeId,
                        Price = s.Price,
                        Currency = s.Currency,
                        Conditions = s.Conditions,
                        DurationInHours = s.DurationInHours,
                        IsActive = s.IsActive,
                        CreatedAt = s.CreatedAt
                    })
                    .ToListAsync(ct);

                dto.HiresAsExpert = await _context.SearchHires
                    .AsNoTracking()
                    .Where(h => h.ExpertId == userId)
                    .Select(h => new SearchHireExport
                    {
                        Id = h.Id,
                        Status = h.Status != null ? h.Status.StatusValue : null,
                        Amount = h.Amount,
                        BaseAmount = h.BaseAmount,
                        TaxAmount = h.TaxAmount,
                        Currency = h.Currency,
                        ExpertCountry = h.ExpertCountry,
                        ExpertTimezone = h.ExpertTimezone,
                        CreatedAt = h.CreatedAt,
                        UpdatedAt = h.UpdatedAt
                    })
                    .ToListAsync(ct);
            }

            dto.HiresAsClient = await _context.SearchHires
                .AsNoTracking()
                .Where(h => h.ClientId == userId)
                .Select(h => new SearchHireExport
                {
                    Id = h.Id,
                    Status = h.Status != null ? h.Status.StatusValue : null,
                    Amount = h.Amount,
                    BaseAmount = h.BaseAmount,
                    TaxAmount = h.TaxAmount,
                    Currency = h.Currency,
                    ExpertCountry = h.ExpertCountry,
                    ExpertTimezone = h.ExpertTimezone,
                    CreatedAt = h.CreatedAt,
                    UpdatedAt = h.UpdatedAt
                })
                .ToListAsync(ct);

            dto.ReviewsGiven = await _context.Reviews
                .AsNoTracking()
                .Where(r => r.ReviewerId == userId)
                .Select(r => new ReviewExport
                {
                    Id = r.Id,
                    Score = r.Score,
                    Description = r.Description,
                    CreatedAt = r.CreatedAt,
                    Country = r.SearchHire != null ? r.SearchHire.ExpertCountry : null
                })
                .ToListAsync(ct);

            dto.ReviewsReceived = await _context.Reviews
                .AsNoTracking()
                .Where(r => r.ExpertId == userId) // El "reviewee" en este modelo es el ExpertId
                .Select(r => new ReviewExport
                {
                    Id = r.Id,
                    Score = r.Score,
                    Description = r.Description,
                    CreatedAt = r.CreatedAt,
                    Country = r.SearchHire != null ? r.SearchHire.ExpertCountry : null
                })
                .ToListAsync(ct);

            dto.FinancialTransactions = await _context.FinancialTransactions
                .AsNoTracking()
                .Where(ft => ft.UserId == userId)
                .Select(ft => new FinancialTransactionExport
                {
                    Id = ft.Id,
                    TransactionType = ft.TransactionType,
                    AmountCents = ft.AmountCents,
                    Currency = ft.Currency,
                    StripePaymentIntentId = ft.StripePaymentIntentId,
                    CreatedAt = ft.CreatedAt
                })
                .ToListAsync(ct);

            dto.DisputesReported = await _context.Disputes
                .AsNoTracking()
                .Where(d => d.ReporterId == userId)
                .Select(d => new DisputeExport
                {
                    Id = d.Id,
                    Status = d.Status,
                    Description = d.Reason, // El modelo Dispute usa Reason, no Description
                    CreatedAt = d.CreatedAt
                })
                .ToListAsync(ct);

            // 🛡️ Round 28 MUD-BC: GDPR Art. 20 requiere export COMPLETO. Antes truncábamos
            // a 1000 messages SIN avisar al usuario → violación material. Ahora subimos a
            // 100.000 (suficiente para usuarios activos) y si superan, marcamos flag
            // MessagesTruncated=true + MessagesTotalAvailable para que sepa que tiene más.
            // Para casos extremos (chats automatizados, scrapers), admin puede generar
            // export ad-hoc por SQL directo.
            const int MESSAGE_CAP = 100_000;
            var totalMessages = await _context.Messages
                .AsNoTracking()
                .Where(m => m.SenderId == userId)
                .CountAsync(ct);
            dto.MessagesSent = await _context.Messages
                .AsNoTracking()
                .Where(m => m.SenderId == userId)
                .OrderByDescending(m => m.SentAt)
                .Take(MESSAGE_CAP)
                .Select(m => new MessageExport
                {
                    Id = m.Id,
                    ContentRedacted = m.Content,
                    CreatedAt = m.SentAt
                })
                .ToListAsync(ct);
            if (totalMessages > MESSAGE_CAP)
            {
                dto.MessagesTruncated = true;
                dto.MessagesTotalAvailable = totalMessages;
            }

            dto.Appointments = await _context.Appointments
                .AsNoTracking()
                .Where(a => a.SearchHire != null && (a.SearchHire.ClientId == userId || a.SearchHire.ExpertId == userId))
                .Select(a => new AppointmentExport
                {
                    Id = a.Id,
                    Status = a.Status != null ? a.Status.StatusValue : null,
                    ScheduledAt = a.ProposedDate, // Appointment usa ProposedDate, no ScheduledAt
                    CreatedAt = a.CreatedAt
                })
                .ToListAsync(ct);

            // 🛡️ Round 28 MUD-BC: imágenes de servicios subidas por el experto (datos del usuario).
            dto.ServiceImages = await _context.SearchServiceImages
                .AsNoTracking()
                .Where(img => img.SearchService != null
                           && img.SearchService.ExpertProfile != null
                           && img.SearchService.ExpertProfile.UserId == userId)
                .Select(img => new ServiceImageExport
                {
                    ServiceId = img.SearchServiceId,
                    Url = img.ImageUrl,
                    UploadedAt = img.CreatedAt
                })
                .ToListAsync(ct);

            // 🛡️ Round 28 MUD-BC: entregables que el usuario subió (como experto) o recibió (como cliente).
            dto.Deliverables = await _context.SearchHireDeliverables
                .AsNoTracking()
                .Where(d => d.SearchHire != null
                         && (d.SearchHire.ExpertId == userId || d.SearchHire.ClientId == userId))
                .Select(d => new DeliverableExport
                {
                    Id = d.Id,
                    SearchHireId = d.SearchHireId,
                    Url = d.Url,
                    FileName = d.ObjectName,
                    Description = d.Type,
                    CreatedAt = d.CreatedAt
                })
                .ToListAsync(ct);

            // Auditoría AEPD: cada export se loguea como Information.
            await _loggingService.LogInfoAsync(
                message: "GDPR Art. 20 — data export requested",
                details: $"Usuario {userId} solicitó export de sus datos personales. Filas: " +
                         $"hires_client={dto.HiresAsClient.Count}, hires_expert={dto.HiresAsExpert.Count}, " +
                         $"services={dto.Services.Count}, reviews_given={dto.ReviewsGiven.Count}, " +
                         $"reviews_received={dto.ReviewsReceived.Count}, ft={dto.FinancialTransactions.Count}, " +
                         $"disputes={dto.DisputesReported.Count}, messages={dto.MessagesSent.Count}" +
                         (dto.MessagesTruncated ? $" (TRUNCATED de {dto.MessagesTotalAvailable})" : "") +
                         $", appointments={dto.Appointments.Count}, service_images={dto.ServiceImages.Count}, " +
                         $"deliverables={dto.Deliverables.Count}.",
                userId: userId,
                source: "DataPortabilityService.ExportUserDataAsync",
                relatedEntityType: "User",
                relatedEntityId: userId);

            return dto;
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Stripe;
using Review = newApi.DataLayer.Models.PostGresModels.Review;
using Dispute = newApi.DataLayer.Models.PostGresModels.Dispute;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.DataLayer.Models
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Ad> Ads { get; set; }
        public DbSet<Like> Likes { get; set; }
        public DbSet<Search> Searches { get; set; }
        public DbSet<SearchParameter> SearchParameters { get; set; }
        public DbSet<SearchResult> SearchResults { get; set; }
        public DbSet<Platform> Platforms { get; set; }
        public DbSet<SearchResultFiltered> SearchResultsFiltered { get; set; }
        public DbSet<SearchParameterPlatform> SearchParameterPlatforms { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<PlatformCategoryMapping> PlatformCategoryMappings { get; set; }
        public DbSet<SubscriptionPlan> SubscriptionPlans { get; set; }
        public DbSet<UserSubscription> UserSubscriptions { get; set; }
        public DbSet<Notification> Notifications { get; set; }
        public DbSet<UserSetting> UserSettings { get; set; }
        public DbSet<SystemSetting> SystemSettings { get; set; }
        public DbSet<AI> AIs { get; set; }
        public DbSet<Log> Logs { get; set; }
        public DbSet<LogType> LogTypes { get; set; }
        public DbSet<Severity> Severities { get; set; }
        public DbSet<ExpertProfile> ExpertProfiles { get; set; }
        public DbSet<ExpertAvailability> ExpertAvailabilities { get; set; }
        public DbSet<SearchService> SearchServices { get; set; }
        public DbSet<SearchServiceImage> SearchServiceImages { get; set; }
        public DbSet<SearchHire> SearchHires { get; set; }
        // 📜 Round 9 — A2 FIX: audit log inmutable de cambios de estado de SearchHire (GDPR Art 5(2) accountability).
        public DbSet<SearchHireStatusHistory> SearchHireStatusHistories { get; set; }
        public DbSet<ReviewImage> ReviewImages { get; set; }
        public DbSet<Review> Reviews { get; set; }
        public DbSet<Dispute> Disputes { get; set; }
        public DbSet<DisputeFile> DisputeFiles { get; set; }
        public DbSet<FinancialTransaction> FinancialTransactions { get; set; }
        // 🔧 FISCAL FLIP: contadores de numeración correlativa de facturas (PK compuesta SeriesCode+Year).
        public DbSet<InvoiceCounter> InvoiceCounters { get; set; }
        public DbSet<ServiceType> ServiceTypes { get; set; }
        public DbSet<ServiceTypeCategory> ServiceTypeCategories { get; set; }
        public DbSet<Conversation> Conversations { get; set; }
        public DbSet<Message> Messages { get; set; }
        public DbSet<MessageAttachment> MessageAttachments { get; set; }
        public DbSet<SearchHireDeliverable> SearchHireDeliverables { get; set; }
        public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; set; }
        // 🛡️ Round 16: códigos OTP para verificación de email en registro/reset/step-up.
        public DbSet<EmailVerificationCode> EmailVerificationCodes { get; set; }
        public DbSet<Appointment> Appointments { get; set; }
        public DbSet<AppointmentTimer> AppointmentTimers { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }
        public DbSet<UserMfaSettings> UserMfaSettings { get; set; }
        public DbSet<DeliverableType> DeliverableTypes { get; set; }
        public DbSet<SearchServiceDeliverableType> SearchServiceDeliverableTypes { get; set; }
        
        // ✅ FAVORITOS: Servicios marcados como favoritos
        public DbSet<SearchServiceFavorite> SearchServiceFavorites { get; set; }
        
        // Tablas redundantes eliminadas - reemplazadas por StatusConfigurations
        // public DbSet<AppointmentStatusConfig> AppointmentStatusConfigs { get; set; }
        // public DbSet<ServiceTypeCategoryConfig> ServiceTypeCategoryConfigs { get; set; }
        // public DbSet<CategoryServiceTypeConfig> CategoryServiceTypeConfigs { get; set; }
        
        // Nueva arquitectura de estados centralizados
        public DbSet<SystemStatus> SystemStatuses { get; set; }
        public DbSet<StatusMapping> StatusMappings { get; set; }
        public DbSet<StatusConfiguration> StatusConfigurations { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Like>()
                .HasOne(l => l.User)
                .WithMany(u => u.Likes)
                .HasForeignKey(l => l.UserId)
                .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Like>()
                .HasOne(l => l.Ad)
                .WithMany(a => a.Likes)
                .HasForeignKey(l => l.AdId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Search>()
                .HasOne(s => s.User)
                .WithMany(u => u.Searches)
                .HasForeignKey(s => s.UserId)
                .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchParameter>()
                .HasOne(sp => sp.Search)
                .WithMany(s => s.SearchParameters)
                .HasForeignKey(sp => sp.SearchId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchParameter>()
                .HasOne(sp => sp.ServiceType)
                .WithMany()
                .HasForeignKey(sp => sp.ServiceTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<SearchResult>()
                .HasOne(sr => sr.Search)
                .WithMany(s => s.SearchResults)
                .HasForeignKey(sr => sr.SearchId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchResult>()
                .HasOne(sr => sr.Ad)
                .WithMany()
                .HasForeignKey(sr => sr.AdId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchResultFiltered>()
                .HasOne(srf => srf.Search)
                .WithMany()
                .HasForeignKey(srf => srf.SearchId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchResultFiltered>()
                .HasOne(srf => srf.Ad)
                .WithMany()
                .HasForeignKey(srf => srf.AdId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchParameterPlatform>()
                .HasKey(spp => new { spp.SearchParameterId, spp.PlatformId });

            modelBuilder.Entity<SearchParameterPlatform>()
                .HasOne(spp => spp.SearchParameter)
                .WithMany(sp => sp.SearchParameterPlatforms)
                .HasForeignKey(spp => spp.SearchParameterId);

            modelBuilder.Entity<SearchParameterPlatform>()
                .HasOne(spp => spp.Platform)
                .WithMany(p => p.SearchParameterPlatforms)
                .HasForeignKey(spp => spp.PlatformId);

            modelBuilder.Entity<Ad>()
                .HasOne(ad => ad.Platform)
                .WithMany(platform => platform.Ads)
                .HasForeignKey(ad => ad.PlatformId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Category>()
                .HasOne(c => c.Parent)
                .WithMany(c => c.Subcategories)
                .HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<PlatformCategoryMapping>()
                .HasOne(pcm => pcm.Platform)
                .WithMany()
                .HasForeignKey(pcm => pcm.PlatformId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PlatformCategoryMapping>()
                .HasOne(pcm => pcm.Category)
                .WithMany(c => c.PlatformCategoryMappings)
                .HasForeignKey(pcm => pcm.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<User>()
                .HasOne(u => u.SubscriptionPlan)
                .WithMany(sp => sp.Users)
                .HasForeignKey(u => u.SubscriptionPlanId)
                .OnDelete(DeleteBehavior.SetNull);

            // 🚨 RESTRICCIÓN DE BASE DE DATOS: Prevenir balances negativos
            modelBuilder.Entity<User>()
                .HasCheckConstraint("CK_Users_Balance_NonNegative", "\"Balance\" >= 0");
            
            // ✅ SOFT DELETE: Query filter para excluir usuarios eliminados automáticamente
            // NOTA: Para acceder a usuarios eliminados, usar .IgnoreQueryFilters()
            modelBuilder.Entity<User>()
                .HasQueryFilter(u => !u.IsDeleted);

            // 🛡️ Round 16: índices para login/lookup rápido.
            // Email: el lookup principal en login-password (case-insensitive — normalizar antes a lowercase).
            // No es UNIQUE porque conviven OAuth-only y password users + soft delete (mismo email puede reactivarse).
            // El uniqueness lógico ya se valida en el service layer (Register/GoogleAuth/AppleAuth).
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .HasDatabaseName("IX_Users_Email");

            // GoogleId: lookup en GoogleAuth para detectar usuario existente vía OAuth.
            modelBuilder.Entity<User>()
                .HasIndex(u => u.GoogleId)
                .HasDatabaseName("IX_Users_GoogleId");

            // AppleId: lookup en AppleAuth (sub claim del identityToken). Stable per (user, team).
            modelBuilder.Entity<User>()
                .HasIndex(u => u.AppleId)
                .HasDatabaseName("IX_Users_AppleId");

            modelBuilder.Entity<Notification>()
                .HasOne(n => n.User)
                .WithMany()
                .HasForeignKey(n => n.UserId)
                .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull para permitir anonimización

            modelBuilder.Entity<UserSubscription>()
                .HasOne(us => us.User)
                .WithMany(u => u.UserSubscriptions)
                .HasForeignKey(us => us.UserId)
                .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserSubscription>()
                .HasOne(us => us.SubscriptionPlan)
                .WithMany(sp => sp.UserSubscriptions)
                .HasForeignKey(us => us.SubscriptionPlanId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserSetting>()
                .HasOne(us => us.User)
                .WithOne(u => u.Settings)
                .HasForeignKey<UserSetting>(us => us.UserId)
                .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserSetting>()
                .HasOne(us => us.AI)
                .WithMany()
                .HasForeignKey(us => us.AIId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<SystemSetting>()
                .HasOne(ss => ss.AI)
                .WithMany()
                .HasForeignKey(ss => ss.AIId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<SystemSetting>()
                .HasIndex(ss => ss.Id)
                .IsUnique();

            modelBuilder.Entity<AI>()
                .HasIndex(ai => ai.Name)
                .IsUnique();

            modelBuilder.Entity<Log>()
                .HasOne(l => l.User)
                .WithMany()
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Log>()
                .HasIndex(l => l.CreatedAt);

            modelBuilder.Entity<ExpertProfile>()
                .HasOne(ep => ep.User)
                .WithOne(u => u.ExpertProfile)
                .HasForeignKey<ExpertProfile>(ep => ep.UserId)
                .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchService>()
                .HasOne(ss => ss.ExpertProfile)
                .WithMany(ep => ep.SearchServices)
                .HasForeignKey(ss => ss.ExpertProfileId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<SearchService>()
                .HasOne(ss => ss.AI)
                .WithMany()
                .HasForeignKey(ss => ss.AIId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<SearchService>()
                .HasOne(ss => ss.Category)
                .WithMany()
                .HasForeignKey(ss => ss.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<SearchService>()
                .HasOne(ss => ss.ServiceType)
                .WithMany()
                .HasForeignKey(ss => ss.ServiceTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            // 🌍 Round 21: ISO 4217 currency (3 chars). Default "EUR" para backward-compat
            // con todos los SearchService existentes. La validación de coincidencia con la
            // default_currency del Stripe Account del experto se hace en CreateSearchService.
            modelBuilder.Entity<SearchService>()
                .Property(ss => ss.Currency)
                .HasMaxLength(3)
                .HasDefaultValue("EUR");

            modelBuilder.Entity<SearchServiceImage>()
                .HasOne(ssi => ssi.SearchService)
                .WithMany(ss => ss.Images)
                .HasForeignKey(ssi => ssi.SearchServiceId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchHire>()
                .HasOne(sh => sh.Client)
                .WithMany(u => u.SearchHiresAsClient)
                .HasForeignKey(sh => sh.ClientId)
                .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull para permitir anonimización (ClientId ahora es nullable)

            modelBuilder.Entity<SearchHire>()
                .HasOne(sh => sh.Expert)
                .WithMany(u => u.SearchHiresAsExpert)
                .HasForeignKey(sh => sh.ExpertId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<SearchHire>()
                .HasOne(sh => sh.SearchService)
                .WithMany(ss => ss.SearchHires)
                .HasForeignKey(sh => sh.SearchServiceId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<SearchHire>()
                .HasOne(sh => sh.Search)
                .WithOne(s => s.SearchHire)
                .HasForeignKey<SearchHire>(sh => sh.SearchId)
                .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull para preservar SearchHires cuando se eliminan Searches

            // 🌍 Round 21: snapshot inmutable del currency en el hire (3-letter ISO 4217).
            // Default "EUR" para hires legacy pre-migración que no tenían la columna.
            modelBuilder.Entity<SearchHire>()
                .Property(sh => sh.Currency)
                .HasMaxLength(3)
                .HasDefaultValue("EUR");

            // 📜 Round 9 — A2 FIX: audit log de transiciones de estado de SearchHire.
            // Append-only por convención (sin método para borrar). Cascade desde SearchHire
            // (si se elimina la hire, su historial también — los registros fiscales clave
            // viven en FinancialTransactions con retención 6 años independiente).
            modelBuilder.Entity<SearchHireStatusHistory>()
                .HasOne(h => h.SearchHire)
                .WithMany()
                .HasForeignKey(h => h.SearchHireId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchHireStatusHistory>()
                .HasIndex(h => h.SearchHireId)
                .HasDatabaseName("IX_SearchHireStatusHistory_SearchHireId");

            modelBuilder.Entity<SearchHireStatusHistory>()
                .HasIndex(h => h.CreatedAt)
                .HasDatabaseName("IX_SearchHireStatusHistory_CreatedAt");

            modelBuilder.Entity<Review>()
                .HasOne(r => r.Reviewer)
                .WithMany(u => u.ReviewsGiven)
                .HasForeignKey(r => r.ReviewerId)
                .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull para permitir anonimización

            modelBuilder.Entity<Review>()
                .HasOne(r => r.Expert)
                .WithMany(u => u.ReviewsReceived)
                .HasForeignKey(r => r.ExpertId)
                .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull para permitir anonimización cuando el experto elimina su cuenta

            modelBuilder.Entity<Review>()
                .HasOne(r => r.SearchHire)
                .WithMany()
                .HasForeignKey(r => r.SearchHireId)
                .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull para permitir anonimización cuando se eliminan SearchHires

            modelBuilder.Entity<ReviewImage>()
                .HasOne(ri => ri.Review)
                .WithMany(r => r.ImagesCollection)
                .HasForeignKey(ri => ri.ReviewId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Dispute>()
                .HasOne(d => d.SearchHire)
                .WithMany(sh => sh.Disputes)
                .HasForeignKey(d => d.SearchHireId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Dispute>()
                .HasOne(d => d.Reporter)
                .WithMany(u => u.DisputesReported)
                .HasForeignKey(d => d.ReporterId)
                .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<DisputeFile>()
                .HasOne(df => df.Dispute)
                .WithMany(d => d.Files)
                .HasForeignKey(df => df.DisputeId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<DisputeFile>()
                .HasOne(df => df.UploadedByUser)
                .WithMany()
                .HasForeignKey(df => df.UploadedByUserId)
                .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<FinancialTransaction>()
                .HasOne(ft => ft.User)
                .WithMany(u => u.FinancialTransactions)
                .HasForeignKey(ft => ft.UserId)
                .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull en lugar de Cascade para preservar transacciones al eliminar usuario

            // 🔁 C1: índices únicos PARCIALES en el ledger = última línea de defensa anti doble-pago/
            // doble-reembolso (además de las claves de idempotencia de Stripe + los guardas en código).
            // (1) Cada refund de Stripe se registra UNA vez. (2) Cada transfer: un Payout y una
            // TransferReversal como máximo. (3) Un ServicePayment por contratación (anti doble-cobro).
            modelBuilder.Entity<FinancialTransaction>()
                .HasIndex(ft => ft.StripeRefundId)
                .IsUnique()
                .HasFilter("\"StripeRefundId\" IS NOT NULL");
            modelBuilder.Entity<FinancialTransaction>()
                .HasIndex(ft => new { ft.StripeTransferId, ft.TransactionType })
                .IsUnique()
                .HasFilter("\"StripeTransferId\" IS NOT NULL");
            modelBuilder.Entity<FinancialTransaction>()
                .HasIndex(ft => new { ft.RelatedEntityType, ft.RelatedEntityId })
                .IsUnique()
                .HasFilter("\"TransactionType\" = 'ServicePayment'");

            // 🛡️ F4 FIX: declaración única. Antes había DOS bloques idénticos (líneas 376-385
            // y 389-398 en el original) que declaraban los mismos dos índices duplicando
            // HasIndex(StripePaymentIntentId) con el mismo DatabaseName/filter. EF Core los
            // procesaba ambos y avisaba con warning. Mantenemos un único bloque.
            // P2-3: defensa anti doble-cobro y doble-chargeback a nivel BD, complementando
            // las claves de idempotencia de Stripe. Ver SQL_UNIQUE_INDEXES_INTEGRITY.sql.
            modelBuilder.Entity<FinancialTransaction>()
                .HasIndex(ft => ft.StripePaymentIntentId)
                .HasDatabaseName("IX_FT_StripePaymentIntent_ServicePayment_uq")
                .IsUnique()
                .HasFilter("\"StripePaymentIntentId\" IS NOT NULL AND \"TransactionType\" = 'ServicePayment'");
            modelBuilder.Entity<FinancialTransaction>()
                .HasIndex(ft => ft.StripePaymentIntentId)
                .HasDatabaseName("IX_FT_StripePaymentIntent_Chargeback_uq")
                .IsUnique()
                .HasFilter("\"StripePaymentIntentId\" IS NOT NULL AND \"TransactionType\" = 'Chargeback'");

            modelBuilder.Entity<ServiceType>(entity =>
            {
                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(100);
                entity.Property(e => e.Description)
                    .HasMaxLength(500);
                entity.Property(e => e.IsActive)
                    .HasDefaultValue(true);
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            modelBuilder.Entity<Conversation>(entity =>
            {
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.IsActive)
                    .HasDefaultValue(true);

                // ✅ Relación con SearchHire (nullable para conversaciones previas)
                entity.HasOne(c => c.SearchHire)
                    .WithMany(sh => sh.Conversations)
                    .HasForeignKey(c => c.SearchHireId)
                    .IsRequired(false) // ✅ Nullable para conversaciones previas a contratar
                    .OnDelete(DeleteBehavior.Cascade);

                // ✅ NUEVO: Relación con SearchService (nullable para conversaciones post-contratación)
                entity.HasOne(c => c.SearchService)
                    .WithMany()
                    .HasForeignKey(c => c.SearchServiceId)
                    .IsRequired(false) // Nullable para conversaciones post-contratación
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(c => c.Client)
                    .WithMany(u => u.ConversationsAsClient)
                    .HasForeignKey(c => c.ClientId)
                    .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull para permitir anonimización

                entity.HasOne(c => c.Expert)
                    .WithMany(u => u.ConversationsAsExpert)
                    .HasForeignKey(c => c.ExpertId)
                    .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull para permitir anonimización

                // ✅ Índices para búsquedas eficientes
                entity.HasIndex(e => e.SearchHireId);
                entity.HasIndex(e => e.SearchServiceId);
            });

            modelBuilder.Entity<Message>(entity =>
                {
                    entity.Property(e => e.Content)
                        .HasMaxLength(5000); // Remove IsRequired() to allow NULL
                    entity.Property(e => e.SentAt)
                        .HasDefaultValueSql("CURRENT_TIMESTAMP");
                    entity.Property(e => e.IsRead)
                        .HasDefaultValue(false);
                    entity.Property(e => e.LocationLatitude)
                        .HasMaxLength(50);
                    entity.Property(e => e.LocationLongitude)
                        .HasMaxLength(50);

                    entity.HasOne(m => m.Conversation)
                        .WithMany(c => c.Messages)
                        .HasForeignKey(m => m.ConversationId)
                        .OnDelete(DeleteBehavior.Cascade);

                    entity.HasOne(m => m.Sender)
                        .WithMany(u => u.MessagesSent)
                    .HasForeignKey(m => m.SenderId)
                    .OnDelete(DeleteBehavior.SetNull); // ✅ SetNull para permitir anonimización
                });

            modelBuilder.Entity<MessageAttachment>(entity =>
            {
                entity.Property(e => e.Url)
                    .IsRequired()
                    .HasMaxLength(500);
                entity.Property(e => e.ObjectName)
                    .IsRequired()
                    .HasMaxLength(200);
                entity.Property(e => e.Type)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.HasOne(ma => ma.Message)
                    .WithMany(m => m.Attachments)
                    .HasForeignKey(ma => ma.MessageId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<SearchHireDeliverable>(entity =>
            {
                entity.Property(e => e.Url)
                    .IsRequired()
                    .HasMaxLength(500);
                entity.Property(e => e.ObjectName)
                    .IsRequired()
                    .HasMaxLength(200);
                entity.Property(e => e.Type)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.HasOne(sd => sd.SearchHire)
                    .WithMany(sh => sh.Deliverables)
                    .HasForeignKey(sd => sd.SearchHireId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // Configuración para ProcessedWebhookEvent
            modelBuilder.Entity<ProcessedWebhookEvent>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EventId)
                    .IsRequired()
                    .HasMaxLength(255);
                entity.Property(e => e.EventType)
                    .IsRequired()
                    .HasMaxLength(100);
                entity.Property(e => e.StripeAccountId)
                    .HasMaxLength(255);
                entity.Property(e => e.Status)
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasDefaultValue("Success");
                entity.Property(e => e.ProcessedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Índice único para evitar duplicados
                entity.HasIndex(e => e.EventId)
                    .IsUnique();

                // Índice para búsquedas por cuenta
                entity.HasIndex(e => e.StripeAccountId);

                // Índice para búsquedas por usuario
                entity.HasIndex(e => e.UserId);
            });

            // 🛡️ Round 16: Configuración de EmailVerificationCode (OTP de email).
            modelBuilder.Entity<EmailVerificationCode>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Email)
                    .IsRequired()
                    .HasMaxLength(320);
                entity.Property(e => e.CodeHash)
                    .IsRequired();
                entity.Property(e => e.Salt)
                    .IsRequired();
                entity.Property(e => e.VerificationToken)
                    .IsRequired()
                    .HasMaxLength(64);
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.AttemptCount)
                    .HasDefaultValue(0);
                entity.Property(e => e.MaxAttempts)
                    .HasDefaultValue(5);
                entity.Property(e => e.RequestIp)
                    .HasMaxLength(45);
                entity.Property(e => e.VerifyIp)
                    .HasMaxLength(45);

                // FK opcional a User (puede emitirse OTP pre-registro sin User asociado).
                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .IsRequired(false)
                    .OnDelete(DeleteBehavior.SetNull);

                // 🔎 Búsquedas frecuentes durante verify (último OTP activo por email+purpose).
                entity.HasIndex(e => new { e.Email, e.Purpose })
                    .HasDatabaseName("IX_EmailVerificationCodes_Email_Purpose");

                // 🔑 Token opaco devuelto al cliente — único + lookup O(log n) en verify-email.
                entity.HasIndex(e => e.VerificationToken)
                    .IsUnique()
                    .HasDatabaseName("IX_EmailVerificationCodes_VerificationToken");

                // 🧹 Cleanup job: filtra códigos expirados/no consumidos.
                entity.HasIndex(e => e.ExpiresAt)
                    .HasDatabaseName("IX_EmailVerificationCodes_ExpiresAt");

                // 🛡️ Anti-flooding: rate-limit por (email, purpose) en ventana corta.
                entity.HasIndex(e => new { e.Email, e.Purpose, e.CreatedAt })
                    .HasDatabaseName("IX_EmailVerificationCodes_Email_Purpose_CreatedAt");
            });

            // Configuración de ServiceTypeCategory
            modelBuilder.Entity<ServiceTypeCategory>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(100);
                entity.Property(e => e.Description)
                    .IsRequired()
                    .HasMaxLength(500);
                entity.Property(e => e.Position)
                    .HasDefaultValue(0);
                entity.Property(e => e.IsActive)
                    .HasDefaultValue(true);
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            // Configuración de ServiceType con relación a ServiceTypeCategory
            modelBuilder.Entity<ServiceType>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(100);
                entity.Property(e => e.Description)
                    .IsRequired()
                    .HasMaxLength(500);
                entity.Property(e => e.Position)
                    .HasDefaultValue(0);
                entity.Property(e => e.IsActive)
                    .HasDefaultValue(true);
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Relación con ServiceTypeCategory
                entity.HasOne(st => st.ServiceTypeCategory)
                    .WithMany(stc => stc.ServiceTypes)
                    .HasForeignKey(st => st.ServiceTypeCategoryId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // Configuración de Appointment
            modelBuilder.Entity<Appointment>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Location)
                    .HasMaxLength(500); // ✅ Nullable: Se asigna cuando el cliente propone la cita
                entity.Property(e => e.ProposedDate); // ✅ Nullable: Se asigna cuando el cliente propone la cita
                entity.Property(e => e.ProposedTime); // ✅ Nullable: Se asigna cuando el cliente propone la cita
                entity.Property(e => e.RejectionCount)
                    .HasDefaultValue(0);
                entity.Property(e => e.ClientCancellationCount)
                    .HasDefaultValue(0);
                entity.Property(e => e.ExpertCancellationCount)
                    .HasDefaultValue(0);
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Relación con SearchHire
                entity.HasOne(a => a.SearchHire)
                    .WithOne(sh => sh.Appointment)
                    .HasForeignKey<Appointment>(a => a.SearchHireId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Relación con SystemStatus
                entity.HasOne(a => a.Status)
                    .WithMany()
                    .HasForeignKey(a => a.StatusId)
                    .OnDelete(DeleteBehavior.Restrict);

                // Índices
                entity.HasIndex(e => e.SearchHireId);
                entity.HasIndex(e => e.StatusId);
                entity.HasIndex(e => e.ProposedDate);
            });

            // Configuración de AppointmentTimer
            modelBuilder.Entity<AppointmentTimer>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.TimerType)
                    .IsRequired()
                    .HasMaxLength(50);
                entity.Property(e => e.StartTime)
                    .IsRequired();
                entity.Property(e => e.EndTime)
                    .IsRequired();
                entity.Property(e => e.IsExpired)
                    .HasDefaultValue(false);
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.HangfireJobId)
                    .HasMaxLength(255);

                // Relación con Appointment
                entity.HasOne(at => at.Appointment)
                    .WithMany(a => a.Timers)
                    .HasForeignKey(at => at.AppointmentId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Índices
                entity.HasIndex(e => e.AppointmentId);
                entity.HasIndex(e => e.TimerType);
                entity.HasIndex(e => e.EndTime);
                entity.HasIndex(e => e.IsExpired);
                // 🔧 FIX G5: índice único PARCIAL — máximo UN timer ACTIVO por (cita, tipo). Es la garantía real
                // contra el TOCTOU del guard expert_report (también creado en prod vía CREATE UNIQUE INDEX).
                entity.HasIndex(e => new { e.AppointmentId, e.TimerType })
                    .IsUnique()
                    .HasFilter("\"IsExpired\" = false")
                    .HasDatabaseName("IX_AppointmentTimers_Appt_Type_Active");
            });

            // 🔧 FISCAL FLIP: InvoiceCounter usa clave compuesta (SeriesCode, Year).
            modelBuilder.Entity<InvoiceCounter>(entity =>
            {
                entity.HasKey(ic => new { ic.SeriesCode, ic.Year });
                entity.Property(ic => ic.SeriesCode).HasMaxLength(32).IsRequired();
                entity.Property(ic => ic.NextNumber).IsRequired();
            });

            // Configuración de la nueva arquitectura de estados
            modelBuilder.Entity<SystemStatus>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.StatusType, e.StatusValue }).IsUnique();
                entity.HasIndex(e => e.StatusType);
                entity.HasIndex(e => e.IsActive);
                
                // Mapeo explícito de columnas para evitar problemas con EF Core
                entity.Property(e => e.StatusValue)
                    .HasColumnName("StatusValue")
                    .IsRequired()
                    .HasMaxLength(50);
                entity.Property(e => e.StatusType)
                    .HasColumnName("StatusType")
                    .IsRequired()
                    .HasMaxLength(50);
            });

            modelBuilder.Entity<StatusMapping>(entity =>
            {
                entity.HasKey(e => e.Id);
                
                // Relación SourceStatus
                entity.HasOne(sm => sm.SourceStatus)
                    .WithMany(ss => ss.SourceMappings)
                    .HasForeignKey(sm => sm.SourceStatusId)
                    .OnDelete(DeleteBehavior.Restrict);
                
                // Relación TargetStatus
                entity.HasOne(sm => sm.TargetStatus)
                    .WithMany(ts => ts.TargetMappings)
                    .HasForeignKey(sm => sm.TargetStatusId)
                    .OnDelete(DeleteBehavior.Restrict);
                
                entity.HasIndex(e => new { e.SourceStatusId, e.TargetStatusId }).IsUnique();
                entity.HasIndex(e => e.IsActive);
            });

            modelBuilder.Entity<StatusConfiguration>(entity =>
            {
                entity.HasKey(e => e.Id);
                
                // Relación con SystemStatus
                entity.HasOne(sc => sc.Status)
                    .WithMany(ss => ss.StatusConfigurations)
                    .HasForeignKey(sc => sc.StatusId)
                    .OnDelete(DeleteBehavior.Cascade);
                
                // Relación con Category (opcional)
                entity.HasOne(sc => sc.Category)
                    .WithMany()
                    .HasForeignKey(sc => sc.CategoryId)
                    .OnDelete(DeleteBehavior.SetNull);
                
                // Relación con ServiceTypeCategory (opcional)
                entity.HasOne(sc => sc.ServiceTypeCategory)
                    .WithMany()
                    .HasForeignKey(sc => sc.ServiceTypeCategoryId)
                    .OnDelete(DeleteBehavior.SetNull);
                
                entity.HasIndex(e => new { e.StatusId, e.CategoryId, e.ServiceTypeCategoryId }).IsUnique();
                entity.HasIndex(e => e.IsActive);
            });

            // Configuración para DeliverableType
            modelBuilder.Entity<DeliverableType>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(50);
                entity.Property(e => e.DisplayName)
                    .IsRequired()
                    .HasMaxLength(100);
                entity.Property(e => e.Description)
                    .HasMaxLength(500);
                entity.Property(e => e.IsRequired)
                    .HasDefaultValue(false);
                entity.Property(e => e.IsActive)
                    .HasDefaultValue(true);
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                
                // Índice único para Name
                entity.HasIndex(e => e.Name)
                    .IsUnique();
            });

            // Configuración para SearchServiceDeliverableType
            modelBuilder.Entity<SearchServiceDeliverableType>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.IsSelected)
                    .HasDefaultValue(false);
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                
                // Relación con SearchService
                entity.HasOne(ssdt => ssdt.SearchService)
                    .WithMany(ss => ss.SelectedDeliverableTypes)
                    .HasForeignKey(ssdt => ssdt.SearchServiceId)
                    .OnDelete(DeleteBehavior.Cascade);
                
                // Relación con DeliverableType
                entity.HasOne(ssdt => ssdt.DeliverableType)
                    .WithMany(dt => dt.SearchServiceDeliverableTypes)
                    .HasForeignKey(ssdt => ssdt.DeliverableTypeId)
                    .OnDelete(DeleteBehavior.Restrict);
                
                // Índice único para evitar duplicados
                entity.HasIndex(e => new { e.SearchServiceId, e.DeliverableTypeId })
                    .IsUnique();
            });

            // Configuración de la relación Log-LogType
            modelBuilder.Entity<Log>()
                .HasOne(l => l.LogType)
                .WithMany(lt => lt.Logs)
                .HasForeignKey(l => l.LogTypeId)
                .OnDelete(DeleteBehavior.SetNull);

            // Configuración de la relación LogType-Severity (opcional)
            modelBuilder.Entity<LogType>()
                .HasOne(lt => lt.Severity)
                .WithMany(s => s.LogTypes)
                .HasForeignKey(lt => lt.SeverityId)
                .OnDelete(DeleteBehavior.SetNull);

            // ✅ SEGURIDAD 2025: Configuración de RefreshToken
            modelBuilder.Entity<RefreshToken>(entity =>
            {
                entity.HasOne(rt => rt.User)
                    .WithMany()
                    .HasForeignKey(rt => rt.UserId)
                    .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                    .OnDelete(DeleteBehavior.Cascade); // Eliminar tokens si se elimina usuario

                // Índices para rendimiento
                entity.HasIndex(rt => rt.Token).IsUnique();
                entity.HasIndex(rt => rt.UserId);
                entity.HasIndex(rt => rt.ExpiresAt);
                entity.HasIndex(rt => new { rt.UserId, rt.IsRevoked, rt.ExpiresAt });
            });

            // ✅ SEGURIDAD 2025: Configuración de UserMfaSettings
            modelBuilder.Entity<UserMfaSettings>(entity =>
            {
                entity.HasOne(mfa => mfa.User)
                    .WithMany()
                    .HasForeignKey(mfa => mfa.UserId)
                    .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                    .OnDelete(DeleteBehavior.Cascade); // Eliminar MFA si se elimina usuario

                // Un usuario solo puede tener una configuración MFA
                entity.HasIndex(mfa => mfa.UserId).IsUnique();
            });

            // ✅ FAVORITOS: Configuración de SearchServiceFavorite
            modelBuilder.Entity<SearchServiceFavorite>(entity =>
            {
                entity.HasKey(e => e.Id);
                
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Relación con User
                entity.HasOne(f => f.User)
                    .WithMany(u => u.FavoriteServices)
                    .HasForeignKey(f => f.UserId)
                    .IsRequired(false) // ✅ Opcional para evitar warning con query filter de User
                    .OnDelete(DeleteBehavior.Cascade); // Eliminar favoritos si se elimina usuario

                // Relación con SearchService
                entity.HasOne(f => f.SearchService)
                    .WithMany(ss => ss.FavoritedBy)
                    .HasForeignKey(f => f.SearchServiceId)
                    .OnDelete(DeleteBehavior.Cascade); // Eliminar favoritos si se elimina servicio

                // Índice único: un usuario no puede marcar el mismo servicio como favorito múltiples veces
                entity.HasIndex(e => new { e.UserId, e.SearchServiceId })
                    .IsUnique();
                
                // Índices para rendimiento
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.SearchServiceId);
                entity.HasIndex(e => e.CreatedAt);
            });

            // P2-4: concurrency token basado en la pseudo-columna xmin de PostgreSQL.
            // EF Core añadirá WHERE xmin = @originalXmin a cada UPDATE/DELETE de estas
            // entidades; si otro proceso modificó la fila entre Load y SaveChanges, se
            // lanzará DbUpdateConcurrencyException. NO requiere migración SQL (xmin es
            // una pseudo-columna implícita de cada tabla en Postgres).
            // En Npgsql.EntityFrameworkCore.PostgreSQL 10 el helper UseXminAsConcurrencyToken
            // no está disponible como extension de EntityTypeBuilder; usamos la API canónica
            // de EF Core (shadow property + IsConcurrencyToken + ValueGeneratedOnAddOrUpdate).
            ConfigureXminConcurrencyToken<Appointment>(modelBuilder);
            ConfigureXminConcurrencyToken<FinancialTransaction>(modelBuilder);
            ConfigureXminConcurrencyToken<SearchHire>(modelBuilder);
            ConfigureXminConcurrencyToken<User>(modelBuilder);
            ConfigureXminConcurrencyToken<ExpertProfile>(modelBuilder);
        }

        private static void ConfigureXminConcurrencyToken<TEntity>(ModelBuilder modelBuilder) where TEntity : class
        {
            modelBuilder.Entity<TEntity>()
                .Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }

        // 🛡️ Round 14 — Q15 FIX: AsyncLocal con contexto del actor para que el interceptor
        // de SaveChanges pueda enriquecer las filas SearchHireStatusHistory generadas
        // automáticamente. Los callers que quieran adjuntar source/reason/userId/extra
        // hacen: using (AppDbContext.BeginStatusAuditScope("Source.Method", reason, userId, extra)) { ... }
        // Si no se llama, la fila se genera igual pero con Source="auto:SaveChanges".
        public sealed class StatusAuditScope : IDisposable
        {
            public string? Source { get; }
            public string? Reason { get; }
            public int? ChangedByUserId { get; }
            public object? AdditionalData { get; }
            private readonly StatusAuditScope? _previous;
            internal StatusAuditScope(string? source, string? reason, int? changedByUserId, object? additionalData)
            {
                Source = source; Reason = reason; ChangedByUserId = changedByUserId; AdditionalData = additionalData;
                _previous = _current.Value;
                _current.Value = this;
            }
            public void Dispose()
            {
                _current.Value = _previous;
            }
            private static readonly System.Threading.AsyncLocal<StatusAuditScope?> _current = new();
            internal static StatusAuditScope? Current => _current.Value;
        }

        /// <summary>
        /// Inicia un scope que enriquece la próxima escritura de SearchHire.StatusId con
        /// metadata. Si el caller no abre scope, la fila se genera igual con Source="auto:SaveChanges".
        /// </summary>
        public static StatusAuditScope BeginStatusAuditScope(string? source = null, string? reason = null, int? changedByUserId = null, object? additionalData = null)
            => new StatusAuditScope(source, reason, changedByUserId, additionalData);

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            CaptureSearchHireStatusTransitions();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override System.Threading.Tasks.Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, System.Threading.CancellationToken cancellationToken = default)
        {
            CaptureSearchHireStatusTransitions();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        /// <summary>
        /// 🛡️ Q15 FIX: detecta toda mutación de SearchHire.StatusId (incluida creación) y
        /// añade fila SearchHireStatusHistory automáticamente. Cubre los 28 puntos del código
        /// que mutan StatusId, en lugar de los 3 únicos que A2 instrumentó manualmente.
        /// Esto resuelve GDPR Art 5(2) accountability sin riesgo de gaps futuros.
        /// </summary>
        private void CaptureSearchHireStatusTransitions()
        {
            // Snapshot del scope ANTES de iterar (callers pueden enrollar varios)
            var scope = StatusAuditScope.Current;
            var sourceDefault = scope?.Source ?? "auto:SaveChanges";
            var reason = scope?.Reason;
            var changedByUserId = scope?.ChangedByUserId;
            var additionalJson = scope?.AdditionalData != null
                ? System.Text.Json.JsonSerializer.Serialize(scope.AdditionalData)
                : null;
            // Limita el JSON a 4000 chars para evitar abusos
            if (additionalJson != null && additionalJson.Length > 4000)
            {
                additionalJson = additionalJson.Substring(0, 4000) + "...[truncated]";
            }

            var entries = ChangeTracker.Entries<PostGresModels.SearchHire>().ToList();
            foreach (var entry in entries)
            {
                if (entry.State == EntityState.Added)
                {
                    // 🛡️ Round 15 — R1 FIX (regresión Q15): el código original asignaba
                    // SearchHireId = entry.Entity.Id (== 0 al momento del Add) → violaba FK
                    // NOT NULL CASCADE de la migración y reventaba TODA creación de SearchHire.
                    // Solución: usar la nav property `SearchHire = entry.Entity` para que EF
                    // resuelva el FK por fixup tras INSERT del padre. Si EF no lo resuelve por
                    // alguna razón, queda fallback no-op (no añadir fila si el Id sigue 0).
                    var newStatusId = entry.Entity.StatusId;
                    SearchHireStatusHistories.Add(new PostGresModels.SearchHireStatusHistory
                    {
                        SearchHire = entry.Entity, // EF resuelve el FK al insertar el padre
                        OldStatusId = null,
                        NewStatusId = newStatusId,
                        ChangedByUserId = changedByUserId,
                        Source = sourceDefault,
                        Reason = reason,
                        AdditionalDataJson = additionalJson,
                        CreatedAt = System.DateTime.UtcNow
                    });
                }
                else if (entry.State == EntityState.Modified)
                {
                    var statusProp = entry.Property(nameof(PostGresModels.SearchHire.StatusId));
                    if (statusProp.IsModified)
                    {
                        var oldStatusId = (int?)statusProp.OriginalValue;
                        var newStatusId = (int)statusProp.CurrentValue;
                        if (oldStatusId != newStatusId)
                        {
                            // 🛡️ Round 15 — R1 FIX: en Modified el Id ya está poblado (>0),
                            // así que pasar SearchHireId escalar es correcto. Pero también
                            // pasamos la nav property por consistencia y defensa.
                            SearchHireStatusHistories.Add(new PostGresModels.SearchHireStatusHistory
                            {
                                SearchHire = entry.Entity,
                                SearchHireId = entry.Entity.Id,
                                OldStatusId = oldStatusId,
                                NewStatusId = newStatusId,
                                ChangedByUserId = changedByUserId,
                                Source = sourceDefault,
                                Reason = reason,
                                AdditionalDataJson = additionalJson,
                                CreatedAt = System.DateTime.UtcNow
                            });
                        }
                    }
                }
            }
        }
    }
}
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
        public DbSet<ExpertProfile> ExpertProfiles { get; set; }
        public DbSet<SearchService> SearchServices { get; set; }
        public DbSet<SearchServiceImage> SearchServiceImages { get; set; }
        public DbSet<SearchHire> SearchHires { get; set; }
        public DbSet<ReviewImage> ReviewImages { get; set; }
        public DbSet<Review> Reviews { get; set; }
        public DbSet<Dispute> Disputes { get; set; }
        public DbSet<DisputeFile> DisputeFiles { get; set; }
        public DbSet<FinancialTransaction> FinancialTransactions { get; set; }
        public DbSet<ServiceType> ServiceTypes { get; set; }
        public DbSet<ServiceTypeCategory> ServiceTypeCategories { get; set; }
        public DbSet<Conversation> Conversations { get; set; }
        public DbSet<Message> Messages { get; set; }
        public DbSet<MessageAttachment> MessageAttachments { get; set; }
        public DbSet<SearchHireDeliverable> SearchHireDeliverables { get; set; }
        public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; set; }
        public DbSet<Appointment> Appointments { get; set; }
        public DbSet<AppointmentTimer> AppointmentTimers { get; set; }
        public DbSet<AppointmentStatusConfig> AppointmentStatusConfigs { get; set; }
        public DbSet<ServiceTypeCategoryConfig> ServiceTypeCategoryConfigs { get; set; }
        public DbSet<CategoryServiceTypeConfig> CategoryServiceTypeConfigs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Like>()
                .HasOne(l => l.User)
                .WithMany(u => u.Likes)
                .HasForeignKey(l => l.UserId)
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

            modelBuilder.Entity<UserSubscription>()
                .HasOne(us => us.User)
                .WithMany(u => u.UserSubscriptions)
                .HasForeignKey(us => us.UserId)
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

            modelBuilder.Entity<SearchServiceImage>()
                .HasOne(ssi => ssi.SearchService)
                .WithMany(ss => ss.Images)
                .HasForeignKey(ssi => ssi.SearchServiceId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchHire>()
                .HasOne(sh => sh.Client)
                .WithMany(u => u.SearchHiresAsClient)
                .HasForeignKey(sh => sh.ClientId)
                .OnDelete(DeleteBehavior.Restrict);

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
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Review>()
                .HasOne(r => r.Reviewer)
                .WithMany(u => u.ReviewsGiven)
                .HasForeignKey(r => r.ReviewerId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Review>()
                .HasOne(r => r.Expert)
                .WithMany(u => u.ReviewsReceived)
                .HasForeignKey(r => r.ExpertId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Review>()
                .HasOne(r => r.SearchHire)
                .WithMany()
                .HasForeignKey(r => r.SearchHireId)
                .OnDelete(DeleteBehavior.Restrict);

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
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<DisputeFile>()
                .HasOne(df => df.Dispute)
                .WithMany(d => d.Files)
                .HasForeignKey(df => df.DisputeId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<FinancialTransaction>()
                .HasOne(ft => ft.User)
                .WithMany(u => u.FinancialTransactions)
                .HasForeignKey(ft => ft.UserId)
                .OnDelete(DeleteBehavior.Cascade);

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

                entity.HasOne(c => c.SearchHire)
                    .WithMany(sh => sh.Conversations)
                    .HasForeignKey(c => c.SearchHireId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(c => c.Client)
                    .WithMany(u => u.ConversationsAsClient)
                    .HasForeignKey(c => c.ClientId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(c => c.Expert)
                    .WithMany(u => u.ConversationsAsExpert)
                    .HasForeignKey(c => c.ExpertId)
                    .OnDelete(DeleteBehavior.Restrict);
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
                        .OnDelete(DeleteBehavior.Restrict);
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
                entity.Property(e => e.Status)
                    .IsRequired()
                    .HasMaxLength(50)
                    .HasDefaultValue("awaiting_appointment");
                entity.Property(e => e.Location)
                    .IsRequired()
                    .HasMaxLength(500);
                entity.Property(e => e.ProposedDate)
                    .IsRequired();
                entity.Property(e => e.ProposedTime)
                    .IsRequired();
                entity.Property(e => e.RejectionCount)
                    .HasDefaultValue(0);
                entity.Property(e => e.CancellationCount)
                    .HasDefaultValue(0);
                entity.Property(e => e.IsLocked)
                    .HasDefaultValue(false);
                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Relación con SearchHire
                entity.HasOne(a => a.SearchHire)
                    .WithOne(sh => sh.Appointment)
                    .HasForeignKey<Appointment>(a => a.SearchHireId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Índices
                entity.HasIndex(e => e.SearchHireId);
                entity.HasIndex(e => e.Status);
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
            });
        }
    }
}
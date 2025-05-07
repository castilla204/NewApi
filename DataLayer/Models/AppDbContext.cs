using Microsoft.EntityFrameworkCore;
using Stripe;
using Review = newApi.DataLayer.Models.PostGresModels.Review;
using Dispute = newApi.DataLayer.Models.PostGresModels.Dispute;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.PostGresModels.newApi.DataLayer.Models.PostGresModels;

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
        public DbSet<Review> Reviews { get; set; }
        public DbSet<Dispute> Disputes { get; set; }
        public DbSet<FinancialTransaction> FinancialTransactions { get; set; }

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
                .HasForeignKey(sr => sr.AdId)
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

            modelBuilder.Entity<Search>()
                .HasOne(s => s.Expert)
                .WithMany(u => u.ExpertSearches)
                .HasForeignKey(s => s.ExpertId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ExpertProfile>()
                .HasOne(ep => ep.User)
                .WithOne(u => u.ExpertProfile)
                .HasForeignKey<ExpertProfile>(ep => ep.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchService>()
                .HasOne(ss => ss.ExpertProfile)
                .WithMany(ep => ep.SearchServices)
                .HasForeignKey(ss => ss.ExpertProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SearchService>()
                .HasOne(ss => ss.Category)
                .WithMany()
                .HasForeignKey(ss => ss.CategoryId)
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
                .OnDelete(DeleteBehavior.Restrict);

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

            modelBuilder.Entity<FinancialTransaction>()
                .HasOne(ft => ft.User)
                .WithMany(u => u.FinancialTransactions)
                .HasForeignKey(ft => ft.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
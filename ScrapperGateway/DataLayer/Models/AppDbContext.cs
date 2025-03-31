using DataLayer.Models.PostGresModels;
using Microsoft.EntityFrameworkCore;

namespace DataLayer.Models
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions options) : base(options) { }

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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // User - Like - Ad relationships
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

            // User - Search relationship
            modelBuilder.Entity<Search>()
                .HasOne(s => s.User)
                .WithMany(u => u.Searches)
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Search - SearchParameter relationship
            modelBuilder.Entity<SearchParameter>()
                .HasOne(sp => sp.Search)
                .WithMany(s => s.SearchParameters)
                .HasForeignKey(sp => sp.SearchId)
                .OnDelete(DeleteBehavior.Cascade);

            // Search - SearchResult relationship
            modelBuilder.Entity<SearchResult>()
                .HasOne(sr => sr.Search)
                .WithMany(s => s.SearchResults)
                .HasForeignKey(sr => sr.SearchId)
                .OnDelete(DeleteBehavior.Cascade);

            // SearchResult - Ad relationship
            modelBuilder.Entity<SearchResult>()
                .HasOne(sr => sr.Ad)
                .WithMany()
                .HasForeignKey(sr => sr.AdId)
                .OnDelete(DeleteBehavior.Cascade);

            // SearchResultFiltered relationships
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

            // Configure the many-to-many relationship between SearchParameter and Platforms
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

            // Ad-Platform relationship
            modelBuilder.Entity<Ad>()
                .HasOne(ad => ad.Platform)
                .WithMany(platform => platform.Ads)
                .HasForeignKey(ad => ad.PlatformId)
                .OnDelete(DeleteBehavior.Restrict);

            // Category relationships
            modelBuilder.Entity<Category>()
                .HasOne(c => c.Parent)
                .WithMany(c => c.Subcategories)
                .HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            // Platform Category Mapping relationships
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

            // Relación entre User y SubscriptionPlan (un usuario puede tener solo un plan)
            modelBuilder.Entity<User>()
                .HasOne(u => u.SubscriptionPlan)
                .WithMany(sp => sp.Users)
                .HasForeignKey(u => u.SubscriptionPlanId)
                .OnDelete(DeleteBehavior.SetNull);  // Si el plan se elimina, el usuario tendrá SubscriptionPlanId en null

            // UserSubscription relationships
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


            //Notifications





        }
    }
}
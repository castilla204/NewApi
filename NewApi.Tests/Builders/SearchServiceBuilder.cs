using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Builders;

/// <summary>
/// Mínimo SearchService para enganchar SearchHires en tests. Categoría 1 y
/// ServiceType 1 deben existir en el seed o ser creados por el test antes.
/// </summary>
public class SearchServiceBuilder
{
    private int _expertProfileId;
    private int _categoryId = 1;
    private int _serviceTypeId = 1;
    private decimal _price = 100m;
    private string _currency = "EUR";
    private string _conditions = "Test conditions";
    private bool _isActive = true;
    private int? _durationInHours;

    public SearchServiceBuilder(int expertProfileId) { _expertProfileId = expertProfileId; }

    public SearchServiceBuilder WithCategory(int categoryId, int serviceTypeId)
    {
        _categoryId = categoryId;
        _serviceTypeId = serviceTypeId;
        return this;
    }
    public SearchServiceBuilder WithPrice(decimal price, string currency = "EUR")
    {
        _price = price;
        _currency = currency;
        return this;
    }
    public SearchServiceBuilder Inactive() { _isActive = false; return this; }
    public SearchServiceBuilder WithDuration(int hours) { _durationInHours = hours; return this; }

    public async Task<SearchService> PersistAsync(AppDbContext db)
    {
        // Asegura que existen los FK requeridos (Category, ServiceType).
        // Sin esto el seed no los provee y los tests fallan con 23503.
        if (!await db.Categories.AnyAsync(c => c.Id == _categoryId))
        {
            db.Categories.Add(new Category
            {
                Id = _categoryId,
                Name = $"Test Category {_categoryId}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        if (!await db.ServiceTypes.AnyAsync(s => s.Id == _serviceTypeId))
        {
            db.ServiceTypes.Add(new ServiceType
            {
                Id = _serviceTypeId,
                Name = $"Test ServiceType {_serviceTypeId}",
                Description = "Test service type",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var svc = new SearchService
        {
            ExpertProfileId = _expertProfileId,
            CategoryId = _categoryId,
            ServiceTypeId = _serviceTypeId,
            Price = _price,
            Currency = _currency,
            Conditions = _conditions,
            IsActive = _isActive,
            DurationInHours = _durationInHours,
            CreatedAt = DateTime.UtcNow,
        };
        db.SearchServices.Add(svc);
        await db.SaveChangesAsync();
        return svc;
    }
}

using DataLayer.Models;
using newApi.DataLayer.Models;

using AdModel = DataLayer.Models.AdModel;
namespace newApi.Services
{
    public interface IScrapperService
    {
        Task<List<AdModel>> SearchAsync(string keywords, string userSearch, int pagestoscrape,
           int? category, string? latitude, string? longitude, int? minprice, int? maxprice,
           int? brandId, int? modelId, bool analyze, bool isMultiPage, bool shippingAviable);

    }
}
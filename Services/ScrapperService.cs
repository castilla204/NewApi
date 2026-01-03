using AdModel = newApi.DataLayer.Models.AdModel;

namespace newApi.Services
{
    public class ScrapperService : IScrapperService
    {
        public ScrapperService()
        {
        }

        public async Task<List<AdModel>> SearchAsync(string keywords, string userSearch, int pagestoscrape,
            int? category, string? latitude, string? longitude, int? minprice, int? maxprice,
            int? brandId, int? modelId, bool analyze, bool isMultiPage, bool shippingAviable)
        {
            // RabbitMQ ha sido eliminado - retornar lista vacía
            // TODO: Implementar alternativa si es necesario
            await Task.CompletedTask;
            return new List<AdModel>();
        }
    }
}
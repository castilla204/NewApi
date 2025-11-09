using newApi.RabbitMQ;
using Newtonsoft.Json;
using AdModel = newApi.DataLayer.Models.AdModel;

namespace newApi.Services
{
    public class ScrapperService : IScrapperService
    {
        private readonly IRabbitMQService _rabbitMQService;
        public ScrapperService(IRabbitMQService rabbitMQService)
        {
            _rabbitMQService = rabbitMQService;
        }

        public async Task<List<AdModel>> SearchAsync(string keywords, string userSearch, int pagestoscrape,
            int? category, string? latitude, string? longitude, int? minprice, int? maxprice,
            int? brandId, int? modelId, bool analyze, bool isMultiPage, bool shippingAviable)
        {
            try
            {
                var searchRequest = new
                {
                    Keywords = keywords,
                    UserSearch = userSearch,
                    PagesToScrape = pagestoscrape,
                    Category = category,
                    Latitude = latitude,
                    Longitude = longitude,
                    MinPrice = minprice,
                    MaxPrice = maxprice,
                    BrandId = brandId,
                    ModelId = modelId,
                    Analyze = analyze,
                    IsMultiPage = isMultiPage,
                    ShippingAviable = shippingAviable
                };
                var result = await _rabbitMQService.SendAndReceiveAsync<List<AdModel>>(
                    "scrapper_request_queue",
                    "scrapper_response_queue",
                    searchRequest,
                    timeout: 120000 // 2 minute timeout
                );
                return result ?? new List<AdModel>();
            }
            catch (TimeoutException ex)
            {
                throw new Exception("The scrapper service is taking longer than expected to process your request. This may be due to the large amount of data being processed. Please try again or reduce the number of pages to scrape.", ex);
            }
            catch (Exception ex)
            {
                throw;
            }
        }
    }
}
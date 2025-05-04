using newApi.RabbitMQ;
using Newtonsoft.Json;
using AdModel = newApi.DataLayer.Models.AdModel;

namespace newApi.Services
{
    public class ScrapperService : IScrapperService
    {
        private readonly IRabbitMQService _rabbitMQService;
        private readonly ILogger<ScrapperService> _logger;

        public ScrapperService(IRabbitMQService rabbitMQService, ILogger<ScrapperService> logger)
        {
            _rabbitMQService = rabbitMQService;
            _logger = logger;
        }

        public async Task<List<AdModel>> SearchAsync(string keywords, string userSearch, int pagestoscrape,
            int? category, string? latitude, string? longitude, int? minprice, int? maxprice,
            int? brandId, int? modelId, bool analyze, bool isMultiPage, bool shippingAviable)
        {
            try
            {
                _logger.LogInformation("Starting search with keywords: {Keywords}", keywords);

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

                _logger.LogInformation("Sending request to scrapper service...");

                var result = await _rabbitMQService.SendAndReceiveAsync<List<AdModel>>(
                    "scrapper_request_queue",
                    "scrapper_response_queue",
                    searchRequest,
                    timeout: 120000 // 2 minute timeout
                );

                _logger.LogInformation("Search completed. Found {Count} results", result?.Count ?? 0);

                return result ?? new List<AdModel>();
            }
            catch (TimeoutException ex)
            {
                _logger.LogError(ex, "Request to scrapper service timed out");
                throw new Exception("The scrapper service is taking longer than expected to process your request. This may be due to the large amount of data being processed. Please try again or reduce the number of pages to scrape.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during search operation");
                throw;
            }
        }
    }
}
using DataLayer.Models;
using DataLayer.Models.DTOs;
using newApi.DataLayer.Models;
using newApi.RabbitMQ;
using AdModel = DataLayer.Models.AdModel;

namespace newApi.Services
{
    public class WebMixerService : IWebMixerService
    {
        private readonly IRabbitMQService _rabbitMQService;
        private readonly ILogger<WebMixerService> _logger;


        public WebMixerService(IRabbitMQService rabbitMQService, ILogger<WebMixerService> logger)
        {
            _rabbitMQService = rabbitMQService ?? throw new ArgumentNullException(nameof(rabbitMQService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<AdModel>> SearchAsync(SearchRequestDto request)
        {
            try
            {
                _logger.LogInformation("Starting search with keywords: {Keywords}", request.Keywords);

                var result = await _rabbitMQService.SendAndReceiveAsync<List<AdModel>>(
                    "scrapper_request_queue",
                    "scrapper_response_queue",
                    request
                );

                _logger.LogInformation("Search completed. Found {Count} results", result?.Count ?? 0);

                return result ?? new List<AdModel>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during search operation");
                throw;
            }
        }
    }
}
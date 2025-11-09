using newApi.DataLayer.Models.DTOs;
using newApi.RabbitMQ;
using AdModel = newApi.DataLayer.Models.AdModel;

namespace newApi.Services
{
    public class WebMixerService : IWebMixerService
    {
        private readonly IRabbitMQService _rabbitMQService;
        public WebMixerService(IRabbitMQService rabbitMQService)
        {
            _rabbitMQService = rabbitMQService ?? throw new ArgumentNullException(nameof(rabbitMQService));
        }

        public async Task<List<AdModel>> SearchAsync(SearchRequestDto request)
        {
            try
            {
                var result = await _rabbitMQService.SendAndReceiveAsync<List<AdModel>>(
                    "scrapper_request_queue",
                    "scrapper_response_queue",
                    request
                );
                return result ?? new List<AdModel>();
            }
            catch (Exception ex)
            {
                throw;
            }
        }
    }
}
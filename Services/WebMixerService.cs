using newApi.DataLayer.Models.DTOs;
using AdModel = newApi.DataLayer.Models.AdModel;

namespace newApi.Services
{
    public class WebMixerService : IWebMixerService
    {
        public WebMixerService()
        {
        }

        public async Task<List<AdModel>> SearchAsync(SearchRequestDto request)
        {
            // RabbitMQ ha sido eliminado - retornar lista vacía
            // TODO: Implementar alternativa si es necesario
            await Task.CompletedTask;
            return new List<AdModel>();
        }
    }
}
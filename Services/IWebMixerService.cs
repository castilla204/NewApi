using newApi.ScrapperGateway.DataLayer.Models.DTOs;
using AdModel = newApi.ScrapperGateway.DataLayer.Models.AdModel;

namespace newApi.Services
{
    public interface IWebMixerService
    {
        Task<List<AdModel>> SearchAsync(SearchRequestDto request);
    }
}
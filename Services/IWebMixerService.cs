using newApi.DataLayer.Models.DTOs;
using AdModel = newApi.DataLayer.Models.AdModel;

namespace newApi.Services
{
    public interface IWebMixerService
    {
        Task<List<AdModel>> SearchAsync(SearchRequestDto request);
    }
}
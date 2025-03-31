using DataLayer.Models;
using DataLayer.Models.DTOs;
using newApi.DataLayer.Models;
using AdModel = DataLayer.Models.AdModel;

namespace newApi.Services
{
    public interface IWebMixerService
    {
        Task<List<AdModel>> SearchAsync(SearchRequestDto request);
    }
}
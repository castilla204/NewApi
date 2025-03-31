using DataLayer.Models.PostGresModels;
using newApi.Controllers;

namespace newApi.Services
{
    public interface IGPTService
    {
        Task<SearchParamsResult> AnalyzeSearchInput(string userInput);
    }
}
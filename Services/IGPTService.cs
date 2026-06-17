using DataLayer.Models.PostGresModels;
using newApi.Controllers;

namespace newApi.Services
{
    public enum DescriptionKind
    {
        ExpertProfile,
        ServiceConditions
    }

    public interface IGPTService
    {
        Task<SearchParamsResult> AnalyzeSearchInput(string userInput);
        Task<string> RewriteDescription(DescriptionKind kind, string text);
    }
}

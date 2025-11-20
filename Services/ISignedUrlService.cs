using System.Net.Http;

namespace newApi.Services
{
    public interface ISignedUrlService
    {
        string? GetSignedUrl(string objectName, TimeSpan? lifetime = null, HttpMethod? httpMethod = null);
    }
}

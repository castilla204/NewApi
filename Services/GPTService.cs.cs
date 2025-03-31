using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataLayer.Models.PostGresModels;
using newApi.Controllers;

namespace newApi.Services
{
    public class GPTService : IGPTService
    {
        private readonly HttpClient _httpClient;
        private readonly string _openAiApiKey;
        private readonly ILogger<GPTService> _logger;

        public GPTService(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<GPTService> logger)
        {
            _httpClient = httpClientFactory.CreateClient();
            _openAiApiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI API key not found");
            _logger = logger;
        }

        public async Task<SearchParamsResult> AnalyzeSearchInput(string userInput)
        {
            try
            {
                var prompt = $@"Analiza el siguiente texto de búsqueda y extrae los parámetros relevantes en formato JSON. El texto es: ""{userInput}""

Debes devolver un JSON con esta estructura exacta:
{{
    ""title"": ""título descriptivo y atractivo para la búsqueda"",
    ""description"": ""descripción detallada de la búsqueda"",
    ""keywords"": ""palabras clave principales"",
    ""category"": número de categoría (1 para coches, 2 para motos, 3 para propiedades, null si no se especifica),
    ""minPrice"": precio mínimo en números (null si no se especifica),
    ""maxPrice"": precio máximo en números (null si no se especifica),
    ""brandId"": ID de la marca (1 para Tesla, 2 para BMW, etc., null si no se especifica),
    ""modelId"": ID del modelo (null si no se especifica),
    ""latitude"": ""latitud si se menciona ubicación"",
    ""longitude"": ""longitud si se menciona ubicación"",
    ""locationRange"": radio de búsqueda en kilómetros (null si no se especifica),
    ""shippingAvailable"": true/false basado en si se menciona envío
}}

Ejemplo de respuesta para 'Tesla Model S 2024 rojo con menos de 40000km':
{{
    ""title"": ""Tesla Model S 2024 - El deportivo eléctrico de tus sueños"",
    ""description"": ""Búsqueda de Tesla Model S del año 2024, en color rojo y con kilometraje inferior a 40,000 km"",
    ""keywords"": ""Tesla Model S 2024 rojo bajo kilometraje"",
    ""category"": 1,
    ""minPrice"": null,
    ""maxPrice"": null,
    ""brandId"": 1,
    ""modelId"": 1,
    ""latitude"": null,
    ""longitude"": null,
    ""locationRange"": null,
    ""shippingAvailable"": false
}}

Responde SOLO con el JSON, sin explicaciones adicionales.";

                var requestBody = new
                {
                    model = "gpt-3.5-turbo",
                    messages = new[]
                    {
                        new { role = "system", content = "Eres un asistente experto en análisis de texto y extracción de parámetros de búsqueda para vehículos y propiedades." },
                        new { role = "user", content = prompt }
                    },
                    max_tokens = 2500,
                    temperature = 0.5
                };

                var requestJson = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _openAiApiKey);
                var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                var completionResponse = JsonSerializer.Deserialize<CompletionResponse>(responseContent);

                if (completionResponse?.Choices == null || completionResponse.Choices.Length == 0)
                {
                    throw new Exception("No response from GPT");
                }

                var gptResponse = completionResponse.Choices[0].Message.Content;
                _logger.LogInformation("GPT Response: {Response}", gptResponse);

                return JsonSerializer.Deserialize<SearchParamsResult>(gptResponse, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new Exception("Failed to parse GPT response");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing search input with GPT");
                throw;
            }
        }

        private class CompletionResponse
        {
            public Choice[] Choices { get; set; }
        }

        private class Choice
        {
            public Message Message { get; set; }
        }

        private class Message
        {
            public string Content { get; set; }
        }
    }
}
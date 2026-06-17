using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using newApi.DataLayer.Models.PostGresModels;
using newApi.Controllers;

namespace newApi.Services
{
    public class GPTService : IGPTService
    {
        private readonly HttpClient _httpClient;
        private readonly string _openAiApiKey;
        public GPTService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient();
            _openAiApiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI API key not found");
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
                return JsonSerializer.Deserialize<SearchParamsResult>(gptResponse, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new Exception("Failed to parse GPT response");
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        private const string ExpertProfileSystemPrompt =
            "Reescribe la descripción profesional de un experto para que suene natural, cercana y " +
            "convincente, como si la hubiera escrito una persona real. Está en primera persona.\n" +
            "Reglas estrictas:\n" +
            "- Responde SOLO con el texto reescrito, sin comillas, sin saludos y sin explicaciones.\n" +
            "- Entre 30 y 60 caracteres (nunca menos de 30).\n" +
            "- Prohibido usar emojis, iconos o símbolos.\n" +
            "- Prohibido usar markdown, asteriscos, almohadillas, viñetas o guiones de lista.\n" +
            "- No inventes datos, títulos ni años de experiencia que no aparezcan en el texto original.\n" +
            "- Español de España, tono profesional y humano. Nada de frases de relleno.";

        private const string ServiceConditionsSystemPrompt =
            "Reescribe la descripción de un servicio (sus condiciones) para que quede clara, " +
            "profesional y atractiva, como si la hubiera escrito un profesional real dirigiéndose a " +
            "un posible cliente.\n" +
            "Reglas estrictas:\n" +
            "- Responde SOLO con el texto reescrito, sin comillas, sin saludos y sin explicaciones.\n" +
            "- Entre 400 y 1000 caracteres.\n" +
            "- Prohibido usar emojis, iconos o símbolos.\n" +
            "- Prohibido usar markdown, asteriscos, almohadillas, viñetas o listas con guiones.\n" +
            "- Escribe en párrafos de prosa normal.\n" +
            "- No inventes datos, garantías ni servicios que no aparezcan en el texto original; " +
            "solo mejora la redacción de lo aportado.\n" +
            "- Español de España, tono profesional y humano. Nada de frases de relleno.";

        public async Task<string> RewriteDescription(DescriptionKind kind, string text)
        {
            var systemPrompt = kind == DescriptionKind.ExpertProfile
                ? ExpertProfileSystemPrompt
                : ServiceConditionsSystemPrompt;
            var maxLength = kind == DescriptionKind.ExpertProfile ? 60 : 1000;

            var requestBody = new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = text }
                },
                max_tokens = 800,
                temperature = 0.6
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

            var raw = completionResponse.Choices[0].Message.Content;
            return DescriptionTextCleaner.Clean(raw, maxLength);
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
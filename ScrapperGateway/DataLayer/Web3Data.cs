using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using ScrapperGateway.Models.Wallapop;
using AutoMapper;
using DataLayer.Models;
using System.Net;

namespace DataLayer
{
    public class Web3Data : IWeb3Data
    {
        private readonly HttpClient client = new();
        private readonly string deviceId;
        private readonly string mpid;
        private const string APP_VERSION = "83070";
        private readonly IMapper _mapper;
        private string categoryString;

        public Web3Data(IMapper mapper)
        {
            _mapper = mapper;
            deviceId = Guid.NewGuid().ToString();
            mpid = GenerateMPID();
            // Comment out proxy usage and create a regular HttpClient
            //client = CreateHttpClientWithProxy(); // <--------------------------------------  Si esta descomentada se usa el proxy y la de abajo comentarla en ese caso
            client = new HttpClient();            
            SetupHttpClient();
        }

        private HttpClient CreateHttpClientWithProxy()
        {
            // Keep proxy configuration but don't use it by default
            var webProxy = new WebProxy("brd.superproxy.io:33335");
            var proxyCredentials = new NetworkCredential(
                "brd-customer-hl_116e4d7f-zone-datacenter_proxy1-country-es",
                "rors9j8p0x3c"
            );
            webProxy.Credentials = proxyCredentials;

            var handler = new HttpClientHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                PreAuthenticate = false,
                UseDefaultCredentials = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var client = new HttpClient(handler);

            return client;
        }

        private string GenerateMPID()
        {
            Random random = new Random();
            return (8000000000000000000 + random.Next(1999999999)).ToString();
        }

        private void SetupHttpClient()
        {
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Language", "es,es-ES;q=0.9");
            client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
            client.DefaultRequestHeaders.Add("DNT", "1");
            client.DefaultRequestHeaders.Add("DeviceOS", "0");
            client.DefaultRequestHeaders.Add("MPID", mpid);
            client.DefaultRequestHeaders.Add("Origin", "https://es.wallapop.com");
            client.DefaultRequestHeaders.Add("Pragma", "no-cache");
            client.DefaultRequestHeaders.Add("Referer", "https://es.wallapop.com/");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("X-AppVersion", APP_VERSION);
            client.DefaultRequestHeaders.Add("X-DeviceID", deviceId);
            client.DefaultRequestHeaders.Add("X-DeviceOS", "0");
            client.DefaultRequestHeaders.Add("sec-ch-ua", "\"Google Chrome\";v=\"129\", \"Not=A?Brand\";v=\"8\", \"Chromium\";v=\"129\"");
            client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
            client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
            client.Timeout = TimeSpan.FromSeconds(30); // Add timeout for proxy requests
        }






        public async Task<string> SearchWallapop(string keywords, int pagestoscrap, int? category, string? latitude, string? longitude, int? minprice, int? maxprice, bool shippingAviable, bool isProgrammed)
        {

            latitude = "41.76401"; //establecidas demomento para que no pille las del mapa
            longitude = "-2.46883";
            keywords = keywords ?? "quad";
            minprice = minprice ?? 1000;
            maxprice = maxprice ?? 2000;
            category = category ?? 0;
            string shipping = shippingAviable ? "true" : "";





            //TEMPORAL 
            isProgrammed = false;




            List<AdModel> anuncios = new List<AdModel>();

            try
            {
                await client.GetAsync("https://es.wallapop.com");
                await Task.Delay(TimeSpan.FromSeconds(2)); // Más tiempo entre solicitudes

                //await Task.Delay(TimeSpan.FromMilliseconds(new Random().Next(500, 1500)));

                for (int page = 0; page < pagestoscrap; page++)
                {
                    if (category != 0)
                    {
                        categoryString = $"category_ids={category}";
                    }
                    var start = page * 40;
                    var apiUrl = $"https://api.wallapop.com/api/v3/general/search?{categoryString}&keywords={keywords}" +
            $"&filters_source=search_box" +
            $"&latitude={latitude}" +
            $"&longitude={longitude}" +
            $"&min_sale_price={minprice}" +
            $"&max_sale_price={maxprice}" +
            $"&start={start}" +
            $"&show_multiple_sections=false" +
            $"&is_shippable={(shippingAviable ? "true" : "false")}" +
            (isProgrammed ? $"&time_filter=today" : string.Empty);


                    var response = await client.GetAsync(apiUrl);

                    // Verificar el estado de la respuesta
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Error en la petición: {response.StatusCode}");
                        continue;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    var data = JObject.Parse(json);

                    // deserealizar al objeto original
                    var pageAnuncios = JsonConvert.DeserializeObject<List<ScrapperGateway.Models.Wallapop.Root>>(data["search_objects"].ToString());



                    //mapear el objeto original al grup
                    var mappedAnuncios = _mapper.Map<List<AdModel>>(pageAnuncios);
                    var hola = mappedAnuncios;
                    anuncios.AddRange(mappedAnuncios);



                    if (page < 1) await Task.Delay(TimeSpan.FromSeconds(1));
                }



                return JsonConvert.SerializeObject(anuncios);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error en la petición: {ex.Message}");
                return JsonConvert.SerializeObject(new List<AdModel>());
            }
        }


    }
}
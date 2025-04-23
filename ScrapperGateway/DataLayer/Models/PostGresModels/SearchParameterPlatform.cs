namespace newApi.ScrapperGateway.DataLayer.Models.PostGresModels
{
    public class SearchParameterPlatform
    {
        //tabla muchos a muchos para las plataformas
        public int SearchParameterId { get; set; }
        public SearchParameter SearchParameter { get; set; }
        public int PlatformId { get; set; }
        public Platform Platform { get; set; }
    }
}

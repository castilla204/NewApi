namespace newApi.ScrapperGateway.DataLayer.Models.PostGresModels
{
    public class Ad
    {
        public string Id { get; set; }  // Cambiado de int a string para reflejar el tipo de identificador
        public string Description { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public decimal? Price { get; set; }
        public string[] Images { get; set; }
        public int? AdScore { get; set; }
        public int? FinalScore { get; set; }
        public string[] GoodThings { get; set; }
        public string[] BadThings { get; set; }
        public DateTimeOffset? PublishDate { get; set; }
        public string Category { get; set; }
        public int? CategoryId { get; set; }
        public string Province { get; set; }
        public int? ProvinceId { get; set; }
        public string City { get; set; }
        public int? CityId { get; set; }
        public bool Highlighted { get; set; }
        public bool IsNew { get; set; }
        public bool IsReserved { get; set; }
        public string Slug { get; set; }
        public string SellerType { get; set; }
        public string[] Tags { get; set; }
        public int PlatformId { get; set; }
        public virtual Platform Platform { get; set; }
        public DateTimeOffset? UpdateDate { get; set; }
        public DateTimeOffset? ScrappedDate { get; set; }
        public virtual ICollection<Like> Likes { get; set; }
    }
}
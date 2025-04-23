using AutoMapper;

using newApi.ScrapperGateway.DataLayer.Models;
using newApi.ScrapperGateway.DataLayer.Models.MilAnuncios;

namespace newApi.ScrapperGateway.DataLayer
{
    public class MilAdsMappingProfile : Profile
    {
        public MilAdsMappingProfile()
        {
            CreateMap<Root, AdModel>()
                .ForMember(dest => dest.id, opt => opt.MapFrom(src => src.id))
                .ForMember(dest => dest.description, opt => opt.MapFrom(src => src.description))
                .ForMember(dest => dest.title, opt => opt.MapFrom(src => src.title))
                .ForMember(dest => dest.url, opt => opt.MapFrom(src => $"https://www.milanuncios.com/{src.url}"))
                .ForMember(dest => dest.price, opt => opt.MapFrom(src => src.price.cashPrice.value))
                .ForMember(dest => dest.images, opt => opt.MapFrom(src => src.images.Select(AddImageRule).ToList())) // Add the rule to each image
                .ForMember(dest => dest.Adscore, opt => opt.MapFrom(src => src.Adscore))
                .ForMember(dest => dest.finalScore, opt => opt.MapFrom(src => src.finalScore))
                .ForMember(dest => dest.goodThings, opt => opt.MapFrom(src => src.goodThings))
                .ForMember(dest => dest.badThings, opt => opt.MapFrom(src => src.badThings))
                .ForMember(dest => dest.publishDate, opt => opt.MapFrom(src => src.publishDate))
                .ForMember(dest => dest.category, opt => opt.MapFrom(src => src.category.name))
                .ForMember(dest => dest.categoryId, opt => opt.MapFrom(src => src.categoryId))
                .ForMember(dest => dest.province, opt => opt.MapFrom(src => src.province.name))
                .ForMember(dest => dest.provinceId, opt => opt.MapFrom(src => src.province.id))
                .ForMember(dest => dest.city, opt => opt.MapFrom(src => src.city.name))
                .ForMember(dest => dest.cityId, opt => opt.MapFrom(src => src.city.id))
                .ForMember(dest => dest.highlighted, opt => opt.MapFrom(src => src.highlighted))
                .ForMember(dest => dest.isNew, opt => opt.MapFrom(src => src.isNew))
                .ForMember(dest => dest.isReserved, opt => opt.MapFrom(src => src.isReserved))
                .ForMember(dest => dest.slug, opt => opt.MapFrom(src => src.city.name))
                .ForMember(dest => dest.sellerType, opt => opt.MapFrom(src => src.sellerType))
                .ForMember(dest => dest.tags, opt => opt.MapFrom(src => src.tags.Select(t => t.text).ToList()))
                .ForMember(dest => dest.userId, opt => opt.MapFrom(src => src.userId))
                .ForMember(dest => dest.updateDate, opt => opt.MapFrom(src => src.updateDate))
                .ForMember(dest => dest.ScrappedDate, opt => opt.MapFrom(src => src.ScrappedDate))
                .ForMember(dest => dest.PlatformId, opt => opt.MapFrom(src => 2));
        }

        private static string AddImageRule(string imageUrl)
        {
            if (!string.IsNullOrEmpty(imageUrl) && !imageUrl.Contains("?rule="))
            {
                return $"https://{imageUrl}?rule=detail_432x320";
            }
            return imageUrl;
        }
    }
}

using AutoMapper;
using newApi.ScrapperGateway.DataLayer.Models.DTOs;
using newApi.ScrapperGateway.DataLayer.Models.PostGresModels;

namespace newApi.ScrapperGateway.DataLayer
{
    public class CategoryMappingProfile : Profile
    {
        public CategoryMappingProfile()
        {
            CreateMap<Category, CategoryDto>();
            CreateMap<CreateCategoryDto, Category>();
            CreateMap<UpdateCategoryDto, Category>();

            CreateMap<PlatformCategoryMapping, PlatformCategoryMappingDto>();
            CreateMap<CreatePlatformCategoryMappingDto, PlatformCategoryMapping>();
            CreateMap<UpdatePlatformCategoryMappingDto, PlatformCategoryMapping>();
        }
    }
}
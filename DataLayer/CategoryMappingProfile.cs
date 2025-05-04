using AutoMapper;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.DataLayer
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
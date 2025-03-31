using AutoMapper;
using DataLayer.Models.DTOs;
using DataLayer.Models.PostGresModels;

namespace DataLayer.Mapping
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
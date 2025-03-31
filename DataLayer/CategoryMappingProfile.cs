
    using AutoMapper;
    using DataLayer.Models.DTOs;
    using DataLayer.Models.PostGresModels;
    using global::DataLayer.Models.DTOs;
    using global::DataLayer.Models.PostGresModels;

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
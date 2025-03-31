using AutoMapper;
using DataLayer.Models.DTOs;
using DataLayer.Models.PostGresModels;

namespace newApi.DataLayer
{
    public class PlatformMappingProfile : Profile
    {
        public PlatformMappingProfile()
        {

            CreateMap<Platform, PlatformDTO>();

            CreateMap<PlatformDTO, Platform>()
                .ForMember(dest => dest.SearchParameterPlatforms, opt => opt.Ignore());
        }
    }
}

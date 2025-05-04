using AutoMapper;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.DataLayer.Models
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

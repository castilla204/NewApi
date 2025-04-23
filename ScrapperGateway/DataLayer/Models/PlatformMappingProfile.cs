using AutoMapper;
using newApi.ScrapperGateway.DataLayer.Models.DTOs;
using newApi.ScrapperGateway.DataLayer.Models.PostGresModels;

namespace newApi.ScrapperGateway.DataLayer.Models
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

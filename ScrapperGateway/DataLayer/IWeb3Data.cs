using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static newApi.ScrapperGateway.DataLayer.Web3Data;


namespace newApi.ScrapperGateway.DataLayer
{
    public interface IWeb3Data
    {

        Task<string> SearchWallapop(string keywords, int pagestoscrap, int? category, string? latitude, string? longitude, int? minprice, int? maxprice, bool shippingAviable, bool isProgrammed);
    }
}

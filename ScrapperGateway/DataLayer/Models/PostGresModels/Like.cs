using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace newApi.ScrapperGateway.DataLayer.Models.PostGresModels
{

    public class Like
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string AdId { get; set; } // Cambiado a string para coincidir con el tipo Id de Ad
        public DateTime CreatedAt { get; set; }

        public User User { get; set; }
        public Ad Ad { get; set; }
    }
}

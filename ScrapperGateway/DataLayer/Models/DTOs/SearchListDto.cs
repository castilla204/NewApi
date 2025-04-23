using System;

namespace newApi.ScrapperGateway.DataLayer.Models.DTOs
{
    public class SearchListDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public int Frequency { get; set; }
        public bool IsActive { get; set; }
        public bool IsRevised { get; set; }
        public DateTime LastExecution { get; set; }
        public DateTime NextExecution { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime StartDate { get; set; }
        public int Category { get; set; }
        public UserDto User { get; set; }
    }

    public class UserDto
    {
        public string Email { get; set; }
        public string Name { get; set; }
    }
}
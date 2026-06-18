using System;

namespace Clean_Room
{
    public class User
    {
        public int Id { get; set; }
        public string UserID { get; set; } = "";
        public string Password { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Role { get; set; } = "";
        public string Gender { get; set; } = "";
        public string Email { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}
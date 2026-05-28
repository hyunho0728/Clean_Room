using System;

namespace Clean_Room
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public string Password { get; set; } // 실제 서비스 시 암호화(Hashing) 권장
        public string FullName { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
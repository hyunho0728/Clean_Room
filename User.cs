using System;

namespace Clean_Room
{
    public class User
    {
        public int Id { get; set; }
        public string UserID { get; set; }      // 아이디
        public string Password { get; set; }   // 비밀번호
        public string FullName { get; set; }   // 이름
        public string Phone { get; set; }      // 전화번호
        public string Role { get; set; }       // 직무
        public string Gender { get; set; }     // 성별
        public string Email { get; set; }      // 이메일
        public DateTime CreatedAt { get; set; }
    }
}
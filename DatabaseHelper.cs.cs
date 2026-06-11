using System;
using System.IO;
using System.Runtime.CompilerServices; // [CallerFilePath] 사용을 위해 필요
using Microsoft.Data.Sqlite;

namespace Clean_Room
{
    public static class DatabaseHelper
    {
        // 1. 현재 이 cs 파일의 절대 경로를 자동으로 알아옵니다.
        private static string GetCurrentSourceDirectory([CallerFilePath] string sourceFilePath = "")
        {
            return Path.GetDirectoryName(sourceFilePath);
        }

        // 2. 알아온 cs 파일 폴더 경로와 파일명을 조합합니다.
        private static readonly string DbPath = Path.Combine(GetCurrentSourceDirectory(), "cleanroom.db");
        private static readonly string ConnectionString = $"Data Source={DbPath}";

        // 앱 시작 시 DB 및 테이블 초기화 (기존 코드와 동일)
        public static void InitializeDatabase()
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS Users (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserID TEXT UNIQUE NOT NULL,
                        Password TEXT NOT NULL,
                        FullName TEXT NOT NULL,
                        Phone TEXT NOT NULL,
                        Role TEXT NOT NULL,
                        Gender TEXT NOT NULL,
                        Email TEXT NOT NULL,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    );";

                using (var command = new SqliteCommand(createTableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }
            }
        }

        // 회원가입 처리 (기존 코드와 동일)
        public static bool RegisterUser(User user)
        {
            try
            {
                using (var connection = new SqliteConnection(ConnectionString))
                {
                    connection.Open();
                    string insertQuery = @"
                        INSERT INTO Users (UserID, Password, FullName, Phone, Role, Gender, Email)
                        VALUES ($userID, $password, $fullname, $phone, $role, $gender, $email);";

                    using (var command = new SqliteCommand(insertQuery, connection))
                    {
                        command.Parameters.AddWithValue("$userID", user.UserID);
                        command.Parameters.AddWithValue("$password", user.Password);
                        command.Parameters.AddWithValue("$fullname", user.FullName);
                        command.Parameters.AddWithValue("$phone", user.Phone);
                        command.Parameters.AddWithValue("$role", user.Role);
                        command.Parameters.AddWithValue("$gender", user.Gender);
                        command.Parameters.AddWithValue("$email", user.Email);

                        command.ExecuteNonQuery();
                        return true;
                    }
                }
            }
            catch (SqliteException ex)
            {
                if (ex.SqliteErrorCode == 19)
                {
                    return false;
                }
                throw;
            }
        }

        // 로그인 인증 처리 - 성공 시 User 객체 반환, 실패 시 null
        public static User AuthenticateUser(string userID, string password)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                string selectQuery = @"
                    SELECT Id, UserID, Password, FullName, Phone, Role, Gender, Email, CreatedAt
                    FROM Users
                    WHERE UserID = $userID AND Password = $password;";

                using (var command = new SqliteCommand(selectQuery, connection))
                {
                    command.Parameters.AddWithValue("$userID", userID);
                    command.Parameters.AddWithValue("$password", password);

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return new User
                            {
                                Id       = reader.GetInt32(0),
                                UserID   = reader.GetString(1),
                                Password = reader.GetString(2),
                                FullName = reader.GetString(3),
                                Phone    = reader.GetString(4),
                                Role     = reader.GetString(5),
                                Gender   = reader.GetString(6),
                                Email    = reader.GetString(7)
                            };
                        }
                        return null;
                    }
                }
            }
        }
    }
}
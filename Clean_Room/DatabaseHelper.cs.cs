using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Clean_Room
{
    public static class DatabaseHelper
    {
        private static string GetCurrentSourceDirectory([CallerFilePath] string sourceFilePath = "")
            => Path.GetDirectoryName(sourceFilePath);

        private static readonly string DbPath =
            Path.Combine(GetCurrentSourceDirectory(), "cleanroom.db");

        // Cache=Shared → 같은 프로세스 내 연결 간 캐시 공유 (WAL과 함께 동시성 향상)
        private static readonly string ConnectionString =
            $"Data Source={DbPath};Cache=Shared";

        // 쓰기 작업 직렬화 — SaveAlarm(타이머)과 RegisterUser(UI) 충돌 방지
        private static readonly object _writeLock = new object();

        // ── 연결 헬퍼 ─────────────────────────────────────────────
        private static SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            // 잠금 대기 5초, WAL 모드로 읽기/쓰기 충돌 최소화
            using var pragma = new SqliteCommand(
                "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;", conn);
            pragma.ExecuteNonQuery();
            return conn;
        }

        // ── 초기화 ────────────────────────────────────────────────
        public static void InitializeDatabase()
        {
            lock (_writeLock)
            using (var connection = OpenConnection())
            {
                using (var cmd = new SqliteCommand(@"
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
                    );", connection))
                    cmd.ExecuteNonQuery();

                using (var cmd = new SqliteCommand(@"
                    CREATE TABLE IF NOT EXISTS AlarmLogs (
                        Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                        Timestamp TEXT NOT NULL,
                        Room      TEXT NOT NULL DEFAULT '-',
                        Sensor    TEXT NOT NULL,
                        Value     REAL NOT NULL,
                        Unit      TEXT NOT NULL,
                        Threshold REAL NOT NULL
                    );", connection))
                    cmd.ExecuteNonQuery();

                // Room 컬럼 마이그레이션 (이미 있으면 예외 무시)
                try
                {
                    using var cmd2 = new SqliteCommand(
                        "ALTER TABLE AlarmLogs ADD COLUMN Room TEXT NOT NULL DEFAULT '-';",
                        connection);
                    cmd2.ExecuteNonQuery();
                }
                catch { }
            }
        }

        // ── 알람 저장 ──────────────────────────────────────────────
        public static void SaveAlarm(string sensor, double value, string unit, double threshold,
                                     string room = "-")
        {
            lock (_writeLock)
            using (var connection = OpenConnection())
            {
                using var cmd = new SqliteCommand(@"
                    INSERT INTO AlarmLogs (Timestamp, Room, Sensor, Value, Unit, Threshold)
                    VALUES ($ts, $room, $sensor, $value, $unit, $threshold);", connection);
                cmd.Parameters.AddWithValue("$ts",        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("$room",      room);
                cmd.Parameters.AddWithValue("$sensor",    sensor);
                cmd.Parameters.AddWithValue("$value",     value);
                cmd.Parameters.AddWithValue("$unit",      unit);
                cmd.Parameters.AddWithValue("$threshold", threshold);
                cmd.ExecuteNonQuery();
            }
        }

        // ── 알람 조회 ──────────────────────────────────────────────
        public static List<AlarmRecord> GetAlarms(DateTime? from = null, DateTime? to = null)
        {
            var list = new List<AlarmRecord>();
            using var connection = OpenConnection();

            string where = "";
            if (from.HasValue && to.HasValue)
                where = $"WHERE Timestamp BETWEEN '{from:yyyy-MM-dd HH:mm:ss}' AND '{to:yyyy-MM-dd HH:mm:ss}'";
            else if (from.HasValue)
                where = $"WHERE Timestamp >= '{from:yyyy-MM-dd HH:mm:ss}'";

            using var cmd = new SqliteCommand(
                $"SELECT Timestamp, Room, Sensor, Value, Unit, Threshold FROM AlarmLogs {where} ORDER BY Id DESC LIMIT 200;",
                connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new AlarmRecord
                {
                    Timestamp = reader.GetString(0),
                    Room      = reader.GetString(1),
                    Sensor    = reader.GetString(2),
                    Value     = reader.GetDouble(3),
                    Unit      = reader.GetString(4),
                    Threshold = reader.GetDouble(5)
                });
            return list;
        }

        // ── 회원가입 ───────────────────────────────────────────────
        public static bool RegisterUser(User user)
        {
            try
            {
                lock (_writeLock)
                using (var connection = OpenConnection())
                using (var cmd = new SqliteCommand(@"
                    INSERT INTO Users (UserID, Password, FullName, Phone, Role, Gender, Email)
                    VALUES ($userID, $password, $fullname, $phone, $role, $gender, $email);",
                    connection))
                {
                    cmd.Parameters.AddWithValue("$userID",   user.UserID);
                    cmd.Parameters.AddWithValue("$password", user.Password);
                    cmd.Parameters.AddWithValue("$fullname", user.FullName);
                    cmd.Parameters.AddWithValue("$phone",    user.Phone);
                    cmd.Parameters.AddWithValue("$role",     user.Role);
                    cmd.Parameters.AddWithValue("$gender",   user.Gender);
                    cmd.Parameters.AddWithValue("$email",    user.Email);
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return false; // UNIQUE 제약 위반 — 중복 아이디
            }
        }

        // ── 로그인 인증 ────────────────────────────────────────────
        public static User AuthenticateUser(string userID, string password)
        {
            using var connection = OpenConnection();
            using var cmd = new SqliteCommand(@"
                SELECT Id, UserID, Password, FullName, Phone, Role, Gender, Email
                FROM Users
                WHERE UserID = $userID AND Password = $password;", connection);
            cmd.Parameters.AddWithValue("$userID",   userID);
            cmd.Parameters.AddWithValue("$password", password);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
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
            return null;
        }
    }
}

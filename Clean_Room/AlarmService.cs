using System;
using System.Collections.Generic;

namespace Clean_Room
{
    /// <summary>
    /// 센서값이 임계값을 초과하면 AlarmLogs DB에 저장하고 AlarmTriggered 이벤트를 발생시킵니다.
    /// 같은 센서는 5초 쿨다운 적용 (중복 로그 방지).
    /// 2회 연속 초과는 AdminWindow의 DataUpdated 핸들러에서 별도로 판단하여 엔지니어 투입.
    /// </summary>
    public class AlarmService
    {
        private static readonly (string Name, Func<SensorData, double> Get, double DangerHi, string Unit)[] _rules =
        {
            ("온도",  d => d.Temperature,  30.0, "°C"  ),
            ("습도",  d => d.Humidity,     80.0, "%RH" ),
            ("압력",  d => d.Pressure,      0.1, "MPa" ), // 0.1 MPa 초과 시 경보 + 에어샤워
            ("진동",  d => d.Vibration,     1.5, "m/s²"),
            ("거리",  d => d.Distance,      4.8, "m"   ),
        };

        private readonly Dictionary<string, DateTime> _lastAlarm = new();
        private const double CooldownSeconds = 5.0;

        public event EventHandler<AlarmRecord>? AlarmTriggered;

        public void Check(SensorData data)     => CheckRoom(data, "");
        public void CheckRoom(SensorData data, string room)
        {
            foreach (var rule in _rules)
            {
                double val = rule.Get(data);
                if (val < rule.DangerHi) continue;  // 이상(>=)일 때 기록

                string key = $"{room}:{rule.Name}";
                if (_lastAlarm.TryGetValue(key, out var last) &&
                    (DateTime.Now - last).TotalSeconds < CooldownSeconds)
                    continue;

                _lastAlarm[key] = DateTime.Now;

                double displayVal = val;
                double displayThr = rule.DangerHi;

                var record = new AlarmRecord
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Room      = string.IsNullOrEmpty(room) ? "-" : room,
                    Sensor    = rule.Name,
                    Value     = Math.Round(displayVal, 3),
                    Unit      = rule.Unit,
                    Threshold = displayThr
                };

                try { DatabaseHelper.SaveAlarm(record.Sensor, record.Value, record.Unit, record.Threshold, record.Room); }
                catch { }

                AlarmTriggered?.Invoke(this, record);
            }
        }
    }
}

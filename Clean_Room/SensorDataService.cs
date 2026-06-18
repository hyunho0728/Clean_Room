using System;
using System.Windows.Threading;

namespace Clean_Room
{
    /// <summary>현재 센서 측정값 스냅샷</summary>
    public class SensorData
    {
        public double Temperature      { get; set; } = 24.0;  // °C
        public double Humidity         { get; set; } = 35.0;  // %RH
        public double Pressure         { get; set; } = 22.0;  // Pa (클린룸 양압 차압, ≥20 Pa)
        public double Vibration        { get; set; } = 1.6;   // m/s² ※임시: 엔지니어 트리거 테스트용 (원래 0.3)
        public double Distance         { get; set; } = 0.0;   // m
        public double ParticleCount    { get; set; } = 2800.0;// 개/m³ (≥0.5μm)
        /// <summary>에어샤워 블로어 출력 압력 (Pa). 실제 장비에서 ADS로 전달. 0 = 미가동.</summary>
        public double AirShowerPressure { get; set; } = 0.0;  // Pa
    }

    public class SensorDataService
    {
        private readonly DispatcherTimer _timer;
        private readonly Random          _rng = new Random();

        public SensorData Current { get; private set; } = new SensorData();
        public event EventHandler<SensorData>? DataUpdated;

        public SensorDataService(TimeSpan interval)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, __) => Tick();
        }

        public void Start() => _timer.Start();
        public void Stop()  => _timer.Stop();

        public void UpdateFromExternal(double temp, double humidity, double pressure,
                                       double vibration = 0, double distance = 0,
                                       double airShowerPressure = 0)
        {
            Current = new SensorData
            {
                Temperature       = temp,
                Humidity          = humidity,
                Pressure          = pressure,
                Vibration         = vibration,
                Distance          = distance,
                AirShowerPressure = airShowerPressure,
            };
            DataUpdated?.Invoke(this, Current);
        }

        private void Tick()
        {
            double Drift(double v, double lo, double hi, double step)
                => Math.Round(Math.Max(lo, Math.Min(hi, v + (_rng.NextDouble() - 0.5) * step)), 2);

            // 입자수: ISO 5 목표(≤3,520개/m³) 부근에서 드리프트, 간헐적으로 ISO 6 초과 가능
            double newParticle = Math.Round(
                Math.Max(500, Math.Min(50_000,
                    Current.ParticleCount + (_rng.NextDouble() - 0.44) * 400)), 0);

            Current = new SensorData
            {
                Temperature       = Drift(Current.Temperature,   0.0, 40.0, 0.4),
                Humidity          = Drift(Current.Humidity,      0.0, 50.0, 1.0),
                // 양압 차압: ISO 5 엄격 기준 ≥20 Pa. 18~28 Pa 범위에서 드리프트
                Pressure          = Drift(Current.Pressure,     18.0, 28.0, 0.8),
                Vibration         = Drift(Current.Vibration,     1.4,  2.0, 0.2), // ※임시 (원래 0.0~2.0, step 0.05)
                Distance          = Drift(Current.Distance,      0.0,  5.0, 0.1),
                ParticleCount     = newParticle,
                AirShowerPressure = Current.AirShowerPressure, // 틱 간 유지 (시뮬레이션 수동 설정값 보존)
            };
            DataUpdated?.Invoke(this, Current);
        }
    }
}

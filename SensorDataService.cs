using System;
using System.Windows.Threading;

namespace Clean_Room
{
    /// <summary>현재 센서 측정값 스냅샷</summary>
    public class SensorData
    {
        public double Temperature { get; set; } = 25.6;   // °C
        public double Humidity    { get; set; } = 38.0;   // %RH
        public double Pressure    { get; set; } = 100.0;  // psi
    }

    /// <summary>
    /// 센서 데이터 공급 서비스.
    /// 실제 센서 연결 시 Tick() 내부를 교체하거나
    /// UpdateFromExternal()로 외부에서 직접 값을 밀어넣으세요.
    /// </summary>
    public class SensorDataService
    {
        private readonly DispatcherTimer _timer;
        private readonly Random          _rng = new Random(42);

        public SensorData Current { get; private set; } = new SensorData();

        /// <summary>새 측정값이 도착할 때마다 UI 스레드에서 발생</summary>
        public event EventHandler<SensorData>? DataUpdated;

        public SensorDataService(TimeSpan interval)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, __) => Tick();
        }

        public void Start() => _timer.Start();
        public void Stop()  => _timer.Stop();

        /// <summary>외부 센서 드라이버에서 직접 값을 넣을 때 사용 (UI 스레드 필요)</summary>
        public void UpdateFromExternal(double temp, double humidity, double pressure)
        {
            Current = new SensorData
            {
                Temperature = temp,
                Humidity    = humidity,
                Pressure    = pressure
            };
            DataUpdated?.Invoke(this, Current);
        }

        // ── 테스트용: 랜덤 워크 ──────────────────────────────────
        private void Tick()
        {
            double Drift(double v, double lo, double hi, double step)
                => Math.Round(Math.Max(lo, Math.Min(hi, v + (_rng.NextDouble() - 0.5) * step)), 1);

            Current = new SensorData
            {
                Temperature = Drift(Current.Temperature, 20.0, 30.0, 0.4),
                Humidity    = Drift(Current.Humidity,    25.0, 55.0, 1.0),
                Pressure    = Drift(Current.Pressure,     0.0,130.0, 4.0)
            };
            DataUpdated?.Invoke(this, Current);
        }
    }
}

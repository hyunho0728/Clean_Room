using System;
using System.Collections.Generic;
using System.Windows;
using TwinCAT.Ads;

namespace Clean_Room
{
    /// <summary>
    /// TwinCAT ADS를 통해 PLC 센서값을 읽어 SensorDataService에 전달합니다.
    /// System.Timers.Timer 사용 — 백그라운드 스레드에서 안전하게 동작.
    /// 선택적 변수(CR2 전용, 에어샤워)는 PLC에 없어도 연결이 끊기지 않음.
    /// </summary>
    public class AdsDataService : IDisposable
    {
        private AdsClient?           _client;
        private System.Timers.Timer? _timer;
        private SensorDataService?   _sensorSvc1;
        private SensorDataService?   _sensorSvc2;
        private SensorDataService?   _sensorSvc3;

        // CR1 / CR3 공용 (필수 — 없으면 Connect 전체 실패)
        private uint _hTemp, _hHum, _hPres, _hVib, _hDist;

        // CR2 전용 (선택 — 없으면 CR1 핸들 재사용)
        private uint _hTemp2, _hHum2, _hPres2, _hVib2, _hDist2;
        private bool _cr2OwnHandles = false;

        // 에어샤워 압력 (선택 — 없으면 0.0 반환)
        private uint _hAirShower1, _hAirShower2, _hAirShower3;

        public bool IsConnected => _client?.IsConnected == true;
        public event Action<string>? StatusChanged;

        // ── 아날로그 → 공학단위 변환 (raw 0~10 기준) ─────────────
        // 센서 범위가 다르면 이 값만 수정
        private const float TempScale  = 5.0f;   // 0~10V → 0~50°C
        private const float HumScale   = 10.0f;  // 0~10V → 0~100%RH
        private const float PresScale  = 1.0f;   // 이미 MPa
        private const float VibScale   = 1.0f;   // 이미 m/s²
        private const float DistScale  = 1.0f;   // 이미 m

        // ── 헬퍼: 실패해도 0 반환 ─────────────────────────────────
        private uint TryCreate(string symbol)
        {
            try   { return _client!.CreateVariableHandle(symbol); }
            catch { return 0; }
        }

        private float TryRead(uint handle)
        {
            if (handle == 0) return 0f;
            return (float)_client!.ReadAny(handle, typeof(float));
        }

        // ── 연결 ─────────────────────────────────────────────────
        public void Connect(SensorDataService svc1,
                            SensorDataService? svc2 = null,
                            SensorDataService? svc3 = null,
                            string amsNetId = "127.0.0.1.1.1",
                            int port = 851)
        {
            _sensorSvc1 = svc1;
            _sensorSvc2 = svc2;
            _sensorSvc3 = svc3;

            _client = new AdsClient();
            _client.Connect(new AmsNetId(amsNetId), port);

            // ── 필수 변수 (없으면 예외 → 연결 실패) ──────────────
            _hTemp = _client.CreateVariableHandle("GVL.fTemperature");
            _hHum  = _client.CreateVariableHandle("GVL.fHumidity");
            _hPres = _client.CreateVariableHandle("GVL.fPressure");
            _hVib  = _client.CreateVariableHandle("GVL.fVibration");
            _hDist = _client.CreateVariableHandle("GVL.fDistance");

            // ── CR2 전용 (선택) ────────────────────────────────────
            _hTemp2 = TryCreate("GVL.fTemperature2");
            _hHum2  = TryCreate("GVL.fHumidity2");
            _hPres2 = TryCreate("GVL.fPressure2");
            _hVib2  = TryCreate("GVL.fVibration2");
            _hDist2 = TryCreate("GVL.fDistance2");
            _cr2OwnHandles = _hTemp2 != 0;   // 하나라도 있으면 독립 모드

            // ── 에어샤워 압력 (선택) ───────────────────────────────
            _hAirShower1 = TryCreate("GVL.fAirShowerPressure1");
            _hAirShower2 = TryCreate("GVL.fAirShowerPressure2");
            _hAirShower3 = TryCreate("GVL.fAirShowerPressure3");

            // ADS 연결 성공 → 랜덤 시뮬레이션 중단
            Application.Current.Dispatcher.Invoke(() =>
            {
                _sensorSvc1?.Stop();
                _sensorSvc1?.UpdateFromExternal(24.0, 35.0, 0.0, 0.0, 0.0);
                _sensorSvc2?.Stop();
                _sensorSvc2?.UpdateFromExternal(24.0, 35.0, 0.0, 0.0, 0.0);
                _sensorSvc3?.Stop();
                _sensorSvc3?.UpdateFromExternal(24.0, 35.0, 0.0, 0.0, 0.0);
            });

            _timer = new System.Timers.Timer(100) { AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();

            StatusChanged?.Invoke("ADS 연결됨 ✔");
        }

        // ── 읽기 루프 ─────────────────────────────────────────────
        private void OnTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (_client == null || !_client.IsConnected) return;

            try
            {
                // CR1 / CR3 공용 — raw 읽기 후 공학단위 변환
                float temp = (float)_client.ReadAny(_hTemp, typeof(float)) * TempScale;
                float hum  = (float)_client.ReadAny(_hHum,  typeof(float)) * HumScale;
                float pres = (float)_client.ReadAny(_hPres, typeof(float)) * PresScale;
                float vib  = (float)_client.ReadAny(_hVib,  typeof(float)) * VibScale;
                float dist = (float)_client.ReadAny(_hDist, typeof(float)) * DistScale;

                // CR2 전용 (없으면 CR1 값 공유)
                float temp2 = (_cr2OwnHandles ? TryRead(_hTemp2) : (float)_client.ReadAny(_hTemp, typeof(float))) * TempScale;
                float hum2  = (_cr2OwnHandles ? TryRead(_hHum2)  : (float)_client.ReadAny(_hHum,  typeof(float))) * HumScale;
                float pres2 = (_cr2OwnHandles ? TryRead(_hPres2) : (float)_client.ReadAny(_hPres, typeof(float))) * PresScale;
                float vib2  = (_cr2OwnHandles ? TryRead(_hVib2)  : (float)_client.ReadAny(_hVib,  typeof(float))) * VibScale;
                float dist2 = (_cr2OwnHandles ? TryRead(_hDist2) : (float)_client.ReadAny(_hDist, typeof(float))) * DistScale;

                // 에어샤워 (없으면 0.0)
                float airSh1 = TryRead(_hAirShower1);
                float airSh2 = TryRead(_hAirShower2);
                float airSh3 = TryRead(_hAirShower3);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    _sensorSvc1?.UpdateFromExternal(temp,  hum,  pres,  vib,  dist,  airSh1);
                    _sensorSvc2?.UpdateFromExternal(temp2, hum2, pres2, vib2, dist2, airSh2);
                    _sensorSvc3?.UpdateFromExternal(temp,  hum,  pres,  vib,  dist,  airSh3);
                });
            }
            catch (Exception ex)
            {
                _timer?.Stop();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _sensorSvc1?.Start();
                    _sensorSvc3?.Start();
                    StatusChanged?.Invoke($"ADS 오류 (랜덤 폴백): {ex.Message}");
                });
            }
        }

        // ── 해제 ─────────────────────────────────────────────────
        public void Disconnect()
        {
            _timer?.Stop();
            _timer?.Dispose();

            if (_client != null && _client.IsConnected)
            {
                var seen = new HashSet<uint>();
                void Del(uint h) { if (h != 0 && seen.Add(h)) _client.DeleteVariableHandle(h); }

                Del(_hTemp); Del(_hHum); Del(_hPres); Del(_hVib); Del(_hDist);
                if (_cr2OwnHandles)
                { Del(_hTemp2); Del(_hHum2); Del(_hPres2); Del(_hVib2); Del(_hDist2); }
                Del(_hAirShower1); Del(_hAirShower2); Del(_hAirShower3);

                _client.Disconnect();
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                _sensorSvc1?.Start();
                _sensorSvc2?.Start();
                _sensorSvc3?.Start();
            });
            StatusChanged?.Invoke("ADS 연결 해제 (랜덤 폴백)");
        }

        public void Dispose()
        {
            Disconnect();
            _client?.Dispose();
        }
    }
}

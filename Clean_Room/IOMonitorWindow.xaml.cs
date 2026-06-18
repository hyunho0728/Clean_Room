using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TwinCAT.Ads;

namespace Clean_Room
{
    public partial class IOMonitorWindow : Window
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ST_DigitalInput_RAW  { public ushort Bits; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ST_DigitalOutput_RAW { public ushort Bits; }

        // ── 기존 I/O 필드 ─────────────────────────────────────────
        private bool _button1 = false;
        private bool _lamp1   = false;

        private DispatcherTimer? _timer;
        private AdsClient        _adsClient;
        private uint _hInput = 0, _hOutput = 0;

        // ── 관리자 인증 필드 ──────────────────────────────────────
        private readonly bool  _isAuthMode;
        private readonly User? _adminUser;

        private int  _input1Count = 0;      // Input1 누른 횟수
        private bool _prevBtn1    = false;  // 직전 상태 (엣지 감지용)
        private bool _prevBtn2    = false;

        private DispatcherTimer? _authTimeoutTimer;
        private int  _timeoutRemain = 150;  // 15초 (100ms 단위 × 150)

        // ── 목표 시퀀스 ───────────────────────────────────────────
        private const int RequiredInput1 = 3;

        // ─────────────────────────────────────────────────────────
        // 기본 생성자 (일반 I/O 모니터 모드)
        public IOMonitorWindow()
        {
            InitializeComponent();
            _adsClient = new AdsClient();
        }

        // 관리자 인증 모드 생성자
        public IOMonitorWindow(User adminUser)
        {
            _isAuthMode = true;
            _adminUser  = adminUser;
            InitializeComponent();
            _adsClient = new AdsClient();

            // 인증 패널 표시, 연결/해제 버튼 숨김
            authPanel.Visibility = Visibility.Visible;
            btnPanel.Visibility  = Visibility.Collapsed;

            // 자동 연결 시도
            AutoConnect();
        }

        // ── 자동 연결 (인증 모드 전용) ────────────────────────────
        private void AutoConnect()
        {
            try
            {
                _adsClient.Connect(AmsNetId.Local, 851);
                _hInput  = _adsClient.CreateVariableHandle("GVL.NX_ID5342");
                _hOutput = _adsClient.CreateVariableHandle("GVL.NX_OD5121");

                txtStatus.Text       = "연결됨 ✔";
                txtStatus.Foreground = Brushes.LimeGreen;
                txtAuthStatus.Text   = "버튼 시퀀스를 입력하세요";
                txtAuthStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22,0xD3,0xEE));

                // 폴링 타이머
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _timer.Tick += Timer_Tick;
                _timer.Start();

                // 15초 타임아웃 타이머
                _authTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _authTimeoutTimer.Tick += AuthTimeout_Tick;
                _authTimeoutTimer.Start();
            }
            catch (Exception ex)
            {
                txtAuthStatus.Text       = $"장비 연결 실패: {ex.Message}";
                txtAuthStatus.Foreground = Brushes.OrangeRed;
                // 2초 후 LoginWindow로 복귀
                var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                delay.Tick += (_, __) => { delay.Stop(); AuthFail("장비 연결 불가"); };
                delay.Start();
            }
        }

        // ── 타임아웃 카운트다운 ───────────────────────────────────
        private void AuthTimeout_Tick(object? sender, EventArgs e)
        {
            _timeoutRemain--;
            timeoutBar.Value = _timeoutRemain / 1.5;  // 0~100 범위로 스케일

            if (_timeoutRemain <= 0)
                AuthFail("시간 초과");
        }

        // ── 폴링 타이머 ───────────────────────────────────────────
        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_adsClient?.IsConnected == true)
                ReadAndWritePLC();
        }

        private void ReadAndWritePLC()
        {
            try
            {
                var rawIn = (ST_DigitalInput_RAW)_adsClient.ReadAny(_hInput, typeof(ST_DigitalInput_RAW));

                bool btn1 = (rawIn.Bits & (1 << 0)) != 0;
                bool btn2 = (rawIn.Bits & (1 << 1)) != 0;

                // ── 인증 모드: 엣지(누르는 순간)만 감지 ──
                if (_isAuthMode)
                {
                    if (btn1 && !_prevBtn1) OnInput1Pressed();
                    if (btn2 && !_prevBtn2) OnInput2Pressed();
                }

                _prevBtn1 = btn1;
                _prevBtn2 = btn2;

                // 기존 UI 업데이트
                _button1 = btn1;
                _lamp1   = _button1;
                UpdateUI();

                // Output 쓰기
                var rawOut = new ST_DigitalOutput_RAW();
                if (_lamp1) rawOut.Bits |= (1 << 0);
                _adsClient.WriteAny(_hOutput, rawOut);
            }
            catch (Exception ex)
            {
                StopMonitoring();
                txtStatus.Text = $"오류: {ex.Message}";
                if (_isAuthMode) AuthFail("통신 오류");
            }
        }

        // ── 인증 시퀀스 처리 ──────────────────────────────────────
        private void OnInput1Pressed()
        {
            if (_input1Count >= RequiredInput1)
            {
                // Input1을 이미 3번 눌렀는데 또 누름 → 실패
                AuthFail("잘못된 입력");
                return;
            }

            _input1Count++;
            UpdateSeqDots();
        }

        private void OnInput2Pressed()
        {
            if (_input1Count == RequiredInput1)
            {
                // 정확히 Input1 × 3 후 Input2 × 1 → 성공
                AuthSuccess();
            }
            else
            {
                // 순서 틀림 → 실패
                AuthFail("잘못된 순서");
            }
        }

        // ── 시퀀스 표시 업데이트 ─────────────────────────────────
        private void UpdateSeqDots()
        {
            var on  = new SolidColorBrush(Color.FromRgb(0x22,0xD3,0xEE));
            var off = new SolidColorBrush(Color.FromRgb(0x37,0x41,0x51));

            seqDot1.Fill = _input1Count >= 1 ? on : off;
            seqDot2.Fill = _input1Count >= 2 ? on : off;
            seqDot3.Fill = _input1Count >= 3 ? on : off;
        }

        // ── 인증 성공 ─────────────────────────────────────────────
        private void AuthSuccess()
        {
            StopAuthTimers();

            seqDot4.Fill = new SolidColorBrush(Color.FromRgb(0x22,0xC5,0x5E));
            txtAuthStatus.Text       = "✔ 관리자로 로그인하였습니다";
            txtAuthStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22,0xC5,0x5E));

            // 잠깐 메시지 보여준 후 AdminWindow 오픈
            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            delay.Tick += (_, __) =>
            {
                delay.Stop();
                new AdminWindow(_adminUser!).Show();
                this.Close();
            };
            delay.Start();
        }

        // ── 인증 실패 ─────────────────────────────────────────────
        private void AuthFail(string reason)
        {
            StopAuthTimers();

            txtAuthStatus.Text       = $"✗ 인증 실패 ({reason})";
            txtAuthStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF,0x44,0x44));

            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            delay.Tick += (_, __) =>
            {
                delay.Stop();
                new LoginWindow().Show();
                this.Close();
            };
            delay.Start();
        }

        private void StopAuthTimers()
        {
            _timer?.Stop();
            _authTimeoutTimer?.Stop();
        }

        // ── 기존 UI ───────────────────────────────────────────────
        private void UpdateUI()
        {
            ellInput.Fill  = _button1 ? Brushes.LimeGreen : Brushes.Gray;
            txtInput.Text  = _button1 ? "ON" : "OFF";
            ellOutput.Fill = _lamp1   ? Brushes.Red       : Brushes.Gray;
            txtOutput.Text = _lamp1   ? "ON" : "OFF";
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_adsClient.IsConnected) return;
                _adsClient.Connect(AmsNetId.Local, 851);
                _hInput  = _adsClient.CreateVariableHandle("GVL.NX_ID5342");
                _hOutput = _adsClient.CreateVariableHandle("GVL.NX_OD5121");

                txtStatus.Text       = "연결됨 ✔";
                txtStatus.Foreground = Brushes.LimeGreen;

                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _timer.Tick += Timer_Tick;
                _timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"연결 실패: {ex.Message}");
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            StopMonitoring();
            txtStatus.Text       = "연결 안됨";
            txtStatus.Foreground = Brushes.Red;
        }

        private void StopMonitoring()
        {
            _timer?.Stop();
            if (_adsClient?.IsConnected == true)
            {
                if (_hInput  != 0) _adsClient.DeleteVariableHandle(_hInput);
                if (_hOutput != 0) _adsClient.DeleteVariableHandle(_hOutput);
                _adsClient.Disconnect();
            }
            _hInput = _hOutput = 0;
        }

        protected override void OnClosed(EventArgs e)
        {
            StopMonitoring();
            _authTimeoutTimer?.Stop();
            _adsClient?.Dispose();
            base.OnClosed(e);
        }
    }
}

using System;
using System.Net.Sockets;
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
        private struct ST_DigitalInput_RAW
        {
            public ushort Bits;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ST_DigitalOutput_RAW
        {
            public ushort Bits;
        }

        private bool _button1 = false;
        private bool _lamp1 = false;

        private DispatcherTimer _timer;
        private AdsClient _adsClient;
        private uint _hInput = 0;
        private uint _hOutput = 0;

        public IOMonitorWindow()
        {
            InitializeComponent();
            _adsClient = new AdsClient();
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_adsClient.IsConnected) return;

                _adsClient.Connect(AmsNetId.Local, 851);
                _hInput = _adsClient.CreateVariableHandle("GVL.NX_ID5342");
                _hOutput = _adsClient.CreateVariableHandle("GVL.NX_OD5121");

                txtStatus.Text = "연결됨 ✔";
                txtStatus.Foreground = Brushes.LimeGreen;

                _timer = new DispatcherTimer();
                _timer.Interval = TimeSpan.FromMilliseconds(100);
                _timer.Tick += Timer_Tick;
                _timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"연결 실패: {ex.Message}");
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_adsClient != null && _adsClient.IsConnected)
            {
                ReadAndWritePLC();
            }
        }

        private void ReadAndWritePLC()
        {
            try
            {
                ST_DigitalInput_RAW rawIn = (ST_DigitalInput_RAW)_adsClient.ReadAny(_hInput, typeof(ST_DigitalInput_RAW));
                _button1 = (rawIn.Bits & (1 << 0)) != 0;
                _lamp1 = _button1;

                UpdateUI();

                ST_DigitalOutput_RAW rawOut = new ST_DigitalOutput_RAW();
                rawOut.Bits = 0;
                if (_lamp1) rawOut.Bits |= (1 << 0);

                _adsClient.WriteAny(_hOutput, rawOut);
            }
            catch (Exception ex)
            {
                StopMonitoring();
                txtStatus.Text = $"오류: {ex.Message}";
            }
        }

        private void UpdateUI()
        {
            ellInput.Fill = _button1 ? Brushes.LimeGreen : Brushes.Gray;
            txtInput.Text = _button1 ? "ON" : "OFF";
            ellOutput.Fill = _lamp1 ? Brushes.Red : Brushes.Gray;
            txtOutput.Text = _lamp1 ? "ON" : "OFF";
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            StopMonitoring();
            txtStatus.Text = "연결 안됨";
            txtStatus.Foreground = Brushes.Red;
        }

        private void StopMonitoring()
        {
            _timer?.Stop();
            if (_adsClient != null && _adsClient.IsConnected)
            {
                if (_hInput != 0) _adsClient.DeleteVariableHandle(_hInput);
                if (_hOutput != 0) _adsClient.DeleteVariableHandle(_hOutput);
                _adsClient.Disconnect();
            }
            _hInput = 0;
            _hOutput = 0;
        }

        protected override void OnClosed(EventArgs e)
        {
            StopMonitoring();
            _adsClient?.Dispose();
            base.OnClosed(e);
        }
    }
}
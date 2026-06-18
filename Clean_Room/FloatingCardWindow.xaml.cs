using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Clean_Room
{
    public partial class FloatingCardWindow : Window
    {
        // 아크 좌표 (Canvas 180x140 기준)
        private const double CX = 90, CY = 72, R = 55;
        private const double ArcStart = 135, ArcSweep = 270;

        private readonly double _min, _max, _warnHi, _dangerHi;
        private readonly Color  _accent;
        private readonly int    _sensorIdx;

        private readonly Path      _fgArc;
        private readonly TextBlock _valueText;

        public FloatingCardWindow(int cardIdx, SensorDataService service, int room)
        {
            InitializeComponent();

            var cfg     = SensorDashboard.Cards[cardIdx];
            _min        = cfg.Min;
            _max        = cfg.Max;
            _warnHi     = cfg.WarnHi;
            _dangerHi   = cfg.DangerHi;
            _accent     = cfg.Accent;
            _sensorIdx  = cardIdx;

            txtTitle.Text = $"R{room}  {cfg.Icon} {cfg.Name}";

            // 배경 아크
            arcCanvas.Children.Add(new Path
            {
                Stroke             = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x5F)),
                StrokeThickness    = 8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                Data               = MakeArc(ArcStart, ArcStart + ArcSweep)
            });

            // 값 아크
            _fgArc = new Path
            {
                Stroke             = new SolidColorBrush(cfg.Accent),
                StrokeThickness    = 8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                Data               = MakeArc(ArcStart, ArcStart + 0.5)
            };
            arcCanvas.Children.Add(_fgArc);

            // 중앙 텍스트 오버레이
            _valueText = new TextBlock
            {
                Text                = "---",
                Foreground          = Brushes.White,
                FontSize            = 24,
                FontWeight          = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var unitText = new TextBlock
            {
                Text                = cfg.Unit,
                Foreground          = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                FontSize            = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 2, 0, 0)
            };
            var center = new StackPanel();
            center.Children.Add(_valueText);
            center.Children.Add(unitText);

            var centerBorder = new Border { Width = 90, Height = 52, Child = center };
            Canvas.SetLeft(centerBorder, CX - 45);
            Canvas.SetTop(centerBorder,  CY - 24);
            arcCanvas.Children.Add(centerBorder);

            // 테두리 강조색 (악센트)
            var accentBrush = new SolidColorBrush(
                Color.FromArgb(0x55, cfg.Accent.R, cfg.Accent.G, cfg.Accent.B));
            ((Border)Content).BorderBrush = accentBrush;

            // 초기 표시
            UpdateGauge(GetValue(service.Current));

            // 이벤트 구독
            service.DataUpdated += (_, data) =>
                Dispatcher.Invoke(() => UpdateGauge(GetValue(data)));

            Closed += (_, __) =>
                service.DataUpdated -= (_, data) =>
                    Dispatcher.Invoke(() => UpdateGauge(GetValue(data)));
        }

        private double GetValue(SensorData d) => _sensorIdx switch
        {
            0 => d.Temperature,
            1 => d.Humidity,
            2 => d.Pressure,
            3 => d.Vibration,
            4 => d.Distance,
            _ => 0
        };

        private void UpdateGauge(double value)
        {
            double pct    = Math.Max(0, Math.Min(1, (value - _min) / (_max - _min)));
            bool   warn   = value > _warnHi;
            bool   danger = value > _dangerHi;

            _fgArc.Data   = MakeArc(ArcStart, ArcStart + pct * ArcSweep);
            _fgArc.Stroke = danger
                ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
                : warn
                    ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
                    : new SolidColorBrush(_accent);

            _valueText.Text  = value.ToString("F1");
            statusDot.Text   = danger ? "🔴 위험" : warn ? "🟡 주의" : "● 정상";
            statusDot.Foreground = danger
                ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
                : warn
                    ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
                    : new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        }

        private static PathGeometry MakeArc(double startDeg, double endDeg)
        {
            if (endDeg - startDeg < 0.5) endDeg = startDeg + 0.5;
            double s = startDeg * Math.PI / 180.0;
            double e = endDeg   * Math.PI / 180.0;
            var start   = new Point(CX + R * Math.Cos(s), CY + R * Math.Sin(s));
            var end     = new Point(CX + R * Math.Cos(e), CY + R * Math.Sin(e));
            bool isLarge = (endDeg - startDeg) > 180;
            var fig = new PathFigure { StartPoint = start, IsFilled = false };
            fig.Segments.Add(new ArcSegment(end, new Size(R, R), 0,
                                             isLarge, SweepDirection.Clockwise, true));
            return new PathGeometry(new[] { fig });
        }

        private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}

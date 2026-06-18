using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Clean_Room
{
    public partial class SensorGraphWindow : Window
    {
        private const int MaxPoints = 60;

        // Room 1 버퍼
        private readonly Queue<double> _r1Temp  = new();
        private readonly Queue<double> _r1Hum   = new();
        private readonly Queue<double> _r1Pres  = new();
        private readonly Queue<double> _r1Vib   = new();

        // Room 2 버퍼
        private readonly Queue<double> _r2Temp  = new();
        private readonly Queue<double> _r2Hum   = new();
        private readonly Queue<double> _r2Pres  = new();
        private readonly Queue<double> _r2Vib   = new();

        private readonly SensorDataService _service1;
        private readonly SensorDataService _service2;
        private readonly DispatcherTimer   _drawTimer;

        // (차트 범위 설정)
        private static readonly (double min, double max, string unit, Color color)[] _cfg =
        {
            (0, 50,  "°C",   Color.FromRgb(0x22, 0xD3, 0xEE)),  // 온도
            (0, 100, "%RH",  Color.FromRgb(0x34, 0xD3, 0x99)),  // 습도
            (0,  1,  "MPa",  Color.FromRgb(0xA7, 0x8B, 0xFA)),  // 압력
            (0,  2,  "m/s²", Color.FromRgb(0xFB, 0xBF, 0x24)),  // 진동
        };

        public SensorGraphWindow(SensorDataService service1, SensorDataService service2, int roomFilter = 0)
        {
            InitializeComponent();

            _service1 = service1;
            _service2 = service2;

            // roomFilter: 0=전체, 1=R1만, 2=R2만
            if (roomFilter == 1)
            {
                tabMain.Items.Remove(tabRoom2);
                Title = "클린룸 1 실시간 그래프";
                Width = 740;
            }
            else if (roomFilter == 2)
            {
                tabMain.Items.Remove(tabRoom1);
                Title = "클린룸 2 실시간 그래프";
                Width = 740;
            }

            // 초기 버퍼: 현재값으로 채움 (그래프가 처음부터 비어 보이지 않도록)
            FillInitial(_r1Temp, service1.Current.Temperature);
            FillInitial(_r1Hum,  service1.Current.Humidity);
            FillInitial(_r1Pres, service1.Current.Pressure);
            FillInitial(_r1Vib,  service1.Current.Vibration);
            FillInitial(_r2Temp, service2.Current.Temperature);
            FillInitial(_r2Hum,  service2.Current.Humidity);
            FillInitial(_r2Pres, service2.Current.Pressure);
            FillInitial(_r2Vib,  service2.Current.Vibration);

            // 데이터 수신: 해당 룸만 구독
            if (roomFilter != 2) service1.DataUpdated += OnData1;
            if (roomFilter != 1) service2.DataUpdated += OnData2;

            // 1초마다 다시 그리기
            _drawTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _drawTimer.Tick += (_, __) => RedrawAll();
            _drawTimer.Start();

            // 창 닫힐 때 이벤트 해제
            Closed += (_, __) =>
            {
                _drawTimer.Stop();
                if (roomFilter != 2) _service1.DataUpdated -= OnData1;
                if (roomFilter != 1) _service2.DataUpdated -= OnData2;
            };

            // 첫 렌더링
            Loaded += (_, __) => RedrawAll();
        }

        private static void FillInitial(Queue<double> q, double val)
        {
            for (int i = 0; i < MaxPoints; i++) q.Enqueue(val);
        }

        private static void Enqueue(Queue<double> q, double val)
        {
            if (q.Count >= MaxPoints) q.Dequeue();
            q.Enqueue(val);
        }

        private void OnData1(object? sender, SensorData data)
        {
            Enqueue(_r1Temp, data.Temperature);
            Enqueue(_r1Hum,  data.Humidity);
            Enqueue(_r1Pres, data.Pressure);
            Enqueue(_r1Vib,  data.Vibration);
        }

        private void OnData2(object? sender, SensorData data)
        {
            Enqueue(_r2Temp, data.Temperature);
            Enqueue(_r2Hum,  data.Humidity);
            Enqueue(_r2Pres, data.Pressure);
            Enqueue(_r2Vib,  data.Vibration);
        }

        private void RedrawAll()
        {
            DrawChart(chartR1Temp, _r1Temp, _cfg[0]);
            DrawChart(chartR1Hum,  _r1Hum,  _cfg[1]);
            DrawChart(chartR1Pres, _r1Pres, _cfg[2]);
            DrawChart(chartR1Vib,  _r1Vib,  _cfg[3]);
            DrawChart(chartR2Temp, _r2Temp, _cfg[0]);
            DrawChart(chartR2Hum,  _r2Hum,  _cfg[1]);
            DrawChart(chartR2Pres, _r2Pres, _cfg[2]);
            DrawChart(chartR2Vib,  _r2Vib,  _cfg[3]);
        }

        private static void DrawChart(Canvas canvas, Queue<double> data,
            (double min, double max, string unit, Color color) cfg)
        {
            canvas.Children.Clear();
            if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;

            const double padL = 44, padR = 8, padT = 8, padB = 20;
            double w = canvas.ActualWidth  - padL - padR;
            double h = canvas.ActualHeight - padT - padB;
            if (w <= 0 || h <= 0) return;

            var arr = data.ToArray();
            int n   = arr.Length;
            if (n < 2) return;

            // ── 동적 Y축: 실제 데이터 범위 ± 여유 10% ──
            double dataMin = double.MaxValue, dataMax = double.MinValue;
            foreach (var v in arr) { if (v < dataMin) dataMin = v; if (v > dataMax) dataMax = v; }
            double span = dataMax - dataMin;
            // 변동폭이 너무 작으면 최소 span 보장 (온도 5°C, 습도 5%, 압력 0.05, 진동 0.5)
            double minSpan = cfg.unit == "MPa"  ? 0.05 :
                             cfg.unit == "m/s²" ? 0.5  :
                             cfg.unit == "%RH"  ? 5.0  : 5.0;
            if (span < minSpan) { span = minSpan; }
            double pad10  = span * 0.15;
            double rangeMin = Math.Max(cfg.min, dataMin - pad10);
            double rangeMax = Math.Min(cfg.max, dataMax + pad10);
            // 범위가 여전히 너무 좁으면 중심 기준으로 확장
            if (rangeMax - rangeMin < minSpan)
            {
                double mid = (rangeMin + rangeMax) / 2.0;
                rangeMin = Math.Max(cfg.min, mid - minSpan / 2.0);
                rangeMax = Math.Min(cfg.max, mid + minSpan / 2.0);
            }

            // ── Y축 격자선 (4개) + 레이블 ──
            int  gridCount = 4;
            var  gridBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            var  labelBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

            for (int g = 0; g <= gridCount; g++)
            {
                double ratio = (double)g / gridCount;
                double py    = padT + h * (1.0 - ratio);
                double val   = rangeMin + (rangeMax - rangeMin) * ratio;

                // 격자선
                var line = new Line
                {
                    X1 = padL, Y1 = py, X2 = padL + w, Y2 = py,
                    Stroke = gridBrush, StrokeThickness = 1
                };
                canvas.Children.Add(line);

                // Y축 레이블
                var tb = new TextBlock
                {
                    Text       = val.ToString(cfg.unit == "MPa" ? "F2" : "F0"),
                    Foreground = labelBrush,
                    FontSize   = 9
                };
                Canvas.SetLeft(tb, 2);
                Canvas.SetTop(tb,  py - 7);
                canvas.Children.Add(tb);
            }

            // ── 단위 레이블 (우측 상단) ──
            var unitTb = new TextBlock
            {
                Text       = cfg.unit,
                Foreground = new SolidColorBrush(cfg.color),
                FontSize   = 9,
                Opacity    = 0.7
            };
            Canvas.SetRight(unitTb, padR + 2);
            Canvas.SetTop(unitTb,   padT);
            canvas.Children.Add(unitTb);

            // ── X축 타임 레이블 (60초 / 30초 / 0초) ──
            var timeBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            foreach (var (sec, label) in new[] { (0, "-60s"), (30, "-30s"), (59, "now") })
            {
                double px = padL + sec * w / (MaxPoints - 1);
                var tl = new TextBlock { Text = label, Foreground = timeBrush, FontSize = 9 };
                Canvas.SetLeft(tl, px - 12);
                Canvas.SetTop(tl,  padT + h + 4);
                canvas.Children.Add(tl);
            }

            // ── 데이터 폴리라인 ──
            var pts   = new PointCollection();
            double step = w / (MaxPoints - 1);

            for (int i = 0; i < n; i++)
            {
                double px = padL + i * step;
                double ratio = (arr[i] - rangeMin) / (rangeMax - rangeMin);
                ratio = Math.Max(0, Math.Min(1, ratio));
                double py = padT + h * (1.0 - ratio);
                pts.Add(new Point(px, py));
            }

            // 라인 그림자 (두꺼운 반투명)
            var shadow = new Polyline
            {
                Points          = pts,
                Stroke          = new SolidColorBrush(Color.FromArgb(0x33, cfg.color.R, cfg.color.G, cfg.color.B)),
                StrokeThickness = 4,
                StrokeLineJoin  = PenLineJoin.Round
            };
            canvas.Children.Add(shadow);

            // 메인 라인
            var poly = new Polyline
            {
                Points          = pts,
                Stroke          = new SolidColorBrush(cfg.color),
                StrokeThickness = 1.8,
                StrokeLineJoin  = PenLineJoin.Round
            };
            canvas.Children.Add(poly);

            // 최신값 표시 (오른쪽 끝점에 점 + 값)
            if (n > 0)
            {
                double lastVal = arr[n - 1];
                double lx = padL + (n - 1) * step;
                double lr = (lastVal - rangeMin) / (rangeMax - rangeMin);
                lr = Math.Max(0, Math.Min(1, lr));
                double ly = padT + h * (1.0 - lr);

                var dot = new Ellipse
                {
                    Width = 6, Height = 6,
                    Fill  = new SolidColorBrush(cfg.color)
                };
                Canvas.SetLeft(dot, lx - 3);
                Canvas.SetTop(dot,  ly - 3);
                canvas.Children.Add(dot);

                string fmt   = cfg.unit == "MPa" ? "F3" : "F1";
                var valLabel = new TextBlock
                {
                    Text       = lastVal.ToString(fmt) + " " + cfg.unit,
                    Foreground = new SolidColorBrush(cfg.color),
                    FontSize   = 10,
                    FontWeight = FontWeights.Bold
                };
                Canvas.SetLeft(valLabel, lx + 6);
                Canvas.SetTop(valLabel,  ly - 8);
                canvas.Children.Add(valLabel);
            }
        }
    }
}

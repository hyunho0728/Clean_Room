using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Clean_Room
{
    public partial class SensorDashboard : Window
    {
        // ── 카드 설정 (이름, 단위, 최소, 최대, 경고 임계값, 강조색) ──
        internal record CardConfig(string Name, string Icon, string Unit,
                                   double Min, double Max, double WarnHi, double DangerHi, Color Accent);

        internal static readonly CardConfig[] Cards =
        {
            new("온도",  "🌡", "°C",   0,  50, 28,  30, Color.FromRgb(0x22,0xD3,0xEE)),
            new("습도",  "💧", "%RH",  0, 100, 70,  80, Color.FromRgb(0x34,0xD3,0x99)),
            new("압력",  "⟳",  "MPa",  0,   1,0.08,0.1,Color.FromRgb(0xA7,0x8B,0xFA)),
            new("진동",  "📳", "m/s²", 0,   2,1.0, 1.5,Color.FromRgb(0xFB,0xBF,0x24)),
            new("거리",  "📏",  "m",   0,   5,4.5, 4.8,Color.FromRgb(0xF4,0x72,0x18)),
        };

        private const double CX = 65, CY = 65, R = 50;  // 아크 원 중심·반지름
        private const double ArcStart = 135, ArcSweep = 270;

        // Room 1 / Room 2 각각 5개 아크·텍스트
        private readonly Path[]      _fgArcs1     = new Path[5];
        private readonly TextBlock[] _valueTexts1 = new TextBlock[5];
        private readonly TextBlock[] _statusDots1 = new TextBlock[5];

        private readonly Path[]      _fgArcs2     = new Path[5];
        private readonly TextBlock[] _valueTexts2 = new TextBlock[5];
        private readonly TextBlock[] _statusDots2 = new TextBlock[5];

        private readonly AlarmService                      _alarmService = new();
        private readonly ObservableCollection<AlarmRecord> _alarms       = new();

        // 알람 일시정지
        private int  _alarmHitCount = 0;
        private bool _alarmPaused   = false;

        // 분리창은 AdminWindow가 관리 — 여기서는 참조 불필요
        private SensorDataService? _svc1, _svc2;

        public SensorDashboard(SensorDataService service1, SensorDataService service2, int roomFilter = 0)
        {
            InitializeComponent();
            _svc1 = service1;
            _svc2 = service2;

            // roomFilter: 0=전체, 1=R1만, 2=R2만
            if (roomFilter == 1)
            {
                headerRoom2.Visibility = Visibility.Collapsed;
                isoBar2.Visibility     = Visibility.Collapsed;
                cardsPanel2.Visibility = Visibility.Collapsed;
                Title = "클린룸 1 센서 현황";
            }
            else if (roomFilter == 2)
            {
                headerRoom1.Visibility = Visibility.Collapsed;
                isoBar1.Visibility     = Visibility.Collapsed;
                cardsPanel1.Visibility = Visibility.Collapsed;
                Title = "클린룸 2 센서 현황";
            }

            BuildCards(cardsPanel1, _fgArcs1, _valueTexts1, _statusDots1, service1, 1);
            BuildCards(cardsPanel2, _fgArcs2, _valueTexts2, _statusDots2, service2, 2);

            alarmList.ItemsSource = _alarms;
            _alarmService.AlarmTriggered += (_, record) =>
            {
                if (_alarmPaused) return;          // 일시정지 중: 새 기록 무시
                _alarms.Insert(0, record);
                txtAlarmCount.Text = $"({_alarms.Count}건)";
                alarmScroll.ScrollToTop();

                _alarmHitCount++;
                if (_alarmHitCount >= 2)
                {
                    _alarmPaused = true;
                    txtAlarmPaused.Visibility  = Visibility.Visible;
                    btnResumeAlarm.Visibility  = Visibility.Visible;
                    txtAlarmCount.Text = $"({_alarms.Count}건)";
                }
            };

            // AdminWindow 장비 고장 이벤트 구독 (Owner 확정 후)
            Loaded += (_, _) =>
            {
                if (Owner is AdminWindow admin)
                    admin.EquipmentFaultOccurred += AddEquipmentFaultRecord;
            };
            Closed += (_, _) =>
            {
                if (Owner is AdminWindow admin)
                    admin.EquipmentFaultOccurred -= AddEquipmentFaultRecord;
            };

            service1.DataUpdated += (_, data) =>
            {
                UpdateCards(data, _fgArcs1, _valueTexts1, _statusDots1);
                UpdateISOBadge(data.ParticleCount, txtISO1, txtParticle1, txtISOStatus1, isoBadge1,
                               Color.FromRgb(0x22, 0xD3, 0xEE));
                txtLastUpdate.Text = "최근 갱신: " + DateTime.Now.ToString("HH:mm:ss");
                _alarmService.CheckRoom(data, "R1");
            };
            service2.DataUpdated += (_, data) =>
            {
                UpdateCards(data, _fgArcs2, _valueTexts2, _statusDots2);
                UpdateISOBadge(data.ParticleCount, txtISO2, txtParticle2, txtISOStatus2, isoBadge2,
                               Color.FromRgb(0x81, 0x8C, 0xF8));
                _alarmService.CheckRoom(data, "R2");
            };

            UpdateCards(service1.Current, _fgArcs1, _valueTexts1, _statusDots1);
            UpdateCards(service2.Current, _fgArcs2, _valueTexts2, _statusDots2);
            UpdateISOBadge(service1.Current.ParticleCount, txtISO1, txtParticle1, txtISOStatus1, isoBadge1,
                           Color.FromRgb(0x22, 0xD3, 0xEE));
            UpdateISOBadge(service2.Current.ParticleCount, txtISO2, txtParticle2, txtISOStatus2, isoBadge2,
                           Color.FromRgb(0x81, 0x8C, 0xF8));
        }

        // ── 룸 분리 버튼 — AdminWindow에 위임해 독립적으로 관리 ──────
        private void BtnDetachRoom1_Click(object sender, RoutedEventArgs e)
            => OpenDetached(1);

        private void BtnDetachRoom2_Click(object sender, RoutedEventArgs e)
            => OpenDetached(2);

        private void OpenDetached(int room)
        {
            // AdminWindow가 Owner인 경우 → 분리창도 AdminWindow가 관리 (메인 대시보드 종속 X)
            if (Owner is AdminWindow admin)
            {
                admin.OpenDashboard(room);
                return;
            }
            // standalone 실행 시 fallback (Owner 없는 경우)
            var win = new SensorDashboard(_svc1!, _svc2!, room);
            win.Show();
        }

        // ── 장비 고장 기록 추가 (AdminWindow 이벤트 핸들러) ──────────
        private void AddEquipmentFaultRecord(AlarmRecord record)
        {
            _alarms.Insert(0, record);
            txtAlarmCount.Text = $"({_alarms.Count}건)";
            alarmScroll.ScrollToTop();
        }

        // ── 알람 일시정지 해제 ────────────────────────────────────────
        private void BtnResumeAlarm_Click(object sender, RoutedEventArgs e)
        {
            _alarmPaused      = false;
            _alarmHitCount    = 0;
            txtAlarmPaused.Visibility = Visibility.Collapsed;
            btnResumeAlarm.Visibility = Visibility.Collapsed;
        }

        private void BtnClearAlarms_Click(object sender, RoutedEventArgs e)
        {
            _alarms.Clear();
            _alarmHitCount = 0;
            _alarmPaused   = false;
            txtAlarmCount.Text        = "(0건)";
            txtAlarmPaused.Visibility = Visibility.Collapsed;
            btnResumeAlarm.Visibility = Visibility.Collapsed;
        }

        // 도움말: 각 센서 위험값 안내 팝업
        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            new HelpWindow { Owner = this }.ShowDialog();
        }

        // 알람 이력 창 열기 (라이브 알람 컬렉션 전달)
        private void BtnHistory_Click(object sender, RoutedEventArgs e)
        {
            new AlarmHistoryWindow(_alarms) { Owner = this }.Show();
        }

        // ── 카드 UI 빌드 ──────────────────────────────────────────
        private void BuildCards(StackPanel panel, Path[] arcs, TextBlock[] values, TextBlock[] dots,
                                SensorDataService service, int room)
        {
            for (int i = 0; i < Cards.Length; i++)
            {
                var card = CreateCard(i, arcs, values, dots);
                AddDragBehavior(card, i, service, room);
                panel.Children.Add(card);
            }
        }

        // ── 카드 드래그 → FloatingCardWindow ──────────────────────
        private void AddDragBehavior(Border card, int cardIdx, SensorDataService service, int room)
        {
            System.Windows.Point dragStart = default;
            bool tracking = false;

            card.MouseLeftButtonDown += (_, e) =>
            {
                dragStart = e.GetPosition(this);
                tracking  = true;
                card.CaptureMouse();
                e.Handled = true;
            };

            card.MouseMove += (_, e) =>
            {
                if (!tracking || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
                var pos = e.GetPosition(this);
                if (Math.Abs(pos.X - dragStart.X) > 8 || Math.Abs(pos.Y - dragStart.Y) > 8)
                {
                    tracking = false;
                    card.ReleaseMouseCapture();
                    var screen = PointToScreen(pos);
                    new FloatingCardWindow(cardIdx, service, room)
                    {
                        Left  = screen.X - 90,
                        Top   = screen.Y - 20,
                        Owner = this
                    }.Show();
                }
            };

            card.MouseLeftButtonUp += (_, _) =>
            {
                tracking = false;
                card.ReleaseMouseCapture();
            };
        }

        private Border CreateCard(int idx, Path[] arcs, TextBlock[] values, TextBlock[] dots)
        {
            var cfg = Cards[idx];
            var accent = new SolidColorBrush(cfg.Accent);

            // 배경 아크 (회색 트랙)
            var bgArc = new Path
            {
                Stroke          = new SolidColorBrush(Color.FromRgb(0x1E,0x3A,0x5F)),
                StrokeThickness = 8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                Data            = MakeArcGeometry(CX, CY, R, ArcStart, ArcStart + ArcSweep)
            };

            // 값 아크 (컬러)
            var fgArc = new Path
            {
                Stroke          = accent,
                StrokeThickness = 8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                Data            = MakeArcGeometry(CX, CY, R, ArcStart, ArcStart + 0.5)
            };
            arcs[idx] = fgArc;

            // 중앙 값 텍스트
            var valueText = new TextBlock
            {
                Text       = "---",
                Foreground = Brushes.White,
                FontSize   = 22,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            values[idx] = valueText;

            // 단위
            var unitText = new TextBlock
            {
                Text       = cfg.Unit,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B,0x72,0x80)),
                FontSize   = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin     = new Thickness(0, 2, 0, 0)
            };

            // 상태 표시
            var statusDot = new TextBlock
            {
                Text       = "● 대기",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B,0x72,0x80)),
                FontSize   = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin     = new Thickness(0, 6, 0, 0)
            };
            dots[idx] = statusDot;

            // Canvas (아크 + 텍스트 오버레이)
            var canvas = new Canvas { Width = 130, Height = 130 };
            canvas.Children.Add(bgArc);
            canvas.Children.Add(fgArc);

            // 텍스트를 Canvas 중앙에 배치
            var centerPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            centerPanel.Children.Add(valueText);
            centerPanel.Children.Add(unitText);

            var centerBorder = new Border
            {
                Width  = 80,
                Height = 50,
                Child  = centerPanel
            };
            Canvas.SetLeft(centerBorder, (130 - 80) / 2.0);
            Canvas.SetTop(centerBorder, (130 - 50) / 2.0 + 5);
            canvas.Children.Add(centerBorder);

            // 카드 내부 레이아웃
            var inner = new StackPanel { Margin = new Thickness(8, 10, 8, 10) };

            // 카드 헤더 (아이콘 + 이름)
            var header = new StackPanel { Orientation = Orientation.Horizontal,
                                          HorizontalAlignment = HorizontalAlignment.Center,
                                          Margin = new Thickness(0, 0, 0, 4) };
            header.Children.Add(new TextBlock
            {
                Text      = cfg.Icon + " " + cfg.Name,
                Foreground = accent,
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold
            });

            inner.Children.Add(header);
            inner.Children.Add(canvas);
            inner.Children.Add(statusDot);

            return new Border
            {
                Width           = 160,
                Margin          = new Thickness(6, 0, 6, 0),
                Background      = new SolidColorBrush(Color.FromRgb(0x0D,0x1B,0x2A)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x55, cfg.Accent.R, cfg.Accent.G, cfg.Accent.B)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Child           = inner
            };
        }

        // ── 데이터 업데이트 ───────────────────────────────────────
        private void UpdateCards(SensorData data, Path[] arcs, TextBlock[] values, TextBlock[] dots)
        {
            double[] vals = { data.Temperature, data.Humidity, data.Pressure,
                              data.Vibration,   data.Distance };
            for (int i = 0; i < 5; i++)
                UpdateCard(i, vals[i], arcs, values, dots);
        }

        private void UpdateCard(int idx, double value, Path[] arcs, TextBlock[] values, TextBlock[] dots)
        {
            var cfg    = Cards[idx];
            double pct = Math.Max(0, Math.Min(1, (value - cfg.Min) / (cfg.Max - cfg.Min)));
            bool warn   = value > cfg.WarnHi;
            bool danger = value > cfg.DangerHi;

            double endAngle = ArcStart + pct * ArcSweep;
            arcs[idx].Data   = MakeArcGeometry(CX, CY, R, ArcStart, endAngle);
            arcs[idx].Stroke = danger
                ? new SolidColorBrush(Color.FromRgb(0xEF,0x44,0x44))
                : warn
                    ? new SolidColorBrush(Color.FromRgb(0xFB,0xBF,0x24))
                    : new SolidColorBrush(cfg.Accent);

            values[idx].Text = value.ToString("F1");
            dots[idx].Text   = danger ? "🔴 위험" : warn ? "🟡 주의" : "● 정상";
            dots[idx].Foreground = danger
                ? new SolidColorBrush(Color.FromRgb(0xEF,0x44,0x44))
                : warn
                    ? new SolidColorBrush(Color.FromRgb(0xFB,0xBF,0x24))
                    : new SolidColorBrush(Color.FromRgb(0x22,0xC5,0x5E));
        }

        // ── ISO 등급 배지 업데이트 ────────────────────────────────
        /// <summary>
        /// ISO 14644-1 (0.5 μm 입자수 기준)으로 등급을 계산하고 배지 UI를 갱신합니다.
        /// 목표: ISO 5 (≤ 3,520 개/m³)
        /// </summary>
        private static void UpdateISOBadge(double particles,
                                           TextBlock txtISO, TextBlock txtParticle,
                                           TextBlock txtStatus, Border badge,
                                           Color accentColor)
        {
            // ISO 3~9 임계값 (≥0.5 μm 입자수/m³)
            // ISO 1·2는 0.5μm 기준이 없으므로 제외
            int isoClass;
            if      (particles <=        35) isoClass = 3;
            else if (particles <=       352) isoClass = 4;
            else if (particles <=     3_520) isoClass = 5;
            else if (particles <=    35_200) isoClass = 6;
            else if (particles <=   352_000) isoClass = 7;
            else if (particles <= 3_520_000) isoClass = 8;
            else                             isoClass = 9;

            bool meetTarget = isoClass <= 5;   // 목표 ISO 5 이하

            // ISO 배지 텍스트 + 색상
            txtISO.Text = $"ISO {isoClass}";
            txtISO.Foreground = meetTarget
                ? new SolidColorBrush(accentColor)
                : new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));   // 노랑

            // 배지 테두리 색
            badge.BorderBrush = meetTarget
                ? new SolidColorBrush(accentColor)
                : new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));

            // 배지 배경: 목표 충족 = 짙은 초록, 초과 = 짙은 노랑
            badge.Background = meetTarget
                ? new SolidColorBrush(Color.FromRgb(0x0D, 0x2A, 0x1A))
                : new SolidColorBrush(Color.FromRgb(0x2A, 0x20, 0x05));

            // 입자수 (콤마 포맷)
            txtParticle.Text = particles.ToString("N0");

            // 상태 텍스트
            if (meetTarget)
            {
                txtStatus.Text       = "✓ 목표 충족";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            }
            else
            {
                txtStatus.Text       = $"⚠ 목표 초과 (ISO {isoClass})";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            }
        }

        // ── 아크 Geometry 계산 ────────────────────────────────────
        private static PathGeometry MakeArcGeometry(double cx, double cy, double r,
                                                     double startDeg, double endDeg)
        {
            // 각도가 너무 작으면 Path가 렌더링 안 됨 → 최소 0.5° 보장
            if (endDeg - startDeg < 0.5) endDeg = startDeg + 0.5;

            double s = startDeg * Math.PI / 180.0;
            double e = endDeg   * Math.PI / 180.0;

            var start = new Point(cx + r * Math.Cos(s), cy + r * Math.Sin(s));
            var end   = new Point(cx + r * Math.Cos(e), cy + r * Math.Sin(e));
            bool isLarge = (endDeg - startDeg) > 180;

            var fig = new PathFigure { StartPoint = start, IsFilled = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0,
                                             isLarge, SweepDirection.Clockwise, true));
            return new PathGeometry(new[] { fig });
        }
    }
}

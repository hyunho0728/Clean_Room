using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Wpf;

namespace Clean_Room
{
    public partial class AdminWindow : Window
    {
        private DispatcherTimer _clockTimer;
        private PerspectiveCamera _cam1, _cam2;

        // 스페이스 + 좌클릭 패닝
        private bool              _isPanning;
        private Point             _panLast;
        private PerspectiveCamera _panCam;

        // Ctrl + 좌클릭 궤도 회전
        private bool              _isOrbiting;
        private Point             _orbitLast;
        private PerspectiveCamera _orbitCam;

        // 클릭 가능 요소 등록 (Visual3D → 표시 이름)
        private readonly Dictionary<Visual3D, string> _clickables1 = new();
        private readonly Dictionary<Visual3D, string> _clickables2 = new();

        // 호버 하이라이트 — 요소별 onHover/onLeave 액션
        private readonly Dictionary<Visual3D, (Action onHover, Action onLeave)> _hoverActions1 = new();
        private readonly Dictionary<Visual3D, (Action onHover, Action onLeave)> _hoverActions2 = new();
        private Visual3D? _hovered1 = null;
        private Visual3D? _hovered2 = null;
        private Action?        _restoreHover1 = null;
        private Action?        _restoreHover2 = null;


        // 방 치수 (반-크기)
        private const double RX = 1.0, RY = 1.0, RZ = 2.0;

        // ── 온습도계 레이아웃 상수 ──────────────────────────────
        private const double DispFaceX  = RX - 0.010;
        private const double DispCy     = 0.10;
        private const double DispCz     = RZ * 0.52;
        private const double DispDevH   = 0.26;
        private const double DispTempY  = DispCy + DispDevH * 0.175;
        private const double DispHumY   = DispCy - DispDevH * 0.13;
        private const double DispTDH    = 0.052;
        private const double DispHDH    = 0.046;

        // ── 압력 게이지 레이아웃 상수 ──────────────────────────
        private const double PressFaceX = RX - 0.06;
        private const double PressCy    = -0.20;
        private const double PressCz    = RZ * 0.12;
        private const double PressGr    = 0.09;

        // ── 7-세그먼트 패턴 (a b c d e f g) ────────────────────
        private static readonly bool[][] Seg7 =
        {
            new[]{true, true, true, true, true, true, false},   // 0
            new[]{false,true, true, false,false,false,false},   // 1
            new[]{true, true, false,true, true, false,true },   // 2
            new[]{true, true, true, true, false,false,true },   // 3
            new[]{false,true, true, false,false,true, true },   // 4
            new[]{true, false,true, true, false,true, true },   // 5
            new[]{true, false,true, true, true, true, true },   // 6
            new[]{true, true, true, false,false,false,false},   // 7
            new[]{true, true, true, true, true, true, true },   // 8
            new[]{true, true, true, true, false,true, true },   // 9
        };

        // ── 실시간 업데이트 가능 요소 ───────────────────────────
        private SensorDataService? _sensorService;
        private (LinesVisual3D tempDig, LinesVisual3D humDig, LinesVisual3D needle) _live1;
        private (LinesVisual3D tempDig, LinesVisual3D humDig, LinesVisual3D needle) _live2;

        // 시점 프리셋
        private static readonly (Point3D pos, Vector3D dir, Vector3D up)[] _views =
        {
            (new Point3D( 3.0,  2.5,  5.5), new Vector3D(-3.0,-2.5,-5.5), new Vector3D(0,1,0)), // 등각
            (new Point3D( 0,    0,    7  ), new Vector3D( 0,   0,  -1  ), new Vector3D(0,1,0)), // 정면
            (new Point3D( 6,    0,    0  ), new Vector3D(-1,   0,   0  ), new Vector3D(0,1,0)), // 측면
            (new Point3D( 0,    7,    0  ), new Vector3D( 0,  -1,   0  ), new Vector3D(0,0,-1)), // 상단
        };

        public AdminWindow(User user)
        {
            InitializeComponent();
            txtAdminName.Text = $"  |  {user.FullName} (관리자)";

            _cam1 = BuildScene(viewport1, Color.FromRgb(0x22, 0xD3, 0xEE), _clickables1, _hoverActions1, out _live1);
            _cam2 = BuildScene(viewport2, Color.FromRgb(0x81, 0x8C, 0xF8), _clickables2, _hoverActions2, out _live2);

            // 센서 서비스 (2초 간격 테스트 데이터 → 실제 센서 연결 시 SensorDataService.UpdateFromExternal() 호출)
            _sensorService = new SensorDataService(TimeSpan.FromSeconds(2));
            _sensorService.DataUpdated += (_, data) =>
            {
                UpdateViewport(_live1, data);
                UpdateViewport(_live2, data);
            };
            _sensorService.Start();

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) =>
                txtDateTime.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
            _clockTimer.Start();
            txtDateTime.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");

            // 스페이스 + 좌클릭 패닝
            this.KeyDown                    += Window_KeyDown;
            this.KeyUp                      += Window_KeyUp;
            this.PreviewMouseLeftButtonDown += Window_PreviewMouseLeftButtonDown;
            this.PreviewMouseMove           += Window_PreviewMouseMove;
            this.PreviewMouseLeftButtonUp   += Window_PreviewMouseLeftButtonUp;
        }

        // ── 씬 빌드 ──────────────────────────────────────────────
        private PerspectiveCamera BuildScene(HelixViewport3D vp, Color accent,
                                              Dictionary<Visual3D, string> clickables,
                                              Dictionary<Visual3D, (Action onHover, Action onLeave)> hoverActions,
                                              out (LinesVisual3D tempDig, LinesVisual3D humDig, LinesVisual3D needle) live)
        {
            var cam = new PerspectiveCamera
            {
                Position      = _views[0].pos,
                LookDirection = _views[0].dir,
                UpDirection   = _views[0].up,
                FieldOfView   = 42
            };
            vp.Camera = cam;

            Color dim = Color.FromRgb(
                (byte)(accent.R * 0.38),
                (byte)(accent.G * 0.38),
                (byte)(accent.B * 0.38));
            Color bright = Color.FromRgb(
                (byte)Math.Min(accent.R + 90, 255),
                (byte)Math.Min(accent.G + 90, 255),
                (byte)Math.Min(accent.B + 90, 255));

            // 라인 레이어 (두께·색상별 분리)
            var main   = new LinesVisual3D { Color = accent,       Thickness = 1.5 };
            var detail = new LinesVisual3D { Color = dim,          Thickness = 1.0 };
            var door   = new LinesVisual3D { Color = Colors.White, Thickness = 2.0 };
            var nozzle = new LinesVisual3D { Color = bright,       Thickness = 1.5 };

            DrawBox(main);
            DrawRoomDetails(detail);
            DrawAirShower(main, door, nozzle);

            vp.Children.Add(main);
            vp.Children.Add(detail);
            vp.Children.Add(door);
            vp.Children.Add(nozzle);

            // 반투명 바닥·천장 패널 (라인 위에 추가)
            AddPanel(vp, 0, -RY, 0, 2*RX, 2*RZ, Color.FromArgb(30, accent.R, accent.G, accent.B));
            AddPanel(vp, 0,  RY, 0, 2*RX, 2*RZ, Color.FromArgb(15, accent.R, accent.G, accent.B));

            // 에어샤워 인터랙티브 프록시
            AddAirShowerProxy(vp, clickables, hoverActions, door, nozzle);

            // HEPA 필터 (후측 좌측 상단)
            AddHEPAFilter(vp, clickables, hoverActions);

            // FFU 팬 필터 유닛 (출입구 쪽 2개)
            AddFFUFans(vp, clickables, hoverActions);

            // 압력 센서 (출입구 우측벽)
            AddPressureSensors(vp, clickables, hoverActions, out var needle);

            // 온습도계 (우측벽 중간)
            AddTempHumidDisplay(vp, clickables, hoverActions, out var tempDig, out var humDig);

            // 8대 공정 장비
            AddFabEquipment(vp, clickables, hoverActions);

            live = (tempDig, humDig, needle);
            return cam;
        }

        // ── FFU 팬 필터 유닛 ─────────────────────────────────────
        private static void AddFFUFans(HelixViewport3D vp,
            Dictionary<Visual3D, string> clickables,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> hoverActions)
        {
            double fw = RX * 0.72;  // 유닛 가로
            double fd = RZ * 0.36;  // 유닛 세로
            double fh = 0.22;       // 두께 (이미지처럼 두꺼운 박스)

            double[] ffuX = { -RX * 0.5, RX * 0.5 };
            double[] ffuZ = { RZ * 0.75 };

            double yTop = RY;
            double yBot = RY - fh;

            var housingCol = Color.FromRgb(0x00, 0xCC, 0xFF);
            var fanCol     = Color.FromRgb(0xAA, 0xDD, 0xFF);

            foreach (double fx in ffuX)
            foreach (double fz in ffuZ)
            {
                double x0 = fx - fw/2, x1 = fx + fw/2;
                double z0 = fz - fd/2, z1 = fz + fd/2;

                // ① 하우징 박스 프레임
                var frame = new LinesVisual3D { Color = housingCol, Thickness = 1.5 };
                Seg(frame, x0,yTop,z0, x1,yTop,z0); Seg(frame, x1,yTop,z0, x1,yTop,z1);
                Seg(frame, x1,yTop,z1, x0,yTop,z1); Seg(frame, x0,yTop,z1, x0,yTop,z0);
                Seg(frame, x0,yBot,z0, x1,yBot,z0); Seg(frame, x1,yBot,z0, x1,yBot,z1);
                Seg(frame, x1,yBot,z1, x0,yBot,z1); Seg(frame, x0,yBot,z1, x0,yBot,z0);
                Seg(frame, x0,yTop,z0, x0,yBot,z0); Seg(frame, x1,yTop,z0, x1,yBot,z0);
                Seg(frame, x1,yTop,z1, x1,yBot,z1); Seg(frame, x0,yTop,z1, x0,yBot,z1);
                vp.Children.Add(frame);

                // ② 팬 외부 원형 프레임 (아랫면)
                double r = Math.Min(fw, fd) * 0.38;
                int circSegs = 24;
                var fanRing = new LinesVisual3D { Color = fanCol, Thickness = 1.3 };
                for (int i = 0; i < circSegs; i++)
                {
                    double a0 = 2*Math.PI * i     / circSegs;
                    double a1 = 2*Math.PI * (i+1) / circSegs;
                    fanRing.Points.Add(new Point3D(fx + r*Math.Cos(a0), yBot, fz + r*Math.Sin(a0)));
                    fanRing.Points.Add(new Point3D(fx + r*Math.Cos(a1), yBot, fz + r*Math.Sin(a1)));
                }
                vp.Children.Add(fanRing);

                // ③ 팬 블레이드 (6개, 곡선 느낌)
                var blades = new LinesVisual3D { Color = fanCol, Thickness = 1.0 };
                int nBlades = 6;
                double hubR = r * 0.18;
                double sweep = Math.PI / (nBlades * 1.4);
                for (int i = 0; i < nBlades; i++)
                {
                    double ang = 2*Math.PI * i / nBlades;
                    // 허브 → 중간
                    blades.Points.Add(new Point3D(fx + hubR*Math.Cos(ang),           yBot, fz + hubR*Math.Sin(ang)));
                    blades.Points.Add(new Point3D(fx + r*0.52*Math.Cos(ang+sweep),   yBot, fz + r*0.52*Math.Sin(ang+sweep)));
                    // 중간 → 외부
                    blades.Points.Add(new Point3D(fx + r*0.52*Math.Cos(ang+sweep),   yBot, fz + r*0.52*Math.Sin(ang+sweep)));
                    blades.Points.Add(new Point3D(fx + r*0.88*Math.Cos(ang+sweep*2), yBot, fz + r*0.88*Math.Sin(ang+sweep*2)));
                }
                vp.Children.Add(blades);

                // ④ 허브 원
                var hub = new LinesVisual3D { Color = fanCol, Thickness = 1.0 };
                for (int i = 0; i < 10; i++)
                {
                    double a0 = 2*Math.PI * i     / 10;
                    double a1 = 2*Math.PI * (i+1) / 10;
                    hub.Points.Add(new Point3D(fx + hubR*Math.Cos(a0), yBot, fz + hubR*Math.Sin(a0)));
                    hub.Points.Add(new Point3D(fx + hubR*Math.Cos(a1), yBot, fz + hubR*Math.Sin(a1)));
                }
                vp.Children.Add(hub);

                // ⑤ 컨트롤 박스 (하우징 하단 측면에 부착)
                double cbW = fw * 0.18, cbH = fh * 0.45, cbD = fd * 0.22;
                double cbX0 = x0 - cbW, cbX1 = x0;
                double cbY0 = yBot, cbY1 = yBot + cbH;
                double cbZ0 = fz - cbD/2, cbZ1 = fz + cbD/2;
                var ctrlBox = new LinesVisual3D { Color = housingCol, Thickness = 1.0 };
                Seg(ctrlBox, cbX0,cbY0,cbZ0, cbX1,cbY0,cbZ0); Seg(ctrlBox, cbX1,cbY0,cbZ0, cbX1,cbY0,cbZ1);
                Seg(ctrlBox, cbX1,cbY0,cbZ1, cbX0,cbY0,cbZ1); Seg(ctrlBox, cbX0,cbY0,cbZ1, cbX0,cbY0,cbZ0);
                Seg(ctrlBox, cbX0,cbY1,cbZ0, cbX1,cbY1,cbZ0); Seg(ctrlBox, cbX1,cbY1,cbZ0, cbX1,cbY1,cbZ1);
                Seg(ctrlBox, cbX1,cbY1,cbZ1, cbX0,cbY1,cbZ1); Seg(ctrlBox, cbX0,cbY1,cbZ1, cbX0,cbY1,cbZ0);
                Seg(ctrlBox, cbX0,cbY0,cbZ0, cbX0,cbY1,cbZ0); Seg(ctrlBox, cbX1,cbY0,cbZ0, cbX1,cbY1,cbZ0);
                Seg(ctrlBox, cbX1,cbY0,cbZ1, cbX1,cbY1,cbZ1); Seg(ctrlBox, cbX0,cbY0,cbZ1, cbX0,cbY1,cbZ1);
                vp.Children.Add(ctrlBox);

                // hit-test 프록시
                int ffuIdx = Array.IndexOf(ffuX, fx) + 1;
                var ffuProxy = new BoxVisual3D
                {
                    Center = new Point3D(fx, (yTop+yBot)/2.0, fz),
                    Width  = fw, Height = fh, Length = fd,
                    Fill   = new SolidColorBrush(Color.FromArgb(2, 255, 255, 255))
                };
                vp.Children.Add(ffuProxy);
                clickables[ffuProxy] = $"FFU 팬 필터 #{ffuIdx}\n공기 정화 장치 (Fan Filter Unit)";
                var fc = housingCol;
                hoverActions[ffuProxy] = (() => frame.Color = Color.FromRgb(0xFF, 0xFF, 0x88),
                                          () => frame.Color = fc);
            }
        }

        // ── 압력 센서 (원형 게이지) ──────────────────────────────
        private static void AddPressureSensors(HelixViewport3D vp,
            Dictionary<Visual3D, string> clickables,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> hoverActions,
            out LinesVisual3D needle)
        {
            double wallX  = RX;
            double faceX  = PressFaceX;
            double cy     = PressCy;
            double cz     = PressCz;
            double gr     = PressGr;
            double br     = gr * 1.15;    // 베젤 반지름

            var metalCol  = Color.FromRgb(0x99, 0xAA, 0xBB);
            var tickCol   = Color.FromRgb(0x44, 0x88, 0xCC);
            var needleCol = Color.FromRgb(0xFF, 0x44, 0x44);

            int segs = 28;

            // ① 전면 베젤 원
            var bezel = new LinesVisual3D { Color = metalCol, Thickness = 2.0 };
            for (int i = 0; i < segs; i++)
            {
                double a0 = 2*Math.PI * i     / segs;
                double a1 = 2*Math.PI * (i+1) / segs;
                bezel.Points.Add(new Point3D(faceX, cy + br*Math.Sin(a0), cz + br*Math.Cos(a0)));
                bezel.Points.Add(new Point3D(faceX, cy + br*Math.Sin(a1), cz + br*Math.Cos(a1)));
            }
            vp.Children.Add(bezel);

            // ② 후면 원 (벽 접촉부)
            var backRing = new LinesVisual3D { Color = metalCol, Thickness = 1.0 };
            for (int i = 0; i < segs; i++)
            {
                double a0 = 2*Math.PI * i     / segs;
                double a1 = 2*Math.PI * (i+1) / segs;
                backRing.Points.Add(new Point3D(wallX, cy + br*Math.Sin(a0), cz + br*Math.Cos(a0)));
                backRing.Points.Add(new Point3D(wallX, cy + br*Math.Sin(a1), cz + br*Math.Cos(a1)));
            }
            vp.Children.Add(backRing);

            // ③ 측면 연결선 (전-후)
            var bodyLines = new LinesVisual3D { Color = metalCol, Thickness = 1.0 };
            for (int i = 0; i < 8; i++)
            {
                double a = 2*Math.PI * i / 8;
                bodyLines.Points.Add(new Point3D(faceX, cy + br*Math.Sin(a), cz + br*Math.Cos(a)));
                bodyLines.Points.Add(new Point3D(wallX, cy + br*Math.Sin(a), cz + br*Math.Cos(a)));
            }
            vp.Children.Add(bodyLines);

            // ④ 다이얼 면 원
            var dial = new LinesVisual3D { Color = tickCol, Thickness = 1.0 };
            for (int i = 0; i < segs; i++)
            {
                double a0 = 2*Math.PI * i     / segs;
                double a1 = 2*Math.PI * (i+1) / segs;
                dial.Points.Add(new Point3D(faceX, cy + gr*Math.Sin(a0), cz + gr*Math.Cos(a0)));
                dial.Points.Add(new Point3D(faceX, cy + gr*Math.Sin(a1), cz + gr*Math.Cos(a1)));
            }
            vp.Children.Add(dial);

            // ⑤ 눈금 (주 12개 + 보조 48개)
            var ticks = new LinesVisual3D { Color = tickCol, Thickness = 0.9 };
            for (int i = 0; i < 12; i++)
            {
                double a = 2*Math.PI * i / 12;
                ticks.Points.Add(new Point3D(faceX, cy + gr*0.78*Math.Sin(a), cz + gr*0.78*Math.Cos(a)));
                ticks.Points.Add(new Point3D(faceX, cy + gr*0.97*Math.Sin(a), cz + gr*0.97*Math.Cos(a)));
            }
            for (int i = 0; i < 48; i++)
            {
                double a = 2*Math.PI * i / 48;
                ticks.Points.Add(new Point3D(faceX, cy + gr*0.89*Math.Sin(a), cz + gr*0.89*Math.Cos(a)));
                ticks.Points.Add(new Point3D(faceX, cy + gr*0.97*Math.Sin(a), cz + gr*0.97*Math.Cos(a)));
            }
            vp.Children.Add(ticks);

            // ⑥ 바늘 (초기값 100 psi)
            needle = new LinesVisual3D { Color = needleCol, Thickness = 1.8 };
            DrawPressNeedle(needle, 100.0);
            vp.Children.Add(needle);

            // ⑦ 허브 원
            var hub = new LinesVisual3D { Color = tickCol, Thickness = 1.0 };
            double hubR = gr * 0.12;
            for (int i = 0; i < 10; i++)
            {
                double a0 = 2*Math.PI * i     / 10;
                double a1 = 2*Math.PI * (i+1) / 10;
                hub.Points.Add(new Point3D(faceX, cy + hubR*Math.Sin(a0), cz + hubR*Math.Cos(a0)));
                hub.Points.Add(new Point3D(faceX, cy + hubR*Math.Sin(a1), cz + hubR*Math.Cos(a1)));
            }
            vp.Children.Add(hub);

            // ⑧ 파이프 피팅 (±Z 방향, 게이지 양쪽)
            double fitR   = gr * 0.18;
            double fitLen = 0.06;
            double nutR   = fitR * 1.5;
            double nutLen = 0.018;
            foreach (double dir in new[] { -1.0, +1.0 })
            {
                double fz0 = cz + dir * br;
                double fz1 = cz + dir * (br + fitLen);
                double fz2 = cz + dir * (br + fitLen + nutLen);
                var fit = new LinesVisual3D { Color = metalCol, Thickness = 1.2 };
                // 파이프 본체
                Seg(fit, wallX, cy - fitR, fz0, wallX, cy - fitR, fz1);
                Seg(fit, wallX, cy + fitR, fz0, wallX, cy + fitR, fz1);
                Seg(fit, wallX, cy - fitR, fz0, wallX, cy + fitR, fz0);
                Seg(fit, wallX, cy - fitR, fz1, wallX, cy + fitR, fz1);
                // 너트 (육각 느낌으로 더 넓게)
                Seg(fit, wallX, cy - nutR, fz1, wallX, cy - nutR, fz2);
                Seg(fit, wallX, cy + nutR, fz1, wallX, cy + nutR, fz2);
                Seg(fit, wallX, cy - nutR, fz1, wallX, cy + nutR, fz1);
                Seg(fit, wallX, cy - nutR, fz2, wallX, cy + nutR, fz2);
                vp.Children.Add(fit);
            }

            // hit-test 프록시
            var proxy = new BoxVisual3D
            {
                Center = new Point3D((faceX + wallX) / 2.0, cy, cz),
                Width  = wallX - faceX + 0.02,
                Height = br * 2.2,
                Length = br * 2.2 + 0.16,
                Fill   = new SolidColorBrush(Color.FromArgb(2, 255, 255, 255))
            };
            vp.Children.Add(proxy);
            clickables[proxy] = "압력 센서  |  출입구 우측벽";
            var bc = metalCol;
            hoverActions[proxy] = (() => bezel.Color = Color.FromRgb(0xFF, 0xFF, 0x88),
                                   () => bezel.Color = bc);
        }

        // ── 온습도계 (디지털 디스플레이) ────────────────────────
        private static void AddTempHumidDisplay(HelixViewport3D vp,
            Dictionary<Visual3D, string> clickables,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> hoverActions,
            out LinesVisual3D tempDig, out LinesVisual3D humDig)
        {
            const double faceX = DispFaceX;
            const double cy    = DispCy;
            const double cz    = DispCz;
            const double devW  = 0.24;
            const double devH  = DispDevH;
            const double rc    = 0.028;

            var housingCol = Color.FromRgb(0xCC, 0xCC, 0xCC);
            var dispCol    = Color.FromRgb(0x33, 0x77, 0xAA);
            var digitCol   = Color.FromRgb(0x00, 0x44, 0x88);

            // ① 하우징 (둥근 사각형)
            var housing = new LinesVisual3D { Color = housingCol, Thickness = 1.8 };
            DrawRoundedRect(housing, faceX, cy, cz, devW, devH, rc);
            vp.Children.Add(housing);

            // ② 디스플레이 내부 테두리
            var display = new LinesVisual3D { Color = dispCol, Thickness = 1.0 };
            DrawRoundedRect(display, faceX, cy + devH*0.02, cz, devW*0.82, devH*0.76, rc*0.4);
            vp.Children.Add(display);

            // ③ 정적 기호: °C (온도 단위), 소수점
            var staticTemp = new LinesVisual3D { Color = digitCol, Thickness = 1.4 };
            // 소수점
            staticTemp.Points.Add(new Point3D(faceX, DispTempY - DispTDH*0.46, cz - 0.005));
            staticTemp.Points.Add(new Point3D(faceX, DispTempY - DispTDH*0.46, cz - 0.003));
            // ° 기호
            double degZ = cz + 0.056, degY = DispTempY + DispTDH*0.38, degR = DispTDH*0.09;
            for (int i = 0; i < 8; i++)
            {
                double a0 = 2*Math.PI*i/8, a1 = 2*Math.PI*(i+1)/8;
                staticTemp.Points.Add(new Point3D(faceX, degY + degR*Math.Sin(a0), degZ + degR*Math.Cos(a0)));
                staticTemp.Points.Add(new Point3D(faceX, degY + degR*Math.Sin(a1), degZ + degR*Math.Cos(a1)));
            }
            // C 글자
            double cLetZ = cz + 0.074, cLetH = DispTDH * 0.50;
            Seg(staticTemp, faceX, DispTempY + cLetH*0.42, cLetZ - cLetH*0.25, faceX, DispTempY + cLetH*0.42, cLetZ + cLetH*0.05);
            Seg(staticTemp, faceX, DispTempY + cLetH*0.42, cLetZ - cLetH*0.25, faceX, DispTempY - cLetH*0.42, cLetZ - cLetH*0.25);
            Seg(staticTemp, faceX, DispTempY - cLetH*0.42, cLetZ - cLetH*0.25, faceX, DispTempY - cLetH*0.42, cLetZ + cLetH*0.05);
            vp.Children.Add(staticTemp);

            // ④ 정적 기호: 스마일, %
            var staticHum = new LinesVisual3D { Color = digitCol, Thickness = 1.3 };
            double smR = DispHDH*0.40, smZ = cz - 0.076, smY = DispHumY;
            for (int i = 0; i < 16; i++)
            {
                double a0 = 2*Math.PI*i/16, a1 = 2*Math.PI*(i+1)/16;
                staticHum.Points.Add(new Point3D(faceX, smY + smR*Math.Sin(a0), smZ + smR*Math.Cos(a0)));
                staticHum.Points.Add(new Point3D(faceX, smY + smR*Math.Sin(a1), smZ + smR*Math.Cos(a1)));
            }
            Seg(staticHum, faceX, smY + smR*0.28, smZ - smR*0.30, faceX, smY + smR*0.30, smZ - smR*0.30);
            Seg(staticHum, faceX, smY + smR*0.28, smZ + smR*0.30, faceX, smY + smR*0.30, smZ + smR*0.30);
            for (int i = 0; i < 8; i++)
            {
                double a0 = Math.PI*i/8, a1 = Math.PI*(i+1)/8;
                staticHum.Points.Add(new Point3D(faceX, smY - smR*0.12 + smR*0.38*Math.Sin(-a0), smZ + smR*0.55*Math.Cos(a0)));
                staticHum.Points.Add(new Point3D(faceX, smY - smR*0.12 + smR*0.38*Math.Sin(-a1), smZ + smR*0.55*Math.Cos(a1)));
            }
            // % 기호
            double pZ = cz + 0.052, pR = DispHDH * 0.11;
            for (int i = 0; i < 6; i++)
            {
                double a0 = 2*Math.PI*i/6, a1 = 2*Math.PI*(i+1)/6;
                staticHum.Points.Add(new Point3D(faceX, DispHumY + DispHDH*0.30 + pR*Math.Sin(a0), pZ - pR*0.9 + pR*Math.Cos(a0)));
                staticHum.Points.Add(new Point3D(faceX, DispHumY + DispHDH*0.30 + pR*Math.Sin(a1), pZ - pR*0.9 + pR*Math.Cos(a1)));
                staticHum.Points.Add(new Point3D(faceX, DispHumY - DispHDH*0.30 + pR*Math.Sin(a0), pZ + pR*0.9 + pR*Math.Cos(a0)));
                staticHum.Points.Add(new Point3D(faceX, DispHumY - DispHDH*0.30 + pR*Math.Sin(a1), pZ + pR*0.9 + pR*Math.Cos(a1)));
            }
            Seg(staticHum, faceX, DispHumY + DispHDH*0.38, pZ - pR*1.2, faceX, DispHumY - DispHDH*0.38, pZ + pR*1.2);
            vp.Children.Add(staticHum);

            // ⑤ 동적 숫자 (업데이트 가능)
            tempDig = new LinesVisual3D { Color = digitCol, Thickness = 1.4 };
            humDig  = new LinesVisual3D { Color = digitCol, Thickness = 1.3 };
            DrawTempDigits(tempDig, 25.6);
            DrawHumDigits(humDig,   38.0);
            vp.Children.Add(tempDig);
            vp.Children.Add(humDig);

            // hit-test 프록시
            var proxy = new BoxVisual3D
            {
                Center = new Point3D(faceX - 0.008, cy, cz),
                Width  = 0.03, Height = devH + 0.02, Length = devW + 0.02,
                Fill   = new SolidColorBrush(Color.FromArgb(2, 255, 255, 255))
            };
            vp.Children.Add(proxy);
            clickables[proxy] = "온습도계  |  우측벽";
            var hc = housingCol;
            hoverActions[proxy] = (() => housing.Color = Color.FromRgb(0xFF, 0xFF, 0x88),
                                   () => housing.Color = hc);
        }

        // 둥근 사각형 (YZ 평면)
        private static void DrawRoundedRect(LinesVisual3D L, double x, double cy, double cz,
            double w, double h, double r)
        {
            double hw = w/2, hh = h/2;
            const int ac = 5;
            Seg(L, x, cy+hh,   cz-hw+r, x, cy+hh,   cz+hw-r);
            Seg(L, x, cy-hh,   cz-hw+r, x, cy-hh,   cz+hw-r);
            Seg(L, x, cy+hh-r, cz-hw,   x, cy-hh+r, cz-hw);
            Seg(L, x, cy+hh-r, cz+hw,   x, cy-hh+r, cz+hw);
            (double cy2, double cz2, double a0)[] corners =
            {
                (cy+hh-r, cz+hw-r, 0),
                (cy+hh-r, cz-hw+r, Math.PI/2),
                (cy-hh+r, cz-hw+r, Math.PI),
                (cy-hh+r, cz+hw-r, 3*Math.PI/2)
            };
            foreach (var (cy2, cz2, a0) in corners)
                for (int i = 0; i < ac; i++)
                {
                    double aa0 = a0 + Math.PI/2 * i / ac;
                    double aa1 = a0 + Math.PI/2 * (i+1) / ac;
                    L.Points.Add(new Point3D(x, cy2 + r*Math.Sin(aa0), cz2 + r*Math.Cos(aa0)));
                    L.Points.Add(new Point3D(x, cy2 + r*Math.Sin(aa1), cz2 + r*Math.Cos(aa1)));
                }
        }

        // ── 실시간 업데이트 ──────────────────────────────────────
        private static void UpdateViewport(
            (LinesVisual3D tempDig, LinesVisual3D humDig, LinesVisual3D needle) live,
            SensorData data)
        {
            DrawTempDigits(live.tempDig, data.Temperature);
            DrawHumDigits (live.humDig,  data.Humidity);
            DrawPressNeedle(live.needle, data.Pressure);
        }

        // 온도 숫자 3자리 재드로우 (XX.X)
        private static void DrawTempDigits(LinesVisual3D L, double temp)
        {
            L.Points.Clear();
            temp = Math.Max(0.0, Math.Min(99.9, temp));
            int d1 = (int)(temp / 10) % 10;
            int d2 = (int)(temp) % 10;
            int d3 = (int)(Math.Round(temp, 1) * 10) % 10;
            DrawSeg7(L, DispFaceX, DispTempY, DispCz - 0.078, DispTDH, Seg7[d1]);
            DrawSeg7(L, DispFaceX, DispTempY, DispCz - 0.037, DispTDH, Seg7[d2]);
            DrawSeg7(L, DispFaceX, DispTempY, DispCz + 0.018, DispTDH, Seg7[d3]);
        }

        // 습도 숫자 2자리 재드로우 (XX)
        private static void DrawHumDigits(LinesVisual3D L, double hum)
        {
            L.Points.Clear();
            hum = Math.Max(0, Math.Min(99, hum));
            int d1 = ((int)hum / 10) % 10;
            int d2 = (int)hum % 10;
            DrawSeg7(L, DispFaceX, DispHumY, DispCz - 0.033, DispHDH, Seg7[d1]);
            DrawSeg7(L, DispFaceX, DispHumY, DispCz + 0.007, DispHDH, Seg7[d2]);
        }

        // 압력 바늘 재드로우 (0~130 psi)
        // 0 psi → 1.32π, 130 psi → 0.54π  (100 psi → 0.72π)
        private static void DrawPressNeedle(LinesVisual3D L, double psi)
        {
            L.Points.Clear();
            psi = Math.Max(0, Math.Min(130, psi));
            double na = Math.PI * (1.32 - 0.006 * psi);
            L.Points.Add(new Point3D(PressFaceX, PressCy, PressCz));
            L.Points.Add(new Point3D(PressFaceX,
                PressCy + PressGr * 0.73 * Math.Sin(na),
                PressCz + PressGr * 0.73 * Math.Cos(na)));
        }

        // ── 8대 공정 장비 ─────────────────────────────────────────
        private static readonly Color[] EqPalette =
        {
            Color.FromRgb(0xFF, 0x88, 0x33), // ① 산화   오렌지
            Color.FromRgb(0xAA, 0x44, 0xFF), // ② 포토   퍼플
            Color.FromRgb(0x33, 0xCC, 0x55), // ③ 식각   그린
            Color.FromRgb(0x00, 0xBB, 0xCC), // ④ CVD    틸
            Color.FromRgb(0xFF, 0x44, 0x99), // ⑤ 이온주입 마젠타
            Color.FromRgb(0x44, 0x99, 0xFF), // ⑥ CMP   블루
            Color.FromRgb(0xFF, 0xCC, 0x00), // ⑦ PVD   골드
            Color.FromRgb(0x00, 0xDD, 0xFF), // ⑧ 세정   시안
        };

        private static void AddFabEquipment(HelixViewport3D vp,
            Dictionary<Visual3D, string> clickables,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> hoverActions)
        {
            double yBot = -RY;
            double[] zP = { RZ * 0.75, RZ * 0.25, -RZ * 0.25, -RZ * 0.75 };
            var cl = clickables; var ha = hoverActions;

            // ─ 좌벽 ①~④ ─────────────────────────────────────────────
            // AddVibSensor(vp, sx, sy_top, sz, cl, ha)  — 장비 윗면 수직 배치
            AddEq1_Furnace  (vp, -RX, yBot, zP[0], EqPalette[0], "① 산화로\n산화 공정 (Oxidation)",                  cl, ha);
            AddVibSensor(vp, -0.88, yBot+0.66, zP[0]+0.08, cl, ha);  // H1+H2=0.66

            AddEq2_Photo    (vp, -RX, yBot, zP[1], EqPalette[1], "② 포토리소그래피\n포토 공정 (Photolithography)",     cl, ha);
            AddVibSensor(vp, -0.88, yBot+0.68, zP[1]-0.06, cl, ha);  // H1+H2=0.68

            AddEq3_Etcher   (vp, -RX, yBot, zP[2], EqPalette[2], "③ 식각기\n식각 공정 (Etching)",                      cl, ha);
            AddVibSensor(vp, -0.87, yBot+0.68, zP[2]+0.06, cl, ha);  // H1+H2=0.68

            AddEq4_CVD      (vp, -RX, yBot, zP[3], EqPalette[3], "④ CVD 증착기\n박막·증착 공정 (Thin Film Deposition)", cl, ha);
            AddVibSensor(vp, -0.88, yBot+0.70, zP[3]+0.05, cl, ha);  // H1+H2=0.70

            // ─ 우벽 ⑤~⑧ ─────────────────────────────────────────────
            AddEq5_Implanter(vp, +RX, yBot, zP[3], EqPalette[4], "⑤ 이온주입기\n금속 배선 공정 (Metal Wiring)",         cl, ha);
            AddVibSensor(vp, +0.88, yBot+0.60, zP[3]-0.27, cl, ha);  // 소스 섹션 topH=0.60

            AddEq6_CMP      (vp, +RX, yBot, zP[2], EqPalette[5], "⑥ CMP\n배선 공정 (Interconnect)",                    cl, ha);
            AddVibSensor(vp, +0.87, yBot+0.64, zP[2]-0.06, cl, ha);  // H1+H2=0.64

            AddEq7_PVD      (vp, +RX, yBot, zP[1], EqPalette[6], "⑦ PVD 스퍼터\nEDS 공정 (Electrical Die Sorting)",    cl, ha);
            AddVibSensor(vp, +0.89, yBot+0.68, zP[1]-0.05, cl, ha);  // H_b+H_t=0.68 (타워 중심)

            AddEq8_WetBench (vp, +RX, yBot, zP[0], EqPalette[7], "⑧ 세정기\n패키징 공정 (Packaging)",                  cl, ha);
            AddVibSensor(vp, +0.88, yBot+0.36, zP[0]-0.32, cl, ha);  // H_bench=0.36, 벤치 윗면 좌측
        }

        // ── 진동 센서 (원통형, 장비 윗면 수직 배치) ─────────────────
        // sx=장비중심X, sy=장비top Y, sz=장비중심Z
        private static void AddVibSensor(HelixViewport3D vp,
            double sx, double sy, double sz,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            const double R_th=0.006, L_th=0.005;   // 나사 부분
            const double R_bd=0.012, L_bd=0.026;   // 본체
            const double R_cp=0.010, L_cp=0.008;   // 상단 캡
            double y0=sy, y1=y0+L_th, y2=y1+L_bd, y3=y2+L_cp;
            var metal = Color.FromRgb(0xC8, 0xC8, 0xD8);
            var black = Color.FromRgb(0x28, 0x28, 0x38);
            int sg=10;

            void Cir(LinesVisual3D L, double y, double r) {
                for (int i=0; i<sg; i++) {
                    double a0=2*Math.PI*i/sg, a1=2*Math.PI*(i+1)/sg;
                    L.Points.Add(new Point3D(sx+r*Math.Cos(a0), y, sz+r*Math.Sin(a0)));
                    L.Points.Add(new Point3D(sx+r*Math.Cos(a1), y, sz+r*Math.Sin(a1)));
                }
            }
            void Sid(LinesVisual3D L, double ya, double yb, double r) {
                for (int i=0; i<6; i++) {
                    double a=2*Math.PI*i/6;
                    L.Points.Add(new Point3D(sx+r*Math.Cos(a), ya, sz+r*Math.Sin(a)));
                    L.Points.Add(new Point3D(sx+r*Math.Cos(a), yb, sz+r*Math.Sin(a)));
                }
            }

            var tL = new LinesVisual3D { Color = metal, Thickness = 0.8 };
            Cir(tL, y0, R_th); Cir(tL, y1, R_th); Sid(tL, y0, y1, R_th);
            vp.Children.Add(tL);

            var bL = new LinesVisual3D { Color = metal, Thickness = 1.1 };
            Cir(bL, y1, R_bd); Cir(bL, y2, R_bd); Sid(bL, y1, y2, R_bd);
            vp.Children.Add(bL);

            var cL = new LinesVisual3D { Color = black, Thickness = 1.1 };
            Cir(cL, y2, R_cp); Cir(cL, y3, R_cp); Sid(cL, y2, y3, R_cp);
            vp.Children.Add(cL);

            // 솔리드 채움
            FillCylY(vp, y0, y1, sx, sz, R_th, Color.FromArgb(255, metal.R, metal.G, metal.B));
            FillCylY(vp, y1, y2, sx, sz, R_bd, Color.FromArgb(255, metal.R, metal.G, metal.B));
            FillCylY(vp, y2, y3, sx, sz, R_cp, Color.FromArgb(255, black.R, black.G, black.B));

            // 호버 프록시
            var proxy = new BoxVisual3D {
                Center = new Point3D(sx, (y0+y3)/2, sz),
                Width  = R_bd*2+0.01, Height = (y3-y0)+0.01, Length = R_bd*2+0.01,
                Fill   = new SolidColorBrush(Color.FromArgb(2, 255,255,255))
            };
            vp.Children.Add(proxy);
            cl[proxy] = "진동 센서\n설비 진동 모니터링 (Vibration Sensor)";
            ha[proxy] = (
                () => { bL.Color = Color.FromRgb(0xFF,0xFF,0x88); cL.Color = Color.FromRgb(0xFF,0xFF,0x88); },
                () => { bL.Color = metal; cL.Color = black; }
            );
        }

        // Y축 원통 솔리드 메쉬 채움 (수직 원통)
        private static void FillCylY(HelixViewport3D vp,
            double ya, double yb, double cx, double cz, double r, Color col)
        {
            if (ya > yb) { double tmp=ya; ya=yb; yb=tmp; }
            int sg = 10;
            var pos = new Point3DCollection();
            var idx = new Int32Collection();
            pos.Add(new Point3D(cx, ya, cz)); // 0: 하단 중심
            pos.Add(new Point3D(cx, yb, cz)); // 1: 상단 중심
            for (int i=0; i<sg; i++) {
                double a=2*Math.PI*i/sg;
                pos.Add(new Point3D(cx+r*Math.Cos(a), ya, cz+r*Math.Sin(a))); // 2+2i
                pos.Add(new Point3D(cx+r*Math.Cos(a), yb, cz+r*Math.Sin(a))); // 3+2i
            }
            for (int i=0; i<sg; i++) {
                int ba=2+2*i, bb=2+2*((i+1)%sg);
                int ta=3+2*i, tb=3+2*((i+1)%sg);
                // 하단 캡
                idx.Add(0); idx.Add(ba); idx.Add(bb);
                idx.Add(0); idx.Add(bb); idx.Add(ba);
                // 상단 캡
                idx.Add(1); idx.Add(tb); idx.Add(ta);
                idx.Add(1); idx.Add(ta); idx.Add(tb);
                // 측면
                idx.Add(ba); idx.Add(ta); idx.Add(bb);
                idx.Add(bb); idx.Add(ta); idx.Add(tb);
                idx.Add(ba); idx.Add(bb); idx.Add(ta);
                idx.Add(bb); idx.Add(tb); idx.Add(ta);
            }
            var mesh = new MeshGeometry3D { Positions=pos, TriangleIndices=idx };
            var mat  = new EmissiveMaterial(new SolidColorBrush(col));
            vp.Children.Add(new ModelVisual3D {
                Content = new GeometryModel3D { Geometry=mesh, Material=mat, BackMaterial=mat }
            });
        }

        // 장비 박스 공통 (12-edge wireframe + 메인 박스는 반투명 채움)
        private static LinesVisual3D AddEquipBox(HelixViewport3D vp,
            double x0, double x1, double y0, double y1, double z0, double z1,
            Color col, double thick = 1.5)
        {
            // 메인 박스(thick≥1.5)에만 반투명 솔리드 채움 적용
            if (thick >= 1.5)
                FillBox(vp, x0, x1, y0, y1, z0, z1,
                    Color.FromArgb(255, col.R, col.G, col.B));

            var L = new LinesVisual3D { Color = col, Thickness = thick };
            Seg(L, x0,y0,z0, x1,y0,z0); Seg(L, x1,y0,z0, x1,y0,z1);
            Seg(L, x1,y0,z1, x0,y0,z1); Seg(L, x0,y0,z1, x0,y0,z0);
            Seg(L, x0,y1,z0, x1,y1,z0); Seg(L, x1,y1,z0, x1,y1,z1);
            Seg(L, x1,y1,z1, x0,y1,z1); Seg(L, x0,y1,z1, x0,y1,z0);
            Seg(L, x0,y0,z0, x0,y1,z0); Seg(L, x1,y0,z0, x1,y1,z0);
            Seg(L, x1,y0,z1, x1,y1,z1); Seg(L, x0,y0,z1, x0,y1,z1);
            vp.Children.Add(L);
            return L;
        }

        // 반투명 6면체 솔리드 채움
        private static void FillBox(HelixViewport3D vp,
            double x0, double x1, double y0, double y1, double z0, double z1, Color col)
        {
            var pos = new Point3DCollection();
            var idx = new Int32Collection();

            void Quad(Point3D p0, Point3D p1, Point3D p2, Point3D p3) {
                int i = pos.Count;
                pos.Add(p0); pos.Add(p1); pos.Add(p2); pos.Add(p3);
                // 앞면
                idx.Add(i); idx.Add(i+1); idx.Add(i+2);
                idx.Add(i); idx.Add(i+2); idx.Add(i+3);
                // 뒷면 (양면 렌더링)
                idx.Add(i+2); idx.Add(i+1); idx.Add(i);
                idx.Add(i+3); idx.Add(i+2); idx.Add(i);
            }

            Quad(new(x0,y0,z0),new(x1,y0,z0),new(x1,y0,z1),new(x0,y0,z1)); // 바닥
            Quad(new(x0,y1,z0),new(x0,y1,z1),new(x1,y1,z1),new(x1,y1,z0)); // 상단
            Quad(new(x0,y0,z0),new(x0,y1,z0),new(x1,y1,z0),new(x1,y0,z0)); // 앞면 z0
            Quad(new(x0,y0,z1),new(x1,y0,z1),new(x1,y1,z1),new(x0,y1,z1)); // 뒷면 z1
            Quad(new(x0,y0,z0),new(x0,y0,z1),new(x0,y1,z1),new(x0,y1,z0)); // 좌측 x0
            Quad(new(x1,y0,z0),new(x1,y1,z0),new(x1,y1,z1),new(x1,y0,z1)); // 우측 x1

            var mesh = new MeshGeometry3D { Positions = pos, TriangleIndices = idx };
            var mat  = new EmissiveMaterial(new SolidColorBrush(col));
            vp.Children.Add(new ModelVisual3D {
                Content = new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = mat }
            });
        }

        // 장비 프록시 + 호버 등록 (단일 프레임)
        private static void EquipProxy(HelixViewport3D vp,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            double cx, double cy, double cz, double w, double h, double d,
            string label, LinesVisual3D frame, Color orig)
            => EquipProxy(vp, cl, ha, cx, cy, cz, w, h, d, label, new[] { frame }, orig);

        // 장비 프록시 + 호버 등록 (복합 형상 — 모든 프레임 동시 하이라이트)
        private static void EquipProxy(HelixViewport3D vp,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            double cx, double cy, double cz, double w, double h, double d,
            string label, LinesVisual3D[] frames, Color orig)
        {
            var proxy = new BoxVisual3D {
                Center = new Point3D(cx, cy, cz),
                Width  = w + 0.02, Height = h + 0.02, Length = d + 0.02,
                Fill   = new SolidColorBrush(Color.FromArgb(2, 255, 255, 255))
            };
            vp.Children.Add(proxy);
            cl[proxy] = label;
            var oc = orig;
            var hi = Color.FromRgb(0xFF, 0xFF, 0x88);
            ha[proxy] = (
                () => { foreach (var f in frames) f.Color = hi; },
                () => { foreach (var f in frames) f.Color = oc; }
            );
        }

        // 벽면별 x 범위 및 전면 x 계산 헬퍼
        private static (double x0, double x1, double frontX) EqBounds(double wallX, double eqW)
        {
            double fx = wallX + (wallX < 0 ? eqW : -eqW);
            return (Math.Min(wallX, fx), Math.Max(wallX, fx), fx);
        }

        // ─── 장비별 전용 3D 형상 메서드 ────────────────────────────────────

        // ① 산화로 — 넓고 낮은 수평 퍼니스 + 우측 상단 제어 모듈
        private static void AddEq1_Furnace(HelixViewport3D vp, double wallX, double yBot, double cz,
            Color col, string label,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            const double W=0.24, H1=0.46, D1=0.70;   // 넓고 낮은 메인 바디
            const double H2=0.20, D2=0.26;            // 우측 상단 제어 모듈
            var (x0,x1,fx) = EqBounds(wallX, W);
            double z0=cz-D1/2, z1=cz+D1/2;
            var f1 = AddEquipBox(vp, x0, x1, yBot, yBot+H1, z0, z1, col);
            var f2 = AddEquipBox(vp, x0, x1, yBot+H1, yBot+H1+H2, z1-D2, z1, col);
            // 수평 튜브 레일 선 (앞면)
            var d = new LinesVisual3D { Color = col, Thickness = 1.0 };
            for (int r=0; r<3; r++) {
                double ry = yBot + H1*0.28 + r*(H1*0.22);
                Seg(d, fx, ry, z0+0.04, fx, ry, z1-D2-0.02);
            }
            vp.Children.Add(d);
            EquipProxy(vp, cl, ha, (x0+x1)/2, yBot+(H1+H2)/2, cz, W, H1+H2, D1, label, new[]{f1,f2}, col);
        }

        // ② 포토리소그래피 — 넓은 하단 트랙 베이스 + 좁은 상단 스테퍼 컬럼
        private static void AddEq2_Photo(HelixViewport3D vp, double wallX, double yBot, double cz,
            Color col, string label,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            const double W=0.24, H1=0.26, D1=0.62;   // 하단 트랙 베이스
            const double H2=0.42, D2=0.24;            // 상단 스테퍼 렌즈 컬럼
            var (x0,x1,fx) = EqBounds(wallX, W);
            var f1 = AddEquipBox(vp, x0, x1, yBot, yBot+H1, cz-D1/2, cz+D1/2, col);
            var f2 = AddEquipBox(vp, x0, x1, yBot+H1, yBot+H1+H2, cz-D2/2, cz+D2/2, col);
            // 트랙 모듈 구분선 (앞면)
            var d = new LinesVisual3D { Color = col, Thickness = 1.0 };
            foreach (double tz in new[]{cz-D1*0.20, cz+D1*0.20})
                Seg(d, fx, yBot+0.03, tz, fx, yBot+H1-0.03, tz);
            vp.Children.Add(d);
            EquipProxy(vp, cl, ha, (x0+x1)/2, yBot+(H1+H2)/2, cz, W, H1+H2, D1, label, new[]{f1,f2}, col);
        }

        // ③ 식각기 (RIE) — 하단 좁은 콘솔 + 상단 정사각형 챔버(원형 도어)
        private static void AddEq3_Etcher(HelixViewport3D vp, double wallX, double yBot, double cz,
            Color col, string label,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            const double W=0.26, H1=0.16, D1=0.36;   // 하단 컨트롤 콘솔
            const double H2=0.52, D2=0.52;            // 메인 챔버 (정사각형 단면)
            var (x0,x1,fx) = EqBounds(wallX, W);
            var f1 = AddEquipBox(vp, x0, x1, yBot, yBot+H1, cz-D1/2, cz+D1/2, col);
            var f2 = AddEquipBox(vp, x0, x1, yBot+H1, yBot+H1+H2, cz-D2/2, cz+D2/2, col);
            // 챔버 도어 원 (앞면)
            var d = new LinesVisual3D { Color = col, Thickness = 1.0 };
            double cr=D2*0.32, ccy=yBot+H1+H2*0.52;
            for (int i=0; i<16; i++) {
                double a0=2*Math.PI*i/16, a1=2*Math.PI*(i+1)/16;
                d.Points.Add(new Point3D(fx, ccy+cr*Math.Sin(a0), cz+cr*Math.Cos(a0)));
                d.Points.Add(new Point3D(fx, ccy+cr*Math.Sin(a1), cz+cr*Math.Cos(a1)));
            }
            // 뷰포트 소원
            double vr=cr*0.30;
            for (int i=0; i<10; i++) {
                double a0=2*Math.PI*i/10, a1=2*Math.PI*(i+1)/10;
                d.Points.Add(new Point3D(fx, ccy+vr*Math.Sin(a0), cz+vr*Math.Cos(a0)));
                d.Points.Add(new Point3D(fx, ccy+vr*Math.Sin(a1), cz+vr*Math.Cos(a1)));
            }
            vp.Children.Add(d);
            EquipProxy(vp, cl, ha, (x0+x1)/2, yBot+(H1+H2)/2, cz, W, H1+H2, D2, label, new[]{f1,f2}, col);
        }

        // ④ CVD 증착기 — 넓은 하단 가스 캐비닛 + 좁고 키 큰 반응로
        private static void AddEq4_CVD(HelixViewport3D vp, double wallX, double yBot, double cz,
            Color col, string label,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            const double W=0.24, H1=0.20, D1=0.60;   // 하단 가스 캐비닛 (넓음)
            const double H2=0.50, D2=0.34;            // 상단 반응로 (좁고 키 큼)
            var (x0,x1,fx) = EqBounds(wallX, W);
            var f1 = AddEquipBox(vp, x0, x1, yBot, yBot+H1, cz-D1/2, cz+D1/2, col);
            var f2 = AddEquipBox(vp, x0, x1, yBot+H1, yBot+H1+H2, cz-D2/2, cz+D2/2, col);
            // 반응로 타원 (앞면)
            var d = new LinesVisual3D { Color = col, Thickness = 1.0 };
            double rcy=yBot+H1+H2*0.52, rw=D2*0.38, rh=H2*0.28;
            for (int i=0; i<14; i++) {
                double a0=2*Math.PI*i/14, a1=2*Math.PI*(i+1)/14;
                Seg(d, fx, rcy+rh*Math.Sin(a0), cz+rw*Math.Cos(a0),
                       fx, rcy+rh*Math.Sin(a1), cz+rw*Math.Cos(a1));
            }
            // 가스 인렛 소원 3개 (캐비닛 앞면)
            double gpr=D1*0.05;
            foreach (double tz in new[]{cz-D1*0.25, cz, cz+D1*0.25})
                for (int i=0; i<8; i++) {
                    double a0=2*Math.PI*i/8, a1=2*Math.PI*(i+1)/8;
                    d.Points.Add(new Point3D(fx, yBot+H1*0.55+gpr*Math.Sin(a0), tz+gpr*Math.Cos(a0)));
                    d.Points.Add(new Point3D(fx, yBot+H1*0.55+gpr*Math.Sin(a1), tz+gpr*Math.Cos(a1)));
                }
            vp.Children.Add(d);
            EquipProxy(vp, cl, ha, (x0+x1)/2, yBot+(H1+H2)/2, cz, W, H1+H2, D1, label, new[]{f1,f2}, col);
        }

        // ⑤ 이온주입기 — 3섹션 계단형 (소스탑 / 분석기 / 엔드스테이션)
        private static void AddEq5_Implanter(HelixViewport3D vp, double wallX, double yBot, double cz,
            Color col, string label,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            const double W=0.24;
            const double D_s=0.26, H_s=0.60;  // 소스 (가장 높음)
            const double D_a=0.24, H_a=0.40;  // 분석기
            const double D_e=0.30, H_e=0.50;  // 엔드스테이션
            double totalD = D_s+D_a+D_e;
            double zS0=cz-totalD/2, zS1=zS0+D_s;
            double zA0=zS1,         zA1=zA0+D_a;
            double zE0=zA1,         zE1=zA0+D_a+D_e;
            var (x0,x1,_) = EqBounds(wallX, W);
            var f1 = AddEquipBox(vp, x0, x1, yBot, yBot+H_s, zS0, zS1, col);
            var f2 = AddEquipBox(vp, x0, x1, yBot, yBot+H_a, zA0, zA1, col);
            var f3 = AddEquipBox(vp, x0, x1, yBot, yBot+H_e, zE0, zE1, col);
            double maxH = Math.Max(H_s, Math.Max(H_a, H_e));
            EquipProxy(vp, cl, ha, (x0+x1)/2, yBot+maxH/2, cz, W, maxH, totalD, label, new[]{f1,f2,f3}, col);
        }

        // ⑥ CMP — 넓은 베이스 + 중앙 상단 연마 유닛 (플래튼 원)
        private static void AddEq6_CMP(HelixViewport3D vp, double wallX, double yBot, double cz,
            Color col, string label,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            const double W=0.26, H1=0.30, D1=0.72;   // 넓은 베이스
            const double H2=0.34, D2=0.38;            // 중앙 연마 유닛
            var (x0,x1,fx) = EqBounds(wallX, W);
            var f1 = AddEquipBox(vp, x0, x1, yBot, yBot+H1, cz-D1/2, cz+D1/2, col);
            var f2 = AddEquipBox(vp, x0, x1, yBot+H1, yBot+H1+H2, cz-D2/2, cz+D2/2, col);
            // 플래튼 원 (앞면)
            var d = new LinesVisual3D { Color = col, Thickness = 1.0 };
            double pr=D2*0.34, pcy=yBot+H1+H2*0.50;
            for (int i=0; i<14; i++) {
                double a0=2*Math.PI*i/14, a1=2*Math.PI*(i+1)/14;
                d.Points.Add(new Point3D(fx, pcy+pr*Math.Sin(a0), cz+pr*Math.Cos(a0)));
                d.Points.Add(new Point3D(fx, pcy+pr*Math.Sin(a1), cz+pr*Math.Cos(a1)));
            }
            vp.Children.Add(d);
            EquipProxy(vp, cl, ha, (x0+x1)/2, yBot+(H1+H2)/2, cz, W, H1+H2, D1, label, new[]{f1,f2}, col);
        }

        // ⑦ PVD 스퍼터 — 넓은 베이스 + 좁고 키 큰 타워
        private static void AddEq7_PVD(HelixViewport3D vp, double wallX, double yBot, double cz,
            Color col, string label,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            const double W_b=0.28, H_b=0.14, D_b=0.58;   // 넓은 베이스
            const double W_t=0.22, H_t=0.54, D_t=0.36;   // 좁은 타워
            var (bx0,bx1,_) = EqBounds(wallX, W_b);
            var (tx0,tx1,fx) = EqBounds(wallX, W_t);
            var f1 = AddEquipBox(vp, bx0, bx1, yBot, yBot+H_b, cz-D_b/2, cz+D_b/2, col);
            var f2 = AddEquipBox(vp, tx0, tx1, yBot+H_b, yBot+H_b+H_t, cz-D_t/2, cz+D_t/2, col);
            // 챔버 뷰포트 원 (앞면)
            var d = new LinesVisual3D { Color = col, Thickness = 1.0 };
            double vr=D_t*0.23, vcy=yBot+H_b+H_t*0.60;
            for (int i=0; i<12; i++) {
                double a0=2*Math.PI*i/12, a1=2*Math.PI*(i+1)/12;
                d.Points.Add(new Point3D(fx, vcy+vr*Math.Sin(a0), cz+vr*Math.Cos(a0)));
                d.Points.Add(new Point3D(fx, vcy+vr*Math.Sin(a1), cz+vr*Math.Cos(a1)));
            }
            vp.Children.Add(d);
            double maxW = Math.Max(W_b, W_t);
            EquipProxy(vp, cl, ha, (bx0+bx1)/2, yBot+(H_b+H_t)/2, cz, maxW, H_b+H_t, D_b, label, new[]{f1,f2}, col);
        }

        // ⑧ 세정기 (Wet Bench) — 매우 넓고 낮은 벤치 + 벽쪽 배기 후드
        private static void AddEq8_WetBench(HelixViewport3D vp, double wallX, double yBot, double cz,
            Color col, string label,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            const double W=0.24, H_bench=0.36, D_bench=0.86;
            const double H_hood=0.22;
            var (x0,x1,fx) = EqBounds(wallX, W);
            double hoodX0 = wallX < 0 ? x0 : x1-W*0.50;
            double hoodX1 = wallX < 0 ? x0+W*0.50 : x1;
            double z0=cz-D_bench/2, z1=cz+D_bench/2;
            var f1 = AddEquipBox(vp, x0, x1, yBot, yBot+H_bench, z0, z1, col);
            var f2 = AddEquipBox(vp, hoodX0, hoodX1, yBot+H_bench, yBot+H_bench+H_hood, z0, z1, col);
            // 조(槽) 구분선 3개 (앞면)
            var d = new LinesVisual3D { Color = col, Thickness = 1.0 };
            foreach (double tz in new[]{cz-D_bench*0.25, cz, cz+D_bench*0.25})
                Seg(d, fx, yBot+0.03, tz, fx, yBot+H_bench-0.03, tz);
            vp.Children.Add(d);
            EquipProxy(vp, cl, ha, (x0+x1)/2, yBot+(H_bench+H_hood)/2, cz, W, H_bench+H_hood, D_bench, label, new[]{f1,f2}, col);
        }

        // 7-세그먼트 한 자리 (segs: a b c d e f g)
        private static void DrawSeg7(LinesVisual3D L, double x, double cy, double cz,
            double h, bool[] s)
        {
            double w = h * 0.55, g = h * 0.05, hw = w/2, hh = h/2;
            if (s[0]) Seg(L, x, cy+hh,   cz-hw+g, x, cy+hh,   cz+hw-g); // a top
            if (s[1]) Seg(L, x, cy+hh-g, cz+hw,   x, cy+g,    cz+hw);   // b top-right
            if (s[2]) Seg(L, x, cy-g,    cz+hw,   x, cy-hh+g, cz+hw);   // c bot-right
            if (s[3]) Seg(L, x, cy-hh,   cz-hw+g, x, cy-hh,   cz+hw-g); // d bottom
            if (s[4]) Seg(L, x, cy-g,    cz-hw,   x, cy-hh+g, cz-hw);   // e bot-left
            if (s[5]) Seg(L, x, cy+hh-g, cz-hw,   x, cy+g,    cz-hw);   // f top-left
            if (s[6]) Seg(L, x, cy,      cz-hw+g, x, cy,      cz+hw-g); // g middle
        }

        // ── 반투명 수평 패널 ──────────────────────────────────────
        private static void AddPanel(HelixViewport3D vp,
            double cx, double cy, double cz, double w, double d, Color color)
        {
            double hw = w / 2, hd = d / 2;
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection {
                    new Point3D(cx-hw, cy, cz-hd), new Point3D(cx+hw, cy, cz-hd),
                    new Point3D(cx+hw, cy, cz+hd), new Point3D(cx-hw, cy, cz+hd) },
                TriangleIndices = new Int32Collection { 0,1,2, 0,2,3, 0,3,2, 0,2,1 }
            };
            var mat = new EmissiveMaterial(new SolidColorBrush(color));
            vp.Children.Add(new ModelVisual3D
            {
                Content = new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = mat }
            });
        }

        // ── 박스 12 엣지 ─────────────────────────────────────────
        private static void DrawBox(LinesVisual3D L)
        {
            // 하단
            Seg(L,-RX,-RY,-RZ,  RX,-RY,-RZ); Seg(L, RX,-RY,-RZ,  RX,-RY, RZ);
            Seg(L, RX,-RY, RZ, -RX,-RY, RZ); Seg(L,-RX,-RY, RZ, -RX,-RY,-RZ);
            // 상단
            Seg(L,-RX, RY,-RZ,  RX, RY,-RZ); Seg(L, RX, RY,-RZ,  RX, RY, RZ);
            Seg(L, RX, RY, RZ, -RX, RY, RZ); Seg(L,-RX, RY, RZ, -RX, RY,-RZ);
            // 수직
            Seg(L,-RX,-RY,-RZ, -RX, RY,-RZ); Seg(L, RX,-RY,-RZ,  RX, RY,-RZ);
            Seg(L, RX,-RY, RZ,  RX, RY, RZ); Seg(L,-RX,-RY, RZ, -RX, RY, RZ);
        }

        // ── 내부 디테일 ──────────────────────────────────────────
        private static void DrawRoomDetails(LinesVisual3D L)
        {
            // ③ 바닥·천장 내부 테두리
            double ins = 0.07, ix = RX-ins, iz = RZ-ins;
            foreach (double wy in new[] { -RY, RY })
                RectXZ(L, 0, wy, 0, 2*ix, 2*iz);

            // ④ 리턴 에어 그릴 (좌·우 하단)
            double gy = -RY+0.15, gw = 0.28, gh = 0.12;
            foreach (double gz in new[] { -RZ*0.6, -RZ*0.2, RZ*0.2, RZ*0.6 })
            {
                RectYZ(L,-RX, gy, gz, gh, gw); // 좌벽
                RectYZ(L, RX, gy, gz, gh, gw); // 우벽
            }
        }

        // ── 에어샤워 챔버 ─────────────────────────────────────────
        // ── 에어샤워 호버 프록시 ─────────────────────────────────────
        private static void AddAirShowerProxy(HelixViewport3D vp,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            LinesVisual3D door, LinesVisual3D nozzle)
        {
            const double dw = RX*0.38, dh = RY*1.70, depth = 0.55;
            double cy = -RY + dh/2, cz = RZ + depth/2;
            var proxy = new BoxVisual3D {
                Center = new Point3D(0, cy, cz),
                Width  = 2*dw + 0.04,
                Height = dh  + 0.04,
                Length = depth + 0.04,
                Fill   = new SolidColorBrush(Color.FromArgb(2, 255, 255, 255))
            };
            vp.Children.Add(proxy);
            cl[proxy] = "에어샤워\n공기 정화 구역 (Air Shower)";
            var origDoor   = door.Color;
            var origNozzle = nozzle.Color;
            var hi = Color.FromRgb(0xFF, 0xFF, 0x88);
            ha[proxy] = (
                () => { door.Color = hi; nozzle.Color = hi; },
                () => { door.Color = origDoor; nozzle.Color = origNozzle; }
            );
        }

        // ── HEPA 필터 (후면 벽 좌측 상단 부착형) ──────────────────
        private static void AddHEPAFilter(HelixViewport3D vp,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            // 위치: 후면 벽(z=-RZ)에 수직 부착, 좌측(x=-0.55), 상단(y=0.55~0.95)
            const double FW=0.52, FH=0.40, FD=0.12;   // 벽 부착형 — 넓고 얇음
            double fcx = -RX*0.55;
            double fcy = RY - FH/2 - 0.05;             // 천장 바로 아래 상단
            double fz0 = -RZ, fz1 = -RZ + FD;
            double fx0 = fcx-FW/2, fx1 = fcx+FW/2;
            double fy0 = fcy-FH/2, fy1 = fcy+FH/2;

            var col = Color.FromRgb(0xB0, 0xC8, 0xFF);

            // 하우징 박스
            var frame = AddEquipBox(vp, fx0, fx1, fy0, fy1, fz0, fz1, col);

            // 필터면 격자 (전면 노출면 z=fz1, 룸 안쪽을 향함)
            var mesh = new LinesVisual3D { Color = col, Thickness = 0.7 };
            const int GX = 7, GY = 5;
            for (int i = 0; i <= GX; i++) {
                double x = fx0 + FW*i/GX;
                Seg(mesh, x, fy0, fz1, x, fy1, fz1);
            }
            for (int i = 0; i <= GY; i++) {
                double y = fy0 + FH*i/GY;
                Seg(mesh, fx0, y, fz1, fx1, y, fz1);
            }
            vp.Children.Add(mesh);

            // 클릭·호버 프록시
            var proxy = new BoxVisual3D {
                Center = new Point3D(fcx, fcy, -RZ + FD/2),
                Width  = FW+0.02, Height = FH+0.02, Length = FD+0.02,
                Fill   = new SolidColorBrush(Color.FromArgb(2, 255, 255, 255))
            };
            vp.Children.Add(proxy);
            cl[proxy] = "HEPA 필터\n공기 정화 장치 (HEPA Filter)";
            var oc = col;
            var hi = Color.FromRgb(0xFF, 0xFF, 0x88);
            ha[proxy] = (
                () => { frame.Color = hi; mesh.Color = hi; },
                () => { frame.Color = oc; mesh.Color = oc; }
            );
        }

        private static void DrawAirShower(LinesVisual3D structure,
            LinesVisual3D door, LinesVisual3D nozzle)
        {
            const double dw = RX*0.38, dh = RY*1.70, depth = 0.55;
            double topY = -RY+dh, outerZ = RZ+depth;

            // 챔버 박스
            Seg(structure,-dw,-RY, RZ,    dw,-RY, RZ);
            Seg(structure, dw,-RY, RZ,    dw,-RY, outerZ);
            Seg(structure, dw,-RY, outerZ,-dw,-RY, outerZ);
            Seg(structure,-dw,-RY, outerZ,-dw,-RY, RZ);
            Seg(structure,-dw, topY, RZ,   dw, topY, RZ);
            Seg(structure, dw, topY, RZ,   dw, topY, outerZ);
            Seg(structure, dw, topY, outerZ,-dw, topY, outerZ);
            Seg(structure,-dw, topY, outerZ,-dw, topY, RZ);
            Seg(structure,-dw,-RY, RZ,    -dw, topY, RZ);
            Seg(structure, dw,-RY, RZ,     dw, topY, RZ);
            Seg(structure,-dw,-RY, outerZ,-dw, topY, outerZ);
            Seg(structure, dw,-RY, outerZ, dw, topY, outerZ);

            // 이중 문 프레임
            double fw = dw*0.76, fh = dh*0.88, fTopY = -RY+fh;
            foreach (double dz in new[] { RZ, outerZ })
            {
                Seg(door,-fw,-RY,dz, -fw,fTopY,dz);
                Seg(door, fw,-RY,dz,  fw,fTopY,dz);
                Seg(door,-fw,fTopY,dz, fw,fTopY,dz);
            }

            // 노즐 마커
            double nLen = 0.09;
            foreach (double nz in new[] { RZ+depth*0.28, RZ+depth*0.72 })
                foreach (double ny in new[] { -RY+dh*0.20, -RY+dh*0.52, -RY+dh*0.82 })
                {
                    Seg(nozzle,-dw,ny,nz-nLen/2, -dw+nLen,ny,nz-nLen/2);
                    Seg(nozzle,-dw,ny,nz+nLen/2, -dw+nLen,ny,nz+nLen/2);
                    Seg(nozzle, dw,ny,nz-nLen/2,  dw-nLen,ny,nz-nLen/2);
                    Seg(nozzle, dw,ny,nz+nLen/2,  dw-nLen,ny,nz+nLen/2);
                }
        }

        // ── 선분 / 사각형 헬퍼 ────────────────────────────────────
        private static void Seg(LinesVisual3D L,
            double x1,double y1,double z1, double x2,double y2,double z2)
        {
            L.Points.Add(new Point3D(x1,y1,z1));
            L.Points.Add(new Point3D(x2,y2,z2));
        }

        private static void RectXZ(LinesVisual3D L,
            double cx, double cy, double cz, double w, double d)
        {
            double hw=w/2, hd=d/2;
            Seg(L,cx-hw,cy,cz-hd, cx+hw,cy,cz-hd);
            Seg(L,cx+hw,cy,cz-hd, cx+hw,cy,cz+hd);
            Seg(L,cx+hw,cy,cz+hd, cx-hw,cy,cz+hd);
            Seg(L,cx-hw,cy,cz+hd, cx-hw,cy,cz-hd);
        }

        private static void RectYZ(LinesVisual3D L,
            double cx, double cy, double cz, double h, double d)
        {
            double hh=h/2, hd=d/2;
            Seg(L,cx,cy-hh,cz-hd, cx,cy+hh,cz-hd);
            Seg(L,cx,cy+hh,cz-hd, cx,cy+hh,cz+hd);
            Seg(L,cx,cy+hh,cz+hd, cx,cy-hh,cz+hd);
            Seg(L,cx,cy-hh,cz+hd, cx,cy-hh,cz-hd);
        }

        // ── 시점 설정 ────────────────────────────────────────────
        private void SetView(PerspectiveCamera cam, int i)
        {
            cam.Position      = _views[i].pos;
            cam.LookDirection = _views[i].dir;
            cam.UpDirection   = _views[i].up;
        }

        private void btnView1_Iso_Click  (object s, RoutedEventArgs e) => SetView(_cam1, 0);
        private void btnView1_Front_Click(object s, RoutedEventArgs e) => SetView(_cam1, 1);
        private void btnView1_Side_Click (object s, RoutedEventArgs e) => SetView(_cam1, 2);
        private void btnView1_Top_Click  (object s, RoutedEventArgs e) => SetView(_cam1, 3);
        private void btnView2_Iso_Click  (object s, RoutedEventArgs e) => SetView(_cam2, 0);
        private void btnView2_Front_Click(object s, RoutedEventArgs e) => SetView(_cam2, 1);
        private void btnView2_Side_Click (object s, RoutedEventArgs e) => SetView(_cam2, 2);
        private void btnView2_Top_Click  (object s, RoutedEventArgs e) => SetView(_cam2, 3);

        // ── 스페이스 + 좌클릭 패닝 ───────────────────────────────
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) { Cursor = Cursors.SizeAll; e.Handled = true; }
        }

        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                Cursor = Cursors.Arrow;
                if (_isPanning)
                {
                    _isPanning = false;
                    _panCam    = null;
                    // LMB가 아직 눌려 있으면 캡처를 유지 → HelixToolkit이 마우스 이벤트를
                    // 받아 SetView 위치로 복원하는 것을 방지.
                    // 캡처는 PreviewMouseLeftButtonUp에서 해제.
                    if (Mouse.LeftButton != MouseButtonState.Pressed)
                        Mouse.Capture(null);
                }
                FreezeCamera(_cam1);
                FreezeCamera(_cam2);
            }
        }

        private static void FreezeCamera(PerspectiveCamera cam)
        {
            if (cam == null) return;
            // 현재 애니메이션된 값을 읽은 뒤 애니메이션 제거 → 그 자리에 고정
            var pos  = cam.Position;
            var look = cam.LookDirection;
            var up   = cam.UpDirection;
            cam.BeginAnimation(PerspectiveCamera.PositionProperty,      null);
            cam.BeginAnimation(PerspectiveCamera.LookDirectionProperty, null);
            cam.BeginAnimation(PerspectiveCamera.UpDirectionProperty,   null);
            cam.Position      = pos;
            cam.LookDirection = look;
            cam.UpDirection   = up;
        }

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            bool space       = Keyboard.IsKeyDown(Key.Space);
            bool ctrl        = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            bool over1       = IsOverViewport(viewport1);
            bool over2       = IsOverViewport(viewport2);
            bool overViewport = over1 || over2;

            // 뷰포트 밖(버튼 등)이면 아무것도 하지 않음
            if (!overViewport) return;

            if (space)
            {
                // 스페이스+좌클릭 → 패닝
                _panCam    = over1 ? _cam1 : _cam2;
                // 패닝 시작 즉시 HelixToolkit 애니메이션을 끊어
                // base value 를 현재 위치로 고정 → Space 해제 시 되돌아가지 않음
                FreezeCamera(_panCam);
                _isPanning = true;
                _panLast   = e.GetPosition(this);
                Mouse.Capture(this);
                e.Handled = true;
            }
            else if (ctrl)
            {
                // Ctrl+좌클릭 → 궤도 회전
                _orbitCam   = over1 ? _cam1 : _cam2;
                _isOrbiting = true;
                _orbitLast  = e.GetPosition(this);
                Mouse.Capture(this);
                e.Handled = true;
            }
            else
            {
                // 뷰포트 위 일반 좌클릭 → hit test 후 orbit 차단
                var vp    = over1 ? viewport1 : viewport2;
                var dict  = over1 ? _clickables1 : _clickables2;
                var panel = over1 ? infoPanel1 : infoPanel2;
                var text  = over1 ? infoText1 : infoText2;

                var process = over1 ? infoProcess1 : infoProcess2;
                var pos  = e.GetPosition(vp);
                var hits = Viewport3DHelper.FindHits(vp.Viewport, pos);
                bool found = false;
                foreach (var hit in hits)
                {
                    if (hit.Visual is Visual3D v3d && dict.TryGetValue(v3d, out var label))
                    {
                        var parts = label.Split('\n');
                        text.Text        = parts[0];
                        panel.Visibility = Visibility.Visible;
                        if (parts.Length > 1)
                        {
                            process.Text       = parts[1];
                            process.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            process.Visibility = Visibility.Collapsed;
                        }
                        found = true;
                        break;
                    }
                }
                if (!found) panel.Visibility = Visibility.Collapsed;

                e.Handled = true;
            }
        }

        private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            Point  cur = e.GetPosition(this);

            if (_isPanning && _panCam != null)
            {
                double dx = cur.X - _panLast.X;
                double dy = cur.Y - _panLast.Y;
                _panLast = cur;

                Vector3D look  = _panCam.LookDirection; look.Normalize();
                Vector3D up    = _panCam.UpDirection;
                Vector3D right = Vector3D.CrossProduct(look, up); right.Normalize();
                Vector3D camUp = Vector3D.CrossProduct(right, look); camUp.Normalize();

                _panCam.Position += -right * dx * 0.006 + camUp * dy * 0.006;
            }
            else if (_isOrbiting && _orbitCam != null)
            {
                double dx = cur.X - _orbitLast.X;
                double dy = cur.Y - _orbitLast.Y;
                _orbitLast = cur;
                OrbitCamera(_orbitCam, dx, dy);
            }

            // 호버 하이라이트 (패닝·회전 중이 아닐 때만)
            if (!_isPanning && !_isOrbiting)
            {
                DoHoverHighlight(viewport1, _hoverActions1, ref _hovered1, ref _restoreHover1, e);
                DoHoverHighlight(viewport2, _hoverActions2, ref _hovered2, ref _restoreHover2, e);
            }
        }

        private static void OrbitCamera(PerspectiveCamera cam, double dx, double dy)
        {
            double x = cam.Position.X;
            double y = cam.Position.Y;
            double z = cam.Position.Z;

            // 구면 좌표 변환
            double r     = Math.Sqrt(x*x + y*y + z*z);
            if (r < 0.001) return;

            double theta = Math.Atan2(x, z);                              // 수평각
            double phi   = Math.Asin(Math.Clamp(y / r, -1.0, 1.0));     // 수직각

            theta += dx * 0.01;
            phi   -= dy * 0.01;
            phi    = Math.Clamp(phi, -Math.PI / 2 + 0.05, Math.PI / 2 - 0.05);

            double nx = r * Math.Cos(phi) * Math.Sin(theta);
            double ny = r * Math.Sin(phi);
            double nz = r * Math.Cos(phi) * Math.Cos(theta);

            cam.Position      = new Point3D(nx, ny, nz);
            cam.LookDirection = new Vector3D(-nx, -ny, -nz);

            // up 벡터: 구면 좌표 미분으로 계산
            double ux = -Math.Sin(phi) * Math.Sin(theta);
            double uy =  Math.Cos(phi);
            double uz = -Math.Sin(phi) * Math.Cos(theta);
            cam.UpDirection = new Vector3D(ux, uy, uz);
        }

        private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                _panCam    = null;
                Mouse.Capture(null);
                // 패닝 종료 시 카메라 고정
                FreezeCamera(_cam1);
                FreezeCamera(_cam2);
            }
            else if (_isOrbiting)
            {
                _isOrbiting = false;
                _orbitCam   = null;
                Mouse.Capture(null);
            }
            else if (Mouse.Captured == this)
            {
                // Space 해제 후 LMB를 유지했다가 이제 놓는 경우
                // HelixToolkit 에 마우스가 넘어가기 전 카메라 고정 후 캡처 해제
                FreezeCamera(_cam1);
                FreezeCamera(_cam2);
                Mouse.Capture(null);
            }
        }

        // ── 호버 하이라이트 헬퍼 ─────────────────────────────────
        private void DoHoverHighlight(HelixViewport3D vp,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> hoverActions,
            ref Visual3D? hovered,
            ref Action? restore,
            MouseEventArgs e)
        {
            if (!IsOverViewport(vp))
            {
                restore?.Invoke(); restore = null; hovered = null;
                return;
            }

            var pos  = e.GetPosition(vp);
            var hits = Viewport3DHelper.FindHits(vp.Viewport, pos);
            Visual3D? newHit = null;
            foreach (var hit in hits)
            {
                if (hit.Visual is Visual3D v && hoverActions.ContainsKey(v))
                { newHit = v; break; }
            }

            if (newHit == hovered) return;

            // 같은 그룹(공유 onHover) 내 이동 → 시각 변화 없이 hovered만 갱신
            if (newHit != null && hovered != null &&
                hoverActions.TryGetValue(newHit,  out var na) &&
                hoverActions.TryGetValue(hovered, out var oa) &&
                ReferenceEquals(na.onHover, oa.onHover))
            {
                hovered = newHit;
                return;
            }

            restore?.Invoke(); restore = null;

            if (newHit != null && hoverActions.TryGetValue(newHit, out var fi))
            {
                fi.onHover();
                restore = fi.onLeave;
            }
            hovered = newHit;
        }

        private static bool IsOverViewport(System.Windows.UIElement el)
        {
            var p = Mouse.GetPosition(el);
            return p.X >= 0 && p.Y >= 0
                && p.X <= el.RenderSize.Width
                && p.Y <= el.RenderSize.Height;
        }

        // ── 로그아웃 / 종료 ──────────────────────────────────────
        private void btnLogout_Click(object sender, RoutedEventArgs e)
        {
            _clockTimer?.Stop();
            new LoginWindow().Show();
            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer?.Stop();
            base.OnClosed(e);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
        private PerspectiveCamera? _panCam;
        private HelixViewport3D?   _panVp;         // 패닝 중인 뷰포트 (Helix 컨트롤 on/off용)
        private Point3D           _trackedPos;     // 패닝 중 직접 추적한 카메라 위치
        private Vector3D          _trackedLook;
        private Vector3D          _trackedUp;

        // Ctrl + 좌클릭 궤도 회전
        private bool              _isOrbiting;
        private Point             _orbitLast;
        private PerspectiveCamera? _orbitCam;

        // 클릭 가능 요소 등록 (Visual3D → 표시 이름)
        private readonly Dictionary<Visual3D, string> _clickables1 = new();
        private readonly Dictionary<Visual3D, string> _clickables2 = new();

        // 호버 하이라이트 — 요소별 onHover/onLeave 액션
        private readonly Dictionary<Visual3D, (Action onHover, Action onLeave)> _hoverActions1 = new();
        private readonly Dictionary<Visual3D, (Action onHover, Action onLeave)> _hoverActions2 = new();
        private Visual3D? _hovered1 = null;
        private Visual3D? _hovered2 = null;
        private Visual3D? _hovered3 = null;
        private Action?        _restoreHover1 = null;
        private Action?        _restoreHover2 = null;
        private Action?        _restoreHover3 = null;
        private long           _lastHoverTick = 0;   // 호버 히트테스트 쓰로틀용 (ms)


        // 방 치수 (반-크기)
        private const double RX = 1.5, RY = 1.4, RZ = 2.0;

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

        // ── 레이어 토글 그룹 ─────────────────────────────────────
        private struct SceneLayers
        {
            public List<Visual3D> Equip, Vib, TH, FFU;
        }

        // ── 작업자 진입 시퀀스 페이즈 ────────────────────────────
        private enum PersonPhase
        {
            CorridorWalk,       // 통로 직진 (X 고정, Z만 이동 → 외부박스 정면)
            AuthWait,           // 통로 끝 정지 — 출입 요청 → 관리자 허용 → 비번 입력 대기
            DoorOpening,        // 자동문 열림 애니메이션
            BoxWait,            // 박스 진입 직후 정지 — 거리센서 조건 대기
            CorridorEntry,      // 외부박스 횡단 → 에어샤워 (X→0)
            AirShowerEntry,     // 에어샤워 2초 정지 (진입)
            WalkToRoom,         // 에어샤워 → 클린룸 입구
            WalkToEquip,        // 다음 장비로 이동 (X·Z 동시)
            WorkingAtEquip,     // 장비 앞 작업 (팔 모션)
            WalkToExitShower,   // 마지막 장비 → 에어샤워 퇴장
            AirShowerExit,      // 에어샤워 2초 정지 (퇴장)
            CorridorExit,       // 에어샤워 → 외부박스 정면 (X→섹터X)
            CorridorExitWalk,   // 통로 직진 복귀 (X 고정)
            CorridorWait,       // 다음 진입까지 랜덤 대기
        }
        private List<Visual3D> _layer1Equip = new(), _layer1Vib = new(),
                               _layer1TH    = new(), _layer1FFU = new();
        private List<Visual3D> _layer2Equip = new(), _layer2Vib = new(),
                               _layer2TH    = new(), _layer2FFU = new();

        // ── 에어샤워 연동 ────────────────────────────────────────
        private const double AirShowerTriggerDist      = 1.0;
        // 에어샤워 블로어 가동 판정 임계치 (MPa)
        private const double AirShowerPressureThreshold  = 0.08; // MPa
        // 거리센서 박스 진입 판정 임계치 (m) — 이 값 이상이면 작업자 추적 시작 → 에어샤워로 이동
        // ADS 미연결 시: BoxWait 10틱(0.5초) 후 시뮬레이션값으로 자동 충족
        private const double DistSensorEnterThreshold    = 3.0;

        private LinesVisual3D? _airNozzle1, _airNozzle2;
        private LinesVisual3D? _airDoor1,   _airDoor2;
        private LinesVisual3D? _airSpray1,  _airSpray2;
        private Color           _nozzleIdleColor1, _nozzleIdleColor2;
        private bool            _airActive1, _airActive2;
        private bool            _prevAirTriggered;
        private bool            _blinkState;

        // ── 출입 인증 / 자동문 ───────────────────────────────────
        private const string    AuthPassword    = "A+클린룸";
        private const double    AuthTriggerZ    = 6.3;   // 작업자가 이 Z에 도달하면 요청 발생
        private const double    AutoDoorOpenX   = 0.42;  // 문 패널 최대 열림 X 오프셋
        private double          _doorOpenOffset = 0.0;   // 현재 열림 량 (0=닫힘, AutoDoorOpenX=열림)
        // 자동문 패널 Transform (각 통로 좌·우 패널, 뷰포트1/2)
        private TranslateTransform3D?[] _doorLeftT1  = new TranslateTransform3D?[3];
        private TranslateTransform3D?[] _doorRightT1 = new TranslateTransform3D?[3];
        private TranslateTransform3D?[] _doorLeftT2  = new TranslateTransform3D?[3];
        private TranslateTransform3D?[] _doorRightT2 = new TranslateTransform3D?[3];
        private DispatcherTimer? _airBlinkTimer;

        private static readonly Color DoorActiveColor  = Color.FromRgb(0xEF, 0x44, 0x44); // 활성 문틀: 빨강
        private static readonly Color SprayColorBright = Color.FromRgb(0xA0, 0xF0, 0xFF); // 분사 밝음
        private static readonly Color SprayColorDim    = Color.FromRgb(0x30, 0x90, 0xB8); // 분사 어두움

        private LinesVisual3D? _distBeam1, _distBeam2;
        private LinesVisual3D? _person1,   _person2;
        private TranslateTransform3D? _personT1, _personT2;

        // 사람 원점 Z (= outerZ + 1.5) — Transform 오프셋 기준
        private const double PersonOriginZ = RZ + 0.55 + 1.5; // 4.05
        private const double PersonMaxDist = 2.9;              // 이 이상이면 건물 밖으로 나감

        // 사람 애니메이션 (CompositionTarget.Rendering 기반 60fps)
        private long   _lastRenderMs  = 0;   // 마지막 프레임 시각 (ms)
        private double _animAccumMs   = 0;   // 누적 미사용 시간 (고정 50ms 스텝)
        private double _personAnimT    = 0.0;           // 작업 사인파용 누적
        private double _personCurrentZ = PersonOriginZ; // 현재 실제 Z (생성자에서 startZ로 갱신)
        private PersonPhase _personPhase      = PersonPhase.CorridorWalk;
        private int         _personPhaseCount = 0;

        // ── 3개 섹터 (각 통로 안쪽 끝 = 작업자 출발점) ──────────
        // X: 정면 3개 통로 중심, Z: 통로 끝 (외부박스 Z=5.5 + 통로 4.0m)
        private static readonly (double x, double z)[] Sectors =
        {
            (-2.2, 8.5),   // ① 좌측 통로 끝
            ( 0.0, 8.5),   // ② 중앙 통로 끝
            ( 2.2, 8.5),   // ③ 우측 통로 끝
        };

        private static readonly Random _personRng = new();
        private int    _personSectorIdx = 0; // 현재 선택된 섹터
        private double _personStartZ;
        private double _personStartX;
        private double _personCurrentX = 0;  // 현재 실제 X
        private double _walkSpeed1;           // CR1 작업자 개인 걸음 속도 (랜덤 초기화)
        private double _walkSpeed3;           // CR3 작업자 개인 걸음 속도 (랜덤 초기화)
        private int    _nextWorkTicks;
        private int    _nextWaitTicks;
        // 장비 순회 상태
        private (double x, double z, string label)[] _equipWaypoints = Array.Empty<(double, double, string)>();
        private int _waypointIdx = 0;

        // 진입 시퀀스 파라미터
        private const double OuterFrontZ      = 5.5;              // 외부박스 정면 Z (통로 입구)
        private const double AirShowerMidZ   = RZ + 0.55 * 0.5; // 2.275 (에어샤워 챔버 중앙)
        private const double RoomEntryZ      = 1.5;              // 클린룸 진입 기준 Z (에어샤워 내측)
        private const double PersonWalkSpeed = 0.022;            // units/tick ≈ 0.44 m/s
        private const int    AirShowerWaitTicks = 40;            // 2초 정지
        private const double WorkPeriod      = 2.5;              // 작업 진동 주기 (초)
        // ── 장비 순회 파라미터 ─────────────────────────────────
        private const double EqApproachX    = RX - 0.55;  // 장비 접근 X 좌표 — front face(RX-eqW) 앞 0.31 지점
        private const int    EqWorkMinTick  = 40;    // 장비당 최소 작업 틱 (2초)
        private const int    EqWorkRndTick  = 40;    // 랜덤 추가 (~2초)
        private const int    EqVisitCount   = 4;     // 방문할 장비 수 (8개 중 랜덤 4개)
        // 8개 공정 장비 위치 (X: -=좌벽 +=우벽, Z: zP 순서)
        private static readonly (double x, double z, string label)[] AllEquips =
        {
            (-EqApproachX,  RZ*0.75, "① 산화로"),
            (-EqApproachX,  RZ*0.25, "② 포토리소그래피"),
            (-EqApproachX, -RZ*0.25, "③ 식각기"),
            (-EqApproachX, -RZ*0.75, "④ CVD 증착기"),
            ( EqApproachX, -RZ*0.75, "⑤ 이온주입기"),
            ( EqApproachX, -RZ*0.25, "⑥ CMP"),
            ( EqApproachX,  RZ*0.25, "⑦ PVD 스퍼터"),
            ( EqApproachX,  RZ*0.75, "⑧ 세정기"),
        };

        private static readonly Color PersonIdleColor    = Color.FromRgb(0xB0, 0xCC, 0xFF); // CR1 청백
        private static readonly Color PersonIdleColor2  = Color.FromRgb(0xC8, 0xB0, 0xFF); // CR2 보라계
        private static readonly Color PersonIdleColor3  = Color.FromRgb(0xB0, 0xFF, 0xCC); // CR3 청록 (CR1 clone)
        private static readonly Color PersonTriggerColor = Color.FromRgb(0xFF, 0x60, 0x40); // 에어샤워 적색

        // ── 클린룸2 완전 독립 상태 머신 ─────────────────────────────
        // CR2: 관리자 지시 기반 — 모든 행동은 버튼 클릭이 있어야 시작
        private enum CR2Phase
        {
            Hidden,           // 클린룸 밖 — 입실 지시 대기
            CorridorWalk,     // 섹터 → 문(AuthTriggerZ)까지 이동
            AuthWait,         // 문 앞 정지 — 로그인/2차 인증 대기
            DoorOpening,      // 자동문 열림
            CorridorEntry,    // 자동문 통과 → 에어샤워 입구 진입 후 정지(BoxIdle)
            BoxIdle,          // 에어샤워 입구 정지 — 거리 센서 아이콘 클릭 대기
            AirShowerEntry,   // 에어샤워 추적 이동 + 대기
            WalkToRoom,       // 에어샤워 내측 → 클린룸 진입
            Idle,             // 클린룸 중앙 정지 — 장비/퇴실 지시 대기
            WalkToEquip,      // 지정 장비로 이동
            AtEquip,          // 장비 앞 정지 — 다음 지시 대기
            WalkToExitShower, // 퇴실 → 에어샤워 방향 복귀
            AirShowerExit,    // 에어샤워 대기 (퇴실)
            CorridorExit,     // 에어샤워 → 통로 끝
            CorridorExitWalk, // 통로 끝 → 섹터 복귀 후 Hidden
        }
        private CR2Phase _cr2Phase     = CR2Phase.Hidden;
        private double   _p2X          = 0.0;
        private double   _p2Z          = 0.0;
        private double   _p2AnimT      = 0.0;
        private int      _p2PhaseCount = 0;
        private double   _doorOpenOffset2 = 0.0;
        private bool     _doorClosing2    = false;  // 거리 아이콘 클릭 시 문 닫기 진행 중
        private int      _p2SectorIdx     = 1;   // 고정 섹터 1 (중앙 통로)
        private (double x, double z, string label)? _p2Target = null;

        // 자동회전 / 정지
        private bool _autoRotate1 = false;
        private bool _autoRotate2 = false;
        private bool _autoRotate3 = false;
        private bool _paused1     = false;
        private bool _paused2     = false;
        private bool _paused3     = false;
        private string _cr2WorkerLabel = "";

        // 장비 고장 관리
        // 각 항목: (frames 배열, 원본색)  —  EquipProxy 등록 시 채워짐
        private readonly List<(LinesVisual3D[] frames, Color orig)> _equipReg1 = new();
        private readonly List<(LinesVisual3D[] frames, Color orig)> _equipReg2 = new();
        private readonly List<(LinesVisual3D[] frames, Color orig)> _equipReg3 = new();

        // ── CR3 (= CR1 클론) 독립 상태 머신 ───────────────────────────
        private PersonPhase _personPhase3      = PersonPhase.CorridorWalk;
        private double      _personAnimT3      = 0.0;
        private double      _personCurrentZ3   = PersonOriginZ;
        private double      _personCurrentX3   = 0.0;
        private int         _personPhaseCount3 = 0;
        private int         _personSectorIdx3  = 0;
        private double      _personStartZ3;
        private double      _personStartX3;
        private int         _nextWorkTicks3;
        private int         _nextWaitTicks3;
        private (double x, double z, string label)[] _equipWaypoints3 = Array.Empty<(double, double, string)>();
        private int         _waypointIdx3      = 0;
        private int         _cr3TotalWorkTicks = 0;   // 총 작업 누적 틱 (300 = 15초)
        private double      _doorOpenOffset3   = 0.0;

        private LinesVisual3D? _airNozzle3, _airDoor3, _airSpray3;
        private Color           _nozzleIdleColor3;
        private bool            _airActive3, _prevAirTriggered3;
        private LinesVisual3D?  _distBeam3, _person3;
        private TranslateTransform3D? _personT3;
        private TranslateTransform3D?[] _doorLeftT3  = new TranslateTransform3D?[3];
        private TranslateTransform3D?[] _doorRightT3 = new TranslateTransform3D?[3];

        // ── CR1 두 번째 작업자 (첫 번째가 클린룸 진입 후 10초 뒤 투입) ──────
        private PersonPhase _personPhase1b      = PersonPhase.CorridorWait;
        private double      _personAnimT1b      = 0.0;
        private double      _personCurrentZ1b   = PersonOriginZ;
        private double      _personCurrentX1b   = 0.0;
        private int         _personPhaseCount1b = 0;
        private int         _personSectorIdx1b  = 2;
        private double      _personStartZ1b;
        private double      _personStartX1b;
        private int         _nextWorkTicks1b;
        private int         _nextWaitTicks1b    = int.MaxValue;
        private (double x, double z, string label)[] _equipWaypoints1b = Array.Empty<(double, double, string)>();
        private int         _waypointIdx1b      = 0;
        private double      _doorOpenOffset1b   = 0.0;
        private double      _walkSpeed1b;
        private LinesVisual3D?  _person1b;
        private TranslateTransform3D? _personT1b;
        private int         _cr1bCountdown      = -1;

        // ── CR3 두 번째 작업자 (첫 번째가 클린룸 진입 후 10초 뒤 투입) ──────
        private PersonPhase _personPhase3b      = PersonPhase.CorridorWait;
        private double      _personAnimT3b      = 0.0;
        private double      _personCurrentZ3b   = PersonOriginZ;
        private double      _personCurrentX3b   = 0.0;
        private int         _personPhaseCount3b = 0;
        private int         _personSectorIdx3b  = 1;
        private double      _personStartZ3b;
        private double      _personStartX3b;
        private int         _nextWorkTicks3b;
        private int         _nextWaitTicks3b    = int.MaxValue; // 첫 투입 전까지 무한 대기
        private (double x, double z, string label)[] _equipWaypoints3b = Array.Empty<(double, double, string)>();
        private int         _waypointIdx3b      = 0;
        private double      _doorOpenOffset3b   = 0.0;
        private double      _walkSpeed3b;
        private LinesVisual3D?  _person3b;
        private TranslateTransform3D? _personT3b;
        private int         _cr3bCountdown      = -1; // -1=비활성, >0=카운트다운 중, 0=투입
        private SensorDataService? _sensorService3;
        private (LinesVisual3D? tempDig, LinesVisual3D? humDig, LinesVisual3D? needle) _live3;
        private PerspectiveCamera? _cam3;
        private readonly Dictionary<Visual3D, string> _clickables3 = new();
        private readonly Dictionary<Visual3D, (Action onHover, Action onLeave)> _hoverActions3 = new();
        private List<Visual3D> _layer3Equip = new(), _layer3Vib = new(),
                               _layer3TH    = new(), _layer3FFU = new();
        // ── CR3 장비엔지니어 (온도/압력 위험값 30초 지속 시 투입) ──────
        private PersonPhase _engineerPhase      = PersonPhase.CorridorWait;
        private double      _engineerAnimT      = 0.0;
        private double      _engineerCurrentZ   = PersonOriginZ;
        private double      _engineerCurrentX   = 0.0;
        private int         _engineerPhaseCount = 0;
        private int         _engineerSectorIdx  = 1;
        private double      _engineerStartZ;
        private double      _engineerStartX;
        private double      _doorOpenOffsetEng  = 0.0;
        private double      _walkSpeedEng;
        private LinesVisual3D?  _personEng;
        private TranslateTransform3D? _personTEng;

        private bool        _engineerActive     = false;
        private bool        _engineerTriggerPending = false; // 센서 위험 2회 감지 → 엔지니어 투입 플래그

        // 엔지니어 투입 원인
        private enum EngineerTriggerReason { Vibration, TempHumidity, Pressure }
        private EngineerTriggerReason _engineerTriggerReason = EngineerTriggerReason.Vibration;
        private readonly Queue<(double x, double z, string label)> _engineerRepairQueue = new();

        // FFU / HEPA팬 수리 위치 (후면 하단 벽면 배치 기준) — X는 AddFFUFans의 ffuX 배열과 동일
        private static readonly (double x, double z, string label)[] FfuRepairTargets =
        {
            ( 0.0,         -RZ+0.2, "FFU-①"),
            ( RX*2.0/3.0, -RZ+0.2, "FFU-②"),
            (-RX*2.0/3.0, -RZ+0.2, "HEPA팬"),
        };
        private (double x, double z, string label) _engineerTarget;
        private readonly List<int>  _brokenEquipIndices3 = new();  // 고장 장비 인덱스 복수 추적
        private bool _distTracking3      = false; // 📡거리 추적 활성 여부
        private bool _ffuFailed3        = false;
        private int  _ffuFailedIndex3   = 0;   // 0=FFU-①, 1=FFU-② (고장 시 랜덤 결정)
        private bool _airShowerFailed3 = false;
        private readonly List<(LinesVisual3D line, Color orig)> _ffuOrigColors3 = new();
        private static readonly Color EquipFailColor = Color.FromRgb(0xEF, 0x44, 0x44); // 고장 장비 표시 색
        private int         _engineerWorkTicks  = 80;  // 4초 수리 (센서+장비 복수 방문)
        // 고장 표시 깜빡임 (2초 주기: 1초 ON / 1초 OFF)
        private int  _failBlinkTick = 0;
        private bool _failBlinkOn   = true;
        private const int FailBlinkToggleTicks = 20;  // 20 × 50ms = 1초
        private int  _repairBannerTicks3 = 0;         // 수리 완료 배너 자동 숨김 카운트다운

        // CR1(CR3) 작업자 주도 수리
        private bool   _cr3FaultActive       = false;  // 장비 고장 발생 중
        private string _cr3FaultLabel        = "";     // 고장 장비 이름
        private int    _cr3WorkerRepairTicks  = -1;    // 수리 카운트다운 (-1=비활성)
        private int    _cr3WorkerDoneTicks    = 0;     // 정비 완료 배너 숨김 카운트다운
        private const int CR3WorkerRepairTotal = 100;  // 100 × 50ms = 5초
        // 센서 방문 위치 (엔지니어 수리 경로)
        private static readonly (double x, double z, string label) ThSensorPos   = (0.65, RZ * 0.52, "온습도센서");
        private static readonly (double x, double z, string label) PressSensorPos = (0.65, RZ * 0.12, "압력센서");
        private const double VibDangerHi    = 1.5;   // 진동 위험 임계값 m/s²
        private const double TempDangerHi   = 36.0;  // 온도 위험 임계값 °C
        private const double HumDangerHi    = 46.0;  // 습도 위험 임계값 %RH
        private const double PressDangerLo  = 18.0;  // 압력 위험 임계값 Pa (알람용 유지)
        private static readonly Color EngineerColor = Color.FromRgb(0xFF, 0xCC, 0x00); // 장비엔지니어: 노란색

        // 머리 위 이름 레이블 (Canvas TextBlock 오버레이 — CR1)
        private System.Windows.Controls.TextBlock? _nameText1;
        private System.Windows.Controls.TextBlock? _nameText1b;
        // CR3 — 3D BillboardText (피겨와 그룹화된 3D 레이블)
        private BillboardTextVisual3D? _nameBillboard3;
        private BillboardTextVisual3D? _nameBillboard3b;
        private BillboardTextVisual3D? _nameBillboardEng;
        // CR3 작업자·엔지니어 이름 상수
        private string PersonName3   = "작업자:홍길동";
        private string PersonName3b  = "작업자:이순신";
        private string EngineerName3 = "엔지니어:김철수";

        // 현재 고장 중인 장비: (frames, orig, 복구 예정 시각)
        private readonly List<(LinesVisual3D[] frames, Color orig, DateTime restoreAt)> _faults = new();
        private static readonly Color FaultColor = Color.FromRgb(0xFF, 0x22, 0x22); // 적색 고장
        private static readonly Color WarnColor  = Color.FromRgb(0xFF, 0xAA, 0x00); // 황색 경고
        private static readonly Random _faultRng = new();
        private const double FaultRecoverySec = 30.0; // 30초 후 자동 복구
        private readonly AlarmService _eqAlarmService = new();        // 장비 고장 전용 알람 판정
        private int _cr2DangerCount = 0;   // CR2 위험 알람 누적 (2회 시 장비 고장)

        // ── CR2 공정 장비 이름 (index 순서 = AddFabEquipment 배치 순서) ──
        private static readonly string[] _cr2EquipNames =
        {
            "1번 식각기",   "2번 세정기",  "3번 증착기",   "4번 리소기",
            "5번 CMP기",   "6번 검사기",  "7번 어닐링로",  "8번 이온주입기"
        };

        // ── CR2 장비 고장 / 5초 수리 추적 ────────────────────────────
        public event Action<AlarmRecord>? EquipmentFaultOccurred;
        private LinesVisual3D[]? _cr2FaultFrames    = null;
        private Color            _cr2FaultOrigColor  = Colors.Transparent;
        private AlarmRecord?     _cr2FaultRecord     = null;
        private string           _cr2FaultEquipName  = "";   // 고장난 장비 이름
        private int              _cr2FaultIdx        = -1;   // 고장난 장비 인덱스 (_equipReg2 기준)
        private int              _cr2TargetIdx       = -1;   // 현재 이동 목표 장비 인덱스
        private int              _cr2RepairTicks     = -1;   // -1=idle, >0=카운트다운 중
        private int              _cr2RepairDoneTicks = 0;    // 정비완료 배너 자동숨김
        private const int        CR2RepairTotalTicks = 100;  // 100×50ms = 5초
        // EquipProxy 등록 타깃 — AddFabEquipment 호출 동안만 설정
        private static List<(LinesVisual3D[] frames, Color orig)>? _equipRegistryTarget;
        // OrbitCamera(cam, dx, 0) 기준: theta += dx*0.01 rad/frame
        // → 초당 약 25° 회전: dx/frame = 25*π/180 / 0.01 / fps ≈ 0.727 (at 60fps)
        private const double AutoRotateDxPerSec = 43.6; // 25 deg/s → dx/s = 0.4363/0.01

        // ── 실시간 업데이트 가능 요소 ───────────────────────────
        private SensorDataService? _sensorService1;
        private SensorDataService? _sensorService2;
        private (LinesVisual3D? tempDig, LinesVisual3D? humDig, LinesVisual3D? needle) _live1;
        private (LinesVisual3D? tempDig, LinesVisual3D? humDig, LinesVisual3D? needle) _live2;

        // 시점 프리셋
        private static readonly (Point3D pos, Vector3D dir, Vector3D up)[] _views =
        {
            (new Point3D( 7.0,  4.0, 14.0), new Vector3D(-7.0,-4.0,-14.0), new Vector3D(0,1,0)), // 등각
            (new Point3D( 0,    0,   12  ), new Vector3D( 0,   0,  -1  ), new Vector3D(0,1,0)), // 정면
            (new Point3D(10,    0,    0  ), new Vector3D(-1,   0,   0  ), new Vector3D(0,1,0)), // 측면
            (new Point3D( 0,   12,    0  ), new Vector3D( 0,  -1,   0  ), new Vector3D(0,0,-1)), // 상단
        };

        public AdminWindow(User user)
        {
            InitializeComponent();
            txtAdminName.Text = $"  |  {user.FullName} (관리자)";

            // CR1·CR3 작업자 개인차 사전 결정 (BuildScene에 전달)
            _walkSpeed1   = 0.017 + _personRng.NextDouble() * 0.012;
            _walkSpeed1b  = 0.017 + _personRng.NextDouble() * 0.012;
            _walkSpeed3   = 0.017 + _personRng.NextDouble() * 0.012;
            _walkSpeed3b  = 0.017 + _personRng.NextDouble() * 0.012;
            _walkSpeedEng = 0.024 + _personRng.NextDouble() * 0.008; // 엔지니어: 약간 빠름
            double sizeScale1   = 0.86 + _personRng.NextDouble() * 0.28;
            double sizeScale1b  = 0.86 + _personRng.NextDouble() * 0.28;
            double sizeScale3   = 0.86 + _personRng.NextDouble() * 0.28;
            double sizeScale3b  = 0.86 + _personRng.NextDouble() * 0.28;
            double sizeScaleEng = 0.90 + _personRng.NextDouble() * 0.20;

            _cam1 = BuildScene(viewport1, Color.FromRgb(0x22, 0xD3, 0xEE), _clickables1, _hoverActions1, out _live1, out _airNozzle1, out _distBeam1, out _person1, out var ae1, out var l1, equipReg: _equipReg1, personScale: sizeScale1);
            _cam2 = BuildScene(viewport2, Color.FromRgb(0x81, 0x8C, 0xF8), _clickables2, _hoverActions2, out _live2, out _airNozzle2, out _distBeam2, out _person2, out var ae2, out var l2, equipReg: _equipReg2);
            _cam3 = BuildScene(viewport3, Color.FromRgb(0x22, 0xD3, 0xEE), _clickables3, _hoverActions3, out _live3, out _airNozzle3, out _distBeam3, out _person3, out var ae3, out var l3, equipReg: _equipReg3, personScale: sizeScale3);
            (_layer1Equip, _layer1Vib, _layer1TH, _layer1FFU) = (l1.Equip, l1.Vib, l1.TH, l1.FFU);
            (_layer2Equip, _layer2Vib, _layer2TH, _layer2FFU) = (l2.Equip, l2.Vib, l2.TH, l2.FFU);
            (_layer3Equip, _layer3Vib, _layer3TH, _layer3FFU) = (l3.Equip, l3.Vib, l3.TH, l3.FFU);

            // 클린룸3 FFU 원본 색상 저장 (장애 표시 후 복원용)
            foreach (var v in _layer3FFU)
                if (v is LinesVisual3D lv3ffu) _ffuOrigColors3.Add((lv3ffu, lv3ffu.Color));

            // 에어샤워 문틀·분사 참조
            _airDoor1  = ae1.door;  _airSpray1 = ae1.spray;
            _airDoor2  = ae2.door;  _airSpray2 = ae2.spray;
            _airDoor3  = ae3.door;  _airSpray3 = ae3.spray;

            // 사람 Transform 참조 (BuildScene 내에서 person.Transform으로 설정됨)
            _personT1 = _person1?.Transform as TranslateTransform3D;
            _personT2 = _person2?.Transform as TranslateTransform3D;

            // CR3 첫 번째 작업자: BuildScene이 직접 추가한 figure를 그룹으로 래핑
            (_person3, _nameBillboard3, _personT3) =
                WrapInPersonGroup(viewport3, _person3!, sizeScale3, PersonName3);

            // CR1 두 번째 작업자 — viewport1에 추가, 투입 전까지 투명
            AddPersonFigure(viewport1, _clickables1, _hoverActions1, out _person1b, scale: sizeScale1b);
            _personT1b = _person1b?.Transform as TranslateTransform3D;
            if (_person1b != null) { _person1b.Points.Clear(); _person1b.Color = Colors.Transparent; }

            // CR3 두 번째 작업자 — 피겨 + 이름 레이블 그룹
            (_person3b, _nameBillboard3b, _personT3b) =
                AddPersonGroup(viewport3, _clickables3, _hoverActions3, PersonName3b, sizeScale3b);
            if (_person3b != null) { _person3b.Points.Clear(); _person3b.Color = Colors.Transparent; }

            // CR3 장비엔지니어 — 피겨 + 이름 레이블 그룹
            (_personEng, _nameBillboardEng, _personTEng) =
                AddPersonGroup(viewport3, _clickables3, _hoverActions3, EngineerName3, sizeScaleEng);
            if (_personEng != null) { _personEng.Points.Clear(); _personEng.Color = Colors.Transparent; }

            // 머리 위 이름 레이블 — CR1은 Canvas 오버레이, CR3는 3D Billboard (위에서 생성)
            _nameText1   = CreateNameText(nameCanvas1);
            _nameText1b  = CreateNameText(nameCanvas1);

            // 에어샤워 노즐 기본색 저장 + 블링크 타이머
            _nozzleIdleColor1 = _airNozzle1?.Color ?? Color.FromRgb(0x22, 0xD3, 0xEE);
            _nozzleIdleColor2 = _airNozzle2?.Color ?? Color.FromRgb(0xA7, 0x8B, 0xFA);
            _nozzleIdleColor3 = _airNozzle3?.Color ?? Color.FromRgb(0x22, 0xD3, 0xEE);
            _airBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _airBlinkTimer.Tick += (_, __) =>
            {
                _blinkState = !_blinkState;
                // 노즐 블링크
                if (_airNozzle1 != null)
                    _airNozzle1.Color = _airActive1 && _blinkState ? Colors.White : _nozzleIdleColor1;
                if (_airNozzle2 != null)
                    _airNozzle2.Color = _airActive2 && _blinkState ? Colors.White : _nozzleIdleColor2;
                if (_airNozzle3 != null)
                    _airNozzle3.Color = _airActive3 && _blinkState ? Colors.White : _nozzleIdleColor3;
                // 분사 블링크 (활성 시 밝음↔어두움, 비활성 시 투명)
                if (_airSpray1 != null)
                    _airSpray1.Color = _airActive1
                        ? (_blinkState ? SprayColorBright : SprayColorDim) : Colors.Transparent;
                if (_airSpray2 != null)
                    _airSpray2.Color = _airActive2
                        ? (_blinkState ? SprayColorBright : SprayColorDim) : Colors.Transparent;
                if (_airSpray3 != null)
                    _airSpray3.Color = _airActive3
                        ? (_blinkState ? SprayColorBright : SprayColorDim) : Colors.Transparent;
                // 배너 블링크
                airBanner1.Visibility = _airActive1 && _blinkState ? Visibility.Visible : Visibility.Hidden;
                airBanner2.Visibility = _airActive2 && _blinkState ? Visibility.Visible : Visibility.Hidden;
                airBanner3.Visibility = _airActive3 && _blinkState ? Visibility.Visible : Visibility.Hidden;
            };
            _airBlinkTimer.Start();

            // 작업자 진입 시퀀스: CompositionTarget.Rendering (60fps, 렌더 동기)
            // 내부 로직은 50ms 고정 스텝으로 누적 처리 → 부드러운 시각 + 기존 속도 유지
            _lastRenderMs = Environment.TickCount64;
            CompositionTarget.Rendering += (_, __) =>
            {
                long nowMs = Environment.TickCount64;
                double dtMs = Math.Min(nowMs - _lastRenderMs, 200); // 최대 200ms 클램프
                _lastRenderMs = nowMs;

                // 자동회전: 카메라 변경 → Helix 자동 리렌더
                if (_autoRotate1 && !_isPanning && !_isOrbiting)
                    OrbitCamera(_cam1, AutoRotateDxPerSec * dtMs / 1000.0, 0);
                if (_autoRotate2 && !_isPanning && !_isOrbiting)
                    OrbitCamera(_cam2, AutoRotateDxPerSec * dtMs / 1000.0, 0);
                if (_autoRotate3 && !_isPanning && !_isOrbiting)
                    OrbitCamera(_cam3!, AutoRotateDxPerSec * dtMs / 1000.0, 0);

                // 50ms 고정 스텝 누적 (자동회전 없을 때도 뷰포트 리렌더 보장)
                _animAccumMs += dtMs;
                bool ticked = false;
                while (_animAccumMs >= 50.0)
                {
                    _animAccumMs -= 50.0;
                    ticked = true;
                    // ── CR1 독립 업데이트 (두 작업자) ───────────────
                    UpdateCR1();
                    UpdateCR1B();

                // ── CR2 독립 업데이트 (관리자 지시) ─────────────
                UpdateCR2();

                // ── CR3 독립 업데이트 (두 작업자 + 엔지니어) ────
                UpdateCR3();
                UpdateCR3B();
                UpdateEngineer3();
                UpdateFailBlink3();
                // 진동 초과 장비 고장(_faults) 깜빡임: 1초 ON / 1초 OFF (FaultColor ↔ Transparent)
                foreach (var (frames, _, _) in _faults)
                    foreach (var f in frames)
                        f.Color = _failBlinkOn ? FaultColor : Colors.Transparent;
                TickRepairBanner3();
                TickCR1WorkerRepair();
                TickCR2Repair();

                // CR2 거리 센서: 아이콘(tglDist2)이 ON일 때만 빔 표시
                if (tglDist2.IsChecked == true)
                    UpdateDistBeam(_distBeam2, _p2X, _p2Z);
                else if (_distBeam2 != null)
                    _distBeam2.Points.Clear();

                // 섹터 문 닫기 — 거리 아이콘 클릭 후 점진적으로 닫힘
                if (_doorClosing2)
                {
                    _doorOpenOffset2 = Math.Max(0.0, _doorOpenOffset2 - 0.015);
                    ApplyDoorOffset2(_doorOpenOffset2);
                    if (_doorOpenOffset2 <= 0.001) _doorClosing2 = false;
                }
                } // end while(_animAccumMs >= 50)

                // 장비 고장 복구 체크 (만료된 항목 원본색 복원)
                RecoverFaults();

                // 자동회전 없고 이번 틱에서 뭔가 바뀐 경우 → 강제 리렌더
                // (카메라 변경 없이 Transform만 바뀔 때 Helix가 자동 갱신 안 할 수 있음)
                if (ticked && !_autoRotate1) viewport1.InvalidateVisual();
                if (ticked && !_autoRotate2) viewport2.InvalidateVisual();
                if (ticked && !_autoRotate3) viewport3.InvalidateVisual();
            }; // end CompositionTarget.Rendering

            // 섹터 초기화 (CR1)
            _personSectorIdx = _personRng.Next(3);
            (_personStartX, _personStartZ) = Sectors[_personSectorIdx];
            _personCurrentX = _personStartX; _personCurrentZ = _personStartZ;
            _nextWorkTicks  = 80 + _personRng.Next(120);
            _nextWaitTicks  = 60 + _personRng.Next(180);

            // CR1 두 번째 작업자 초기 섹터 (person1과 다른 섹터)
            do { _personSectorIdx1b = _personRng.Next(3); }
            while (_personSectorIdx1b == _personSectorIdx);
            (_personStartX1b, _personStartZ1b) = Sectors[_personSectorIdx1b];
            _personCurrentX1b = _personStartX1b; _personCurrentZ1b = _personStartZ1b;

            // 섹터 초기화 (CR3 — CR1과 다른 섹터 보장)
            do { _personSectorIdx3 = _personRng.Next(3); }
            while (_personSectorIdx3 == _personSectorIdx);
            (_personStartX3, _personStartZ3) = Sectors[_personSectorIdx3];
            _personCurrentX3 = _personStartX3; _personCurrentZ3 = _personStartZ3;
            _nextWorkTicks3  = 80 + _personRng.Next(120);
            _nextWaitTicks3  = 60 + _personRng.Next(180);

            // CR3 두 번째 작업자 초기 섹터
            do { _personSectorIdx3b = _personRng.Next(3); }
            while (_personSectorIdx3b == _personSectorIdx3);
            (_personStartX3b, _personStartZ3b) = Sectors[_personSectorIdx3b];
            _personCurrentX3b = _personStartX3b; _personCurrentZ3b = _personStartZ3b;

            // CR3 엔지니어 초기 섹터 (고정 섹터 1 — 중앙 통로)
            _engineerSectorIdx = 1;
            (_engineerStartX, _engineerStartZ) = Sectors[_engineerSectorIdx];
            _engineerCurrentX = _engineerStartX; _engineerCurrentZ = _engineerStartZ;

            // CR2 작업자: 숨김 상태로 시작 — 관리자 입실 지시 대기
            _p2X = 0.0;
            _p2Z = 0.0;

            // 클린룸별 독립 센서 서비스 (ADS 연결 전까지 랜덤 폴백)
            // 에어샤워 ON/OFF는 CompositionTarget.Rendering(페이즈 기반)이 단독 제어
            // 센서 서비스 콜백은 디스플레이(온도·습도·압력) 업데이트만 담당
            _sensorService1 = new SensorDataService(TimeSpan.FromSeconds(2));
            _sensorService1.DataUpdated += (_, data) => UpdateViewport(_live1, data);
            _sensorService1.DataUpdated += (_, data) => _eqAlarmService.CheckRoom(data, "R1");
            _sensorService1.Start();

            _sensorService2 = new SensorDataService(TimeSpan.FromSeconds(2));
            _sensorService2.DataUpdated += (_, data) => UpdateViewport(_live2, data);
            _sensorService2.DataUpdated += (_, data) => _eqAlarmService.CheckRoom(data, "R2");
            _sensorService2.Start();

            _sensorService3 = new SensorDataService(TimeSpan.FromSeconds(2));
            _sensorService3.DataUpdated += (_, data) => UpdateViewport(_live3, data);
            _sensorService3.DataUpdated += (_, data) => _eqAlarmService.CheckRoom(data, "R3");
            // 엔지니어 투입: 센서 아이콘 버튼 클릭 시에만 수동으로 트리거 (자동 감지 없음)
            _sensorService3.Start();

            // 진동·압력 임계값 초과 2회 시 CR2 랜덤 장비 고장 트리거
            _eqAlarmService.AlarmTriggered += (_, rec) =>
            {
                if (rec.Room == "R2" &&
                    (rec.Sensor == "진동" || rec.Sensor == "압력"))
                {
                    _cr2DangerCount++;
                    if (_cr2DangerCount >= 2)
                    {
                        _cr2DangerCount = 0;
                        TriggerEquipmentFault(rec.Room);
                    }
                }
            };

            // ADS 자동 연결 시도 (실패 시 랜덤 유지, UI 블로킹 없음)
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var ads = new AdsDataService();
                    ads.StatusChanged += msg =>
                        Dispatcher.Invoke(() =>
                        {
                            if (btnAdsConnect != null)
                            {
                                btnAdsConnect.Content    = ads.IsConnected ? "✔ ADS 연결됨" : "🔌 ADS 연결";
                                btnAdsConnect.Foreground = ads.IsConnected
                                    ? System.Windows.Media.Brushes.Cyan
                                    : System.Windows.Media.Brushes.MediumSeaGreen;
                                btnAdsConnect.ToolTip = msg;
                            }
                        });

                    ads.Connect(_sensorService1!, _sensorService2, _sensorService3, amsNetId: "127.0.0.1.1.1", port: 851);

                    Dispatcher.Invoke(() =>
                    {
                        _adsService = ads;
                        if (btnAdsConnect != null)
                        {
                            btnAdsConnect.Content    = "✔ ADS 연결됨";
                            btnAdsConnect.Foreground = System.Windows.Media.Brushes.Cyan;
                        }
                    });
                }
                catch
                {
                    // TwinCAT 미실행이면 랜덤 폴백 유지 — 아무것도 하지 않음
                }
            });

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) =>
                txtDateTime.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
            _clockTimer.Start();
            txtDateTime.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");

            // 스페이스 + 좌클릭 패닝 / 우클릭 카메라 정지
            this.KeyDown                     += Window_KeyDown;
            this.KeyUp                       += Window_KeyUp;
            this.PreviewMouseLeftButtonDown  += Window_PreviewMouseLeftButtonDown;

            this.PreviewMouseMove           += Window_PreviewMouseMove;
            this.PreviewMouseLeftButtonUp   += Window_PreviewMouseLeftButtonUp;
        }

        // ── 씬 빌드 ──────────────────────────────────────────────
        // 뷰포트 1개에 대한 전체 3D 씬 구성:
        // PerspectiveCamera 생성, 조명 추가, 클린룸 구조물·장비·작업자·센서를 모두 배치하고
        // 실시간 업데이트용 live(온습도·압력), airNozzle, distBeam, person 참조를 out으로 반환
        private PerspectiveCamera BuildScene(HelixViewport3D vp, Color accent,
                                              Dictionary<Visual3D, string> clickables,
                                              Dictionary<Visual3D, (Action onHover, Action onLeave)> hoverActions,
                                              out (LinesVisual3D? tempDig, LinesVisual3D? humDig, LinesVisual3D? needle) live,
                                              out LinesVisual3D airNozzle,
                                              out LinesVisual3D distBeam,
                                              out LinesVisual3D person,
                                              out (LinesVisual3D door, LinesVisual3D spray) airExtra,
                                              out SceneLayers layers,
                                              List<(LinesVisual3D[] frames, Color orig)>? equipReg = null,
                                              double personScale = 1.0)
        {
            var cam = new PerspectiveCamera
            {
                Position           = _views[0].pos,
                LookDirection      = _views[0].dir,
                UpDirection        = _views[0].up,
                FieldOfView        = 42,
                NearPlaneDistance  = 0.1,   // 0.01→0.1: 깊이버퍼 정밀도 10배 향상 (Z-fighting 감소)
                FarPlaneDistance   = 80.0
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
            var main   = new LinesVisual3D { Color = accent,          Thickness = 1.5 };
            var detail = new LinesVisual3D { Color = dim,             Thickness = 1.0 };
            var door   = new LinesVisual3D { Color = Colors.White,    Thickness = 2.0 };
            var nozzle = new LinesVisual3D { Color = bright,          Thickness = 1.5 };
            var spray  = new LinesVisual3D { Color = Colors.Transparent, Thickness = 1.2 };

            DrawBox(main);
            DrawRoomDetails(detail);
            DrawAirShower(main, door, nozzle);
            DrawAirShowerSpray(spray);

            // ── 클린룸 바닥 격자 ──────────────────────────────────
            var floor = new GridLinesVisual3D
            {
                Center        = new Point3D(0, -RY + 0.002, 0),
                Normal        = new Vector3D(0, 1, 0),
                Width         = RX * 2,
                Length        = RZ * 2,
                MinorDistance = 0.5,
                MajorDistance = 1.0,
                Thickness     = 0.004,
                Fill          = new SolidColorBrush(Color.FromArgb(70, 40, 200, 230))
            };
            vp.Children.Add(floor);

            vp.Children.Add(main);
            vp.Children.Add(detail);
            vp.Children.Add(door);
            vp.Children.Add(nozzle);
            vp.Children.Add(spray);

            // 에어샤워 노즐 레퍼런스 저장

            // 반투명 바닥·천장 패널 (라인 위에 추가)
            AddPanel(vp, 0, -RY, 0, 2*RX, 2*RZ, Color.FromArgb(30, accent.R, accent.G, accent.B));
            AddPanel(vp, 0,  RY, 0, 2*RX, 2*RZ, Color.FromArgb(15, accent.R, accent.G, accent.B));

            // 에어샤워 인터랙티브 프록시 (레이어 미분류 — 항상 표시)
            AddAirShowerProxy(vp, clickables, hoverActions, door, nozzle);

            // 스냅샷 헬퍼
            List<Visual3D> Snap(int from) {
                var r = new List<Visual3D>();
                for (int i = from; i < vp.Children.Count; i++) r.Add(vp.Children[i]);
                return r;
            }

            // ─ 거리 센서 + 작업자 (항상 표시) ─
            AddDistanceSensor(vp, clickables, hoverActions, out distBeam);
            AddPersonFigure(vp, clickables, hoverActions, out person, scale: personScale);

            // ─ 복도 섹터 마커 ─
            AddSectorMarkers(vp);

            // ─ 설비 레이어 (HEPA + FFU) ─
            int s = vp.Children.Count;
            AddHEPAFilter(vp, clickables, hoverActions);
            AddFFUFans(vp, clickables, hoverActions);
            layers.FFU = Snap(s);

            // ─ 온습도/압력 벽면 계기 제거 (센서 아이콘 버튼으로 대체) ─
            LinesVisual3D? needle = null, tempDig = null, humDig = null;
            layers.TH = new List<Visual3D>();

            // ─ 공정 장비 레이어 ─
            s = vp.Children.Count;
            AddFabEquipment(vp, clickables, hoverActions, vibOnly: false, equipReg: equipReg);
            layers.Equip = Snap(s);

            // ─ 진동 센서 레이어 ─
            s = vp.Children.Count;
            AddFabEquipment(vp, clickables, hoverActions, vibOnly: true);
            layers.Vib = Snap(s);

            // ─ 외부 건물 (클린룸을 감싸는 더 큰 공간) ─
            {
                // 외부 건물: X ±3.5, Y -RY ~ +3.0, Z -4.5 ~ +5.5  (OYb는 클린룸 바닥과 일치)
                const double OX = 3.5, OYt = 3.0;
                double OYb = -RY;
                const double OZn = -4.5, OZf = 5.5;

                var outer = new LinesVisual3D { Color = Color.FromRgb(0x22, 0x44, 0x66), Thickness = 1.0 };
                // 바닥 4변
                Seg(outer, -OX,OYb,OZn,  OX,OYb,OZn);
                Seg(outer,  OX,OYb,OZn,  OX,OYb,OZf);
                Seg(outer,  OX,OYb,OZf, -OX,OYb,OZf);
                Seg(outer, -OX,OYb,OZf, -OX,OYb,OZn);
                // 천장 4변
                Seg(outer, -OX,OYt,OZn,  OX,OYt,OZn);
                Seg(outer,  OX,OYt,OZn,  OX,OYt,OZf);
                Seg(outer,  OX,OYt,OZf, -OX,OYt,OZf);
                Seg(outer, -OX,OYt,OZf, -OX,OYt,OZn);
                // 수직 기둥 4개
                Seg(outer, -OX,OYb,OZn, -OX,OYt,OZn);
                Seg(outer,  OX,OYb,OZn,  OX,OYt,OZn);
                Seg(outer,  OX,OYb,OZf,  OX,OYt,OZf);
                Seg(outer, -OX,OYb,OZf, -OX,OYt,OZf);
                vp.Children.Add(outer);

                // 복도 바닥 패널 (클린룸 외부 영역 — 방 바닥과 동일 Y)
                AddPanel(vp, 0, OYb, (OZn+OZf)/2, OX*2, OZf-OZn,
                    Color.FromArgb(18, accent.R, accent.G, accent.B));
            }

            // ─ 외부박스 연결 3개 통로 + 자동문 ─
            // 뷰포트1이면 _doorLeftT1/RightT1, 뷰포트2이면 _doorLeftT2/RightT2 사용
            bool isVp1 = (vp == viewport1);
            bool isVp3 = (vp == viewport3);
            AddExternalCorridors(vp,
                isVp1 ? _doorLeftT1  : (isVp3 ? _doorLeftT3  : _doorLeftT2),
                isVp1 ? _doorRightT1 : (isVp3 ? _doorRightT3 : _doorRightT2));
            live      = (tempDig, humDig, needle);
            airNozzle = nozzle;
            airExtra  = (door, spray);
            return cam;
        }


        // FFU 팬 필터 유닛: 후면 벽 부착형 — HEPA와 나란히 배치
        private static void AddFFUFans(HelixViewport3D vp,
            Dictionary<Visual3D, string> clickables,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> hoverActions)
        {
            // 벽 부착형 치수 (HEPA와 동일 깊이)
            const double FW = 0.48;   // 폭 (X)
            const double FH = 0.48;   // 높이 (Y)
            const double FD = 0.12;   // 벽 돌출 깊이 (Z)

            // 후면 벽(z=-RZ) 하단 좌우에 배치, HEPA 오른쪽
            double[] ffuX = { 0.0, RX * 2.0/3.0 };  // X축 1:1:1 균등 배치 (HEPA=-2/3, FFU-①=0, FFU-②=+2/3)
            double   ffuYc = -RY + FH / 2 + 0.05;       // HEPA와 동일 Y (하단)
            double   wallZ = -RZ;
            double   faceZ = -RZ + FD;                   // 룸 안쪽 노출면

            var housingCol = Color.FromRgb(0x00, 0xCC, 0xFF);
            var fanCol     = Color.FromRgb(0xAA, 0xDD, 0xFF);

            double[] ffuXArr = ffuX;
            foreach (double fx in ffuXArr)
            {
                double x0 = fx - FW/2, x1 = fx + FW/2;
                double y0 = ffuYc - FH/2, y1 = ffuYc + FH/2;

                // ① 하우징 박스 (벽 접촉면 ↔ 룸 노출면)
                var frame = new LinesVisual3D { Color = housingCol, Thickness = 1.5 };
                // 후면(wallZ)
                Seg(frame, x0,y0,wallZ, x1,y0,wallZ); Seg(frame, x1,y0,wallZ, x1,y1,wallZ);
                Seg(frame, x1,y1,wallZ, x0,y1,wallZ); Seg(frame, x0,y1,wallZ, x0,y0,wallZ);
                // 전면(faceZ)
                Seg(frame, x0,y0,faceZ, x1,y0,faceZ); Seg(frame, x1,y0,faceZ, x1,y1,faceZ);
                Seg(frame, x1,y1,faceZ, x0,y1,faceZ); Seg(frame, x0,y1,faceZ, x0,y0,faceZ);
                // 측면 연결
                Seg(frame, x0,y0,wallZ, x0,y0,faceZ); Seg(frame, x1,y0,wallZ, x1,y0,faceZ);
                Seg(frame, x1,y1,wallZ, x1,y1,faceZ); Seg(frame, x0,y1,wallZ, x0,y1,faceZ);
                vp.Children.Add(frame);

                // ② 팬 원형 프레임 (전면 노출면 — X-Y 평면)
                double r = Math.Min(FW, FH) * 0.38;
                int circSegs = 24;
                var fanRing = new LinesVisual3D { Color = fanCol, Thickness = 1.3 };
                for (int i = 0; i < circSegs; i++)
                {
                    double a0 = 2*Math.PI * i     / circSegs;
                    double a1 = 2*Math.PI * (i+1) / circSegs;
                    fanRing.Points.Add(new Point3D(fx + r*Math.Cos(a0), ffuYc + r*Math.Sin(a0), faceZ));
                    fanRing.Points.Add(new Point3D(fx + r*Math.Cos(a1), ffuYc + r*Math.Sin(a1), faceZ));
                }
                vp.Children.Add(fanRing);

                // ③ 팬 블레이드 (6개)
                var blades = new LinesVisual3D { Color = fanCol, Thickness = 1.0 };
                int nBlades = 6;
                double hubR = r * 0.18;
                double sweep = Math.PI / (nBlades * 1.4);
                for (int i = 0; i < nBlades; i++)
                {
                    double ang = 2*Math.PI * i / nBlades;
                    blades.Points.Add(new Point3D(fx + hubR*Math.Cos(ang),           ffuYc + hubR*Math.Sin(ang),           faceZ));
                    blades.Points.Add(new Point3D(fx + r*0.52*Math.Cos(ang+sweep),   ffuYc + r*0.52*Math.Sin(ang+sweep),   faceZ));
                    blades.Points.Add(new Point3D(fx + r*0.52*Math.Cos(ang+sweep),   ffuYc + r*0.52*Math.Sin(ang+sweep),   faceZ));
                    blades.Points.Add(new Point3D(fx + r*0.88*Math.Cos(ang+sweep*2), ffuYc + r*0.88*Math.Sin(ang+sweep*2), faceZ));
                }
                vp.Children.Add(blades);

                // ④ 허브 원
                var hub = new LinesVisual3D { Color = fanCol, Thickness = 1.0 };
                for (int i = 0; i < 10; i++)
                {
                    double a0 = 2*Math.PI * i     / 10;
                    double a1 = 2*Math.PI * (i+1) / 10;
                    hub.Points.Add(new Point3D(fx + hubR*Math.Cos(a0), ffuYc + hubR*Math.Sin(a0), faceZ));
                    hub.Points.Add(new Point3D(fx + hubR*Math.Cos(a1), ffuYc + hubR*Math.Sin(a1), faceZ));
                }
                vp.Children.Add(hub);

                // hit-test 프록시
                int ffuIdx = Array.IndexOf(ffuXArr, fx) + 1;
                var ffuProxy = new BoxVisual3D
                {
                    Center = new Point3D(fx, ffuYc, wallZ + FD/2),
                    Width  = FW, Height = FH, Length = FD,
                    Fill   = new SolidColorBrush(Color.FromArgb(2, 255, 255, 255))
                };
                vp.Children.Add(ffuProxy);
                clickables[ffuProxy] = $"FFU 팬 필터 #{ffuIdx}\n공기 정화 장치 (Fan Filter Unit)";
                var fc = housingCol;
                hoverActions[ffuProxy] = (() => frame.Color = Color.FromRgb(0xFF, 0xFF, 0x88),
                                          () => frame.Color = fc);
            }
        }

        // 압력 센서: 우측 벽에 원형 게이지 + 바늘 배치, needle은 실시간 업데이트용으로 반환
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

            // ⑥ 바늘 (초기값 22 Pa — ISO 5 엄격 기준 정상 범위)
            needle = new LinesVisual3D { Color = needleCol, Thickness = 1.8 };
            DrawPressNeedle(needle, 22.0);
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

        // 온습도계: 좌측 벽에 7-세그먼트 디지털 디스플레이 배치, tempDig·humDig은 실시간 업데이트용 반환
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
        // 둥근 모서리 직사각형을 YZ 평면(고정 x)에 라인으로 그림
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

        // 센서 데이터 → 온도·습도 숫자 및 압력 바늘을 다시 그림 (벽면 계기 제거 후 null 가드)
        private static void UpdateViewport(
            (LinesVisual3D? tempDig, LinesVisual3D? humDig, LinesVisual3D? needle) live,
            SensorData data)
        {
            if (live.tempDig != null) DrawTempDigits(live.tempDig, data.Temperature);
            if (live.humDig  != null) DrawHumDigits (live.humDig,  data.Humidity);
            if (live.needle  != null) DrawPressNeedle(live.needle, data.Pressure);
        }

        // 온도 표시: 7-세그먼트 3자리(XX.X) 재드로우
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

        // 습도 표시: 7-세그먼트 2자리(XX) 재드로우
        private static void DrawHumDigits(LinesVisual3D L, double hum)
        {
            L.Points.Clear();
            hum = Math.Max(0, Math.Min(99, hum));
            int d1 = ((int)hum / 10) % 10;
            int d2 = (int)hum % 10;
            DrawSeg7(L, DispFaceX, DispHumY, DispCz - 0.033, DispHDH, Seg7[d1]);
            DrawSeg7(L, DispFaceX, DispHumY, DispCz + 0.007, DispHDH, Seg7[d2]);
        }

        // 압력 바늘 재드로우 (0~40 Pa 양압 차압, ISO 5 엄격 기준 ≥20 Pa)
        // 0 Pa → 1.32π, 40 Pa → 0.54π  (20 Pa → 0.93π 중앙 약간 오른쪽)
        // 압력 바늘: psi 값에 따라 게이지 바늘 각도 재계산 후 재드로우
        private static void DrawPressNeedle(LinesVisual3D L, double psi)
        {
            L.Points.Clear();
            psi = Math.Max(0, Math.Min(40, psi));
            double na = Math.PI * (1.32 - 0.0195 * psi); // (0.54-1.32)/40 = -0.0195
            L.Points.Add(new Point3D(PressFaceX, PressCy, PressCz));
            L.Points.Add(new Point3D(PressFaceX,
                PressCy + PressGr * 0.73 * Math.Sin(na),
                PressCz + PressGr * 0.73 * Math.Cos(na)));
        }

        // ── 8대 공정 장비 ─────────────────────────────────────────
        private static readonly Color[] EqPalette =
        {
            Color.FromRgb(0x00, 0xDD, 0xFF), // ① 산화   시안
            Color.FromRgb(0x66, 0xFF, 0xCC), // ② 포토   민트
            Color.FromRgb(0x44, 0xAA, 0xFF), // ③ 식각   스카이블루
            Color.FromRgb(0xAA, 0xDD, 0xFF), // ④ CVD    연파랑
            Color.FromRgb(0x00, 0xFF, 0xEE), // ⑤ 이온주입 청록
            Color.FromRgb(0x55, 0xCC, 0xFF), // ⑥ CMP   하늘
            Color.FromRgb(0x88, 0xEE, 0xFF), // ⑦ PVD   아이스블루
            Color.FromRgb(0x22, 0x88, 0xFF), // ⑧ 세정   기준색 (파랑)
        };

        // 8개 반도체 공정 장비 배치: vibOnly=false → 장비 형상, vibOnly=true → 진동 센서만 추가
        // equipReg에 각 장비의 LinesVisual3D 프레임 + 원본색을 등록해 고장 표시에 활용
        private static void AddFabEquipment(HelixViewport3D vp,
            Dictionary<Visual3D, string> clickables,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> hoverActions,
            bool vibOnly = false,
            List<(LinesVisual3D[] frames, Color orig)>? equipReg = null)
        {
            // 장비 레지스트리 타깃 설정 (vibOnly일 때는 등록 안 함)
            var prevReg = _equipRegistryTarget;
            _equipRegistryTarget = vibOnly ? null : equipReg;

            double yBot = -RY;
            double[] zP = { RZ * 0.75, RZ * 0.25, -RZ * 0.25, -RZ * 0.75 };
            var cl = clickables; var ha = hoverActions;

            // vibOnly=false → 장비 형상, vibOnly=true → 각 장비 함수 내부에서 정확한 위치로 센서 배치 (그룹화)
            if (!vibOnly) AddEq1_Furnace  (vp, -RX, yBot, zP[0], EqPalette[0], "① 산화로\n산화 공정 (Oxidation)",                  cl, ha);
            if ( vibOnly) AddEq1_Furnace  (vp, -RX, yBot, zP[0], default, "", cl, ha, drawSensor: true);

            if (!vibOnly) AddEq2_Photo    (vp, -RX, yBot, zP[1], EqPalette[1], "② 포토리소그래피\n포토 공정 (Photolithography)",     cl, ha);
            if ( vibOnly) AddEq2_Photo    (vp, -RX, yBot, zP[1], default, "", cl, ha, drawSensor: true);

            if (!vibOnly) AddEq3_Etcher   (vp, -RX, yBot, zP[2], EqPalette[2], "③ 식각기\n식각 공정 (Etching)",                      cl, ha);
            if ( vibOnly) AddEq3_Etcher   (vp, -RX, yBot, zP[2], default, "", cl, ha, drawSensor: true);

            if (!vibOnly) AddEq4_CVD      (vp, -RX, yBot, zP[3], EqPalette[3], "④ CVD 증착기\n박막·증착 공정 (Thin Film Deposition)", cl, ha);
            if ( vibOnly) AddEq4_CVD      (vp, -RX, yBot, zP[3], default, "", cl, ha, drawSensor: true);

            if (!vibOnly) AddEq5_Implanter(vp, +RX, yBot, zP[3], EqPalette[4], "⑤ 이온주입기\n금속 배선 공정 (Metal Wiring)",         cl, ha);
            if ( vibOnly) AddEq5_Implanter(vp, +RX, yBot, zP[3], default, "", cl, ha, drawSensor: true);

            if (!vibOnly) AddEq6_CMP      (vp, +RX, yBot, zP[2], EqPalette[5], "⑥ CMP\n배선 공정 (Interconnect)",                    cl, ha);
            if ( vibOnly) AddEq6_CMP      (vp, +RX, yBot, zP[2], default, "", cl, ha, drawSensor: true);

            if (!vibOnly) AddEq7_PVD      (vp, +RX, yBot, zP[1], EqPalette[6], "⑦ PVD 스퍼터\nEDS 공정 (Electrical Die Sorting)",    cl, ha);
            if ( vibOnly) AddEq7_PVD      (vp, +RX, yBot, zP[1], default, "", cl, ha, drawSensor: true);

            if (!vibOnly) AddEq8_WetBench (vp, +RX, yBot, zP[0], EqPalette[7], "⑧ 세정기\n패키징 공정 (Packaging)",                  cl, ha);
            if ( vibOnly) AddEq8_WetBench (vp, +RX, yBot, zP[0], default, "", cl, ha, drawSensor: true);

            // 레지스트리 타깃 복원
            _equipRegistryTarget = prevReg;
        }

        // 진동 센서: 장비 상단에 원통 + 케이블 라인으로 표현 (sx/sy/sz = 부착 좌표)
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
        // 반투명 원통 박스(Y축 방향)를 뷰포트에 추가 — 진동 센서 바디용
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
        // 장비 외형 박스: 라인 프레임 + 반투명 면 채움, LinesVisual3D 반환 (고장 색 변경용)
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

        // 솔리드 박스 메시(6면)를 뷰포트에 추가하고 ModelVisual3D 반환 — 장비 채움 + 히트테스트 겸용
        private static ModelVisual3D FillBox(HelixViewport3D vp,
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
            var mv   = new ModelVisual3D {
                Content = new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = mat }
            };
            vp.Children.Add(mv);
            return mv;
        }

        // 장비 히트테스트 프록시(투명 BoxVisual3D) 생성 + 클릭 라벨·호버 하이라이트 등록 (단일 프레임)
        private static void EquipProxy(HelixViewport3D vp,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            double cx, double cy, double cz, double w, double h, double d,
            string label, LinesVisual3D frame, Color orig)
            => EquipProxy(vp, cl, ha, cx, cy, cz, w, h, d, label, new[] { frame }, orig);

        // 장비 히트테스트 프록시 생성 + 클릭 라벨·호버 하이라이트 등록 (복합 형상 — 모든 프레임 동시 하이라이트)
        private static void EquipProxy(HelixViewport3D vp,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            double cx, double cy, double cz, double w, double h, double d,
            string label, LinesVisual3D[] frames, Color orig)
        {
            var oc = orig;
            var hi = Color.FromRgb(0xFF, 0xFF, 0x88);

            // 히트테스트 전용 투명 BoxVisual3D — Fill(Brush)로 설정 (Material 직접 할당 금지)
            // Color.FromArgb(2,...) : 거의 투명하나 WPF 3D 레이캐스트는 기하 메시 기준 → 정확히 히트 감지
            var hitBox = new BoxVisual3D
            {
                Center = new Point3D(cx, cy, cz),
                Width  = w + 0.02,
                Height = h + 0.02,
                Length = d + 0.02,
                Fill   = new SolidColorBrush(Color.FromArgb(2, 0xFF, 0xFF, 0xFF)),
            };
            vp.Children.Add(hitBox);

            cl[hitBox] = label;
            ha[hitBox] = (
                () => { foreach (var fr in frames) fr.Color = hi; },
                () => { foreach (var fr in frames) fr.Color = oc; }
            );

            // LinesVisual3D 프레임도 등록 (보조 히트테스트 + 레지스트리)
            foreach (var f in frames)
            {
                cl[f] = label;
                ha[f] = (
                    () => { foreach (var fr in frames) fr.Color = hi; },
                    () => { foreach (var fr in frames) fr.Color = oc; }
                );
            }
            // 장비 고장 레지스트리에 등록 (frames 사본 + 원본색 저장)
            _equipRegistryTarget?.Add(((LinesVisual3D[])frames.Clone(), orig));
        }

        // 벽면(좌:-RX, 우:+RX)과 장비 너비로 x0·x1·frontX(작업자가 서는 전면 x)를 계산
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
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            bool drawSensor = false)
        {
            const double W=0.24, H1=0.46, D1=0.70;   // 넓고 낮은 메인 바디
            const double H2=0.20, D2=0.26;            // 우측 상단 제어 모듈
            var (x0,x1,fx) = EqBounds(wallX, W);
            if (drawSensor) { AddVibSensor(vp, fx, yBot+H1+H2, cz+0.08, cl, ha); return; }
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
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            bool drawSensor = false)
        {
            const double W=0.24, H1=0.26, D1=0.62;   // 하단 트랙 베이스
            const double H2=0.42, D2=0.24;            // 상단 스테퍼 렌즈 컬럼
            var (x0,x1,fx) = EqBounds(wallX, W);
            if (drawSensor) { AddVibSensor(vp, fx, yBot+H1+H2, cz-0.06, cl, ha); return; }
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
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            bool drawSensor = false)
        {
            const double W=0.26, H1=0.16, D1=0.36;   // 하단 컨트롤 콘솔
            const double H2=0.52, D2=0.52;            // 메인 챔버 (정사각형 단면)
            var (x0,x1,fx) = EqBounds(wallX, W);
            if (drawSensor) { AddVibSensor(vp, fx, yBot+H1+H2, cz+0.06, cl, ha); return; }
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
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            bool drawSensor = false)
        {
            const double W=0.24, H1=0.20, D1=0.60;   // 하단 가스 캐비닛 (넓음)
            const double H2=0.50, D2=0.34;            // 상단 반응로 (좁고 키 큼)
            var (x0,x1,fx) = EqBounds(wallX, W);
            if (drawSensor) { AddVibSensor(vp, fx, yBot+H1+H2, cz+0.05, cl, ha); return; }
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
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            bool drawSensor = false)
        {
            const double W=0.24;
            const double D_s=0.26, H_s=0.60;  // 소스 (가장 높음)
            const double D_a=0.24, H_a=0.40;  // 분석기
            const double D_e=0.30, H_e=0.50;  // 엔드스테이션
            double totalD = D_s+D_a+D_e;
            var (x0,x1,fx5) = EqBounds(wallX, W);
            if (drawSensor) { AddVibSensor(vp, fx5, yBot+H_s, cz-0.27, cl, ha); return; }
            double zS0=cz-totalD/2, zS1=zS0+D_s;
            double zA0=zS1,         zA1=zA0+D_a;
            double zE0=zA1,         zE1=zA0+D_a+D_e;
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
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            bool drawSensor = false)
        {
            const double W=0.26, H1=0.30, D1=0.72;   // 넓은 베이스
            const double H2=0.34, D2=0.38;            // 중앙 연마 유닛
            var (x0,x1,fx) = EqBounds(wallX, W);
            if (drawSensor) { AddVibSensor(vp, fx, yBot+H1+H2, cz-0.06, cl, ha); return; }
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
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            bool drawSensor = false)
        {
            const double W_b=0.28, H_b=0.14, D_b=0.58;   // 넓은 베이스
            const double W_t=0.22, H_t=0.54, D_t=0.36;   // 좁은 타워
            var (bx0,bx1,_) = EqBounds(wallX, W_b);
            var (tx0,tx1,fx) = EqBounds(wallX, W_t);
            if (drawSensor) { AddVibSensor(vp, fx, yBot+H_b+H_t, cz-0.05, cl, ha); return; }
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
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            bool drawSensor = false)
        {
            const double W=0.24, H_bench=0.36, D_bench=0.86;
            const double H_hood=0.22;
            var (x0,x1,fx) = EqBounds(wallX, W);
            if (drawSensor) { AddVibSensor(vp, fx, yBot+H_bench, cz-0.32, cl, ha); return; }
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

        // 반투명 수평 메시 패널(바닥·천장) 추가
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

        // 클린룸 외벽 12개 엣지 라인 그리기
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

        // 클린룸 내부 디테일: 내부 테두리·기둥 라인으로 실내감 표현
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
        // 에어샤워 영역에 투명 프록시 박스 추가 — 마우스 호버 시 문틀·노즐 하이라이트
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

        // HEPA 필터: 후면 벽 하단 패널 형태로 라인 배치 (클릭 시 정보 표시)
        private static void AddHEPAFilter(HelixViewport3D vp,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha)
        {
            // 위치: 후면 벽(z=-RZ)에 수직 부착, 좌측(x=-0.55), 하단(y=-0.95~-0.55)
            const double FW=0.52, FH=0.40, FD=0.12;   // 벽 부착형 — 넓고 얇음
            double fcx = -RX * 2.0/3.0;  // X축 1:1:1 균등 배치 (HEPA=-2/3, FFU-①=0, FFU-②=+2/3)
            double fcy = -RY + FH/2 + 0.05;            // 바닥 바로 위 하단
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

        // 에어샤워 챔버: 외벽 프레임(structure), 문틀(door), 노즐 라인(nozzle) 그리기
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

        // 에어샤워 분사 라인: 평소 투명, 가동 시 밝음↔어두움 블링크로 분사 시각화
        private static void DrawAirShowerSpray(LinesVisual3D spray)
        {
            const double dw    = RX * 0.38;   // 벽 X 위치 ≈ ±0.361
            const double dh    = RY * 1.70;
            const double depth = 0.55;

            // 노즐과 같은 위치에서 분사
            foreach (double nz in new[] { RZ + depth * 0.28, RZ + depth * 0.72 })
                foreach (double ny in new[] { -RY + dh * 0.20, -RY + dh * 0.52, -RY + dh * 0.82 })
                {
                    double reach = dw * 0.85; // 분사 도달 거리 (중심까지의 85%)
                    // 좌벽 → 중심 방향
                    Seg(spray, -dw, ny,          nz,  -dw + reach, ny,          nz);
                    Seg(spray, -dw, ny,          nz,  -dw + reach, ny + 0.055,  nz);
                    Seg(spray, -dw, ny,          nz,  -dw + reach, ny - 0.055,  nz);
                    // 우벽 → 중심 방향
                    Seg(spray,  dw, ny,          nz,   dw - reach, ny,          nz);
                    Seg(spray,  dw, ny,          nz,   dw - reach, ny + 0.055,  nz);
                    Seg(spray,  dw, ny,          nz,   dw - reach, ny - 0.055,  nz);
                }
        }

        // ── 거리 센서 (에어샤워 외벽 상단 마운트) ────────────────────
        // 에어샤워 좌표: topY = -RY+RY*1.70 = 0.70 / outerZ = RZ+0.55 = 2.55
        // 거리 센서: 에어샤워 입구 상단에 송수신 본체 배치, 빔 라인(distBeam)은 실시간 연장
        private static void AddDistanceSensor(HelixViewport3D vp,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            out LinesVisual3D distBeam)
        {
            const double dh    = RY * 1.70;
            const double depth = 0.55;
            double topY  = -RY + dh;        // 0.70
            double outerZ = RZ + depth;     // 2.55

            // 센서 본체 치수
            const double bw = 0.055, bh = 0.030, bd = 0.018;
            double bx0 = -bw / 2, bx1 = bw / 2;
            double by0 = topY - bh,  by1 = topY;        // 0.670 ~ 0.700
            double bz0 = outerZ,     bz1 = outerZ + bd; // 2.550 ~ 2.568
            double tyCen = (by0 + by1) / 2;             // 0.685

            var pcbCol   = Color.FromRgb(0x1A, 0x6B, 0x35); // 진녹색 PCB
            var transCol = Color.FromRgb(0xCC, 0x99, 0x33);  // 금색 트랜스듀서
            var mountCol = Color.FromRgb(0x88, 0x88, 0x99);  // 회색 브라켓

            // ① PCB 본체 박스 (12 edges)
            var body = new LinesVisual3D { Color = pcbCol, Thickness = 1.3 };
            // 앞면 (bz1)
            Seg(body, bx0,by0,bz1, bx1,by0,bz1); Seg(body, bx1,by0,bz1, bx1,by1,bz1);
            Seg(body, bx1,by1,bz1, bx0,by1,bz1); Seg(body, bx0,by1,bz1, bx0,by0,bz1);
            // 뒷면 (bz0)
            Seg(body, bx0,by0,bz0, bx1,by0,bz0); Seg(body, bx1,by0,bz0, bx1,by1,bz0);
            Seg(body, bx1,by1,bz0, bx0,by1,bz0); Seg(body, bx0,by1,bz0, bx0,by0,bz0);
            // 연결 4 기둥
            Seg(body, bx0,by0,bz0, bx0,by0,bz1); Seg(body, bx1,by0,bz0, bx1,by0,bz1);
            Seg(body, bx1,by1,bz0, bx1,by1,bz1); Seg(body, bx0,by1,bz0, bx0,by1,bz1);
            vp.Children.Add(body);

            // ② 마운트 브라켓 (도어 프레임 상단 → 센서 연결)
            var mount = new LinesVisual3D { Color = mountCol, Thickness = 1.0 };
            Seg(mount,  0, topY, outerZ - 0.015,  0, topY, outerZ);           // 수직 스텝
            Seg(mount, bx0, topY, outerZ,         bx1, topY, outerZ);         // 가로 베이스
            vp.Children.Add(mount);

            // ③ 두 개의 초음파 트랜스듀서 원 (앞면 bz1, XY 평면)
            const double txR = 0.009;
            int sg = 14;
            var trans = new LinesVisual3D { Color = transCol, Thickness = 1.3 };
            foreach (double txX in new[] { -bw / 4, bw / 4 })
            {
                for (int i = 0; i < sg; i++)
                {
                    double a0 = 2 * Math.PI * i     / sg;
                    double a1 = 2 * Math.PI * (i+1) / sg;
                    trans.Points.Add(new Point3D(txX + txR*Math.Cos(a0), tyCen + txR*Math.Sin(a0), bz1));
                    trans.Points.Add(new Point3D(txX + txR*Math.Cos(a1), tyCen + txR*Math.Sin(a1), bz1));
                }
                // 트랜스듀서 내부 크로스
                trans.Points.Add(new Point3D(txX - txR*0.5, tyCen, bz1 + 0.001));
                trans.Points.Add(new Point3D(txX + txR*0.5, tyCen, bz1 + 0.001));
                trans.Points.Add(new Point3D(txX, tyCen - txR*0.5, bz1 + 0.001));
                trans.Points.Add(new Point3D(txX, tyCen + txR*0.5, bz1 + 0.001));
            }
            vp.Children.Add(trans);

            // ④ 측정 빔 초기값 (PersonOriginZ = 4.05 기준)
            const double personY0 = -RY + 0.575 + 0.055; // ≈-0.37  머리 높이 (축소 후)
            distBeam = new LinesVisual3D { Color = Color.FromRgb(0xF4, 0x72, 0x18), Thickness = 1.0 };
            distBeam.Points.Add(new Point3D(0, tyCen, bz1));
            distBeam.Points.Add(new Point3D(0, personY0, PersonOriginZ));
            vp.Children.Add(distBeam);

            // ⑤ 호버 프록시
            var proxy = new BoxVisual3D
            {
                Center = new Point3D(0, (by0+by1)/2, (bz0+bz1)/2),
                Width  = bw + 0.02, Height = bh + 0.02, Length = bd + 0.04,
                Fill   = new SolidColorBrush(Color.FromArgb(2, 255, 255, 255))
            };
            vp.Children.Add(proxy);
            cl[proxy] = "거리 센서\n에어샤워 입구 감지 (초음파)\n인체 접근 시 에어샤워 작동";
            ha[proxy] = (
                () => { body.Color = Color.FromRgb(0x44, 0xFF, 0x88); trans.Color = Colors.Yellow; },
                () => { body.Color = pcbCol; trans.Color = transCol; }
            );
        }

        // ── 머리 위 이름 레이블 헬퍼 (Canvas TextBlock) ─────────────────
        private static System.Windows.Controls.TextBlock CreateNameText(System.Windows.Controls.Canvas canvas)
        {
            var tb = new System.Windows.Controls.TextBlock
            {
                Text       = "",
                FontSize   = 11,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White,
                Visibility = System.Windows.Visibility.Collapsed,
            };
            canvas.Children.Add(tb);
            return tb;
        }

        // 3D 월드 좌표 → Canvas 2D 좌표 변환
        private static System.Windows.Point? Project3D(
            Point3D world, PerspectiveCamera cam, double w, double h)
        {
            if (cam == null || w <= 0 || h <= 0) return null;

            var look  = cam.LookDirection; look.Normalize();
            var upDir = cam.UpDirection;
            var right = Vector3D.CrossProduct(look, upDir); right.Normalize();
            var up    = Vector3D.CrossProduct(right, look); up.Normalize();

            var delta = world - cam.Position;
            double vx =  Vector3D.DotProduct(delta, right);
            double vy =  Vector3D.DotProduct(delta, up);
            double vz = -Vector3D.DotProduct(delta, look); // 카메라 앞쪽이 -Z

            if (vz <= 0.01) return null; // 카메라 뒤

            double fovRad = cam.FieldOfView * Math.PI / 180.0;
            double tanHalf = Math.Tan(fovRad / 2.0);

            double sx = vx / (vz * tanHalf * (w / h));
            double sy = vy / (vz * tanHalf);

            return new System.Windows.Point(
                (sx + 1.0) * 0.5 * w,
                (1.0 - sy) * 0.5 * h);
        }

        // ── 에어샤워 시뮬레이션 헬퍼 ─────────────────────────────────────
        private void SimulateAirShowerOn(SensorDataService? svc)
        {
            if (_adsService?.IsConnected != true && svc != null)
                svc.Current.AirShowerPressure = 0.15; // 시뮬레이션: 임계치(0.08 MPa) 초과
        }
        private void SimulateAirShowerOff(SensorDataService? svc)
        {
            if (_adsService?.IsConnected != true && svc != null)
                svc.Current.AirShowerPressure = 0.0;
        }

        private void UpdateNameLabel(
            System.Windows.Controls.TextBlock? tb,
            double wx, double wy, double wz,
            PerspectiveCamera cam, double cw, double ch)
        {
            if (tb == null) return;
            if (string.IsNullOrEmpty(tb.Text)) { tb.Visibility = System.Windows.Visibility.Collapsed; return; }

            var pt = Project3D(new Point3D(wx, wy, wz), cam, cw, ch);
            if (pt == null) { tb.Visibility = System.Windows.Visibility.Collapsed; return; }

            tb.Visibility = System.Windows.Visibility.Visible;
            System.Windows.Controls.Canvas.SetLeft(tb, pt.Value.X - tb.ActualWidth  / 2);
            System.Windows.Controls.Canvas.SetTop (tb, pt.Value.Y - tb.ActualHeight - 2);
        }

        // ── 작업자 스틱피겨 (에어샤워 통로 고정 위치) ──────────────────
        // 스틱 피겨 작업자: TranslateTransform3D 부착, 위치는 렌더 루프에서 실시간 갱신
        private static void AddPersonFigure(HelixViewport3D vp,
            Dictionary<Visual3D, string> cl,
            Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
            out LinesVisual3D person,
            double scale = 1.0)
        {
            const double depth = 0.55;
            double fZ  = RZ + depth + 1.5; // 4.05 (통로 중간)
            double fY  = -RY;              // -1.0 (바닥)

            // Y 좌표 계산 — 모든 오프셋에 scale 적용 (키·체형 개인차)
            double hR  = 0.055 * scale;         // 머리 반지름
            double hCy = fY + 0.575 * scale;    // 머리 중심
            double neck = hCy - hR;             // 목 하단
            double sY   = neck  - 0.045 * scale; // 어깨
            double eY   = sY    - 0.135 * scale; // 팔꿈치
            double wY   = eY    - 0.110 * scale; // 손목
            double hipY = sY    - 0.220 * scale; // 골반
            double knY  = hipY  - 0.145 * scale; // 무릎
            double ftY  = fY + 0.01;             // 발 (바닥 기준 고정)

            // X 좌표(팔·다리 너비)도 scale 적용
            double sw  = 0.115 * scale; // 어깨 너비 절반
            double sw2 = 0.090 * scale; // 손목 X
            double hw  = 0.065 * scale; // 골반 너비 절반
            double hw2 = 0.045 * scale; // 발 X

            person = new LinesVisual3D { Color = PersonIdleColor, Thickness = 1.4 };

            // ① 머리 원
            int sg = 18;
            for (int i = 0; i < sg; i++)
            {
                double a0 = 2*Math.PI*i/sg, a1 = 2*Math.PI*(i+1)/sg;
                person.Points.Add(new Point3D(hR*Math.Cos(a0), hCy + hR*Math.Sin(a0), fZ));
                person.Points.Add(new Point3D(hR*Math.Cos(a1), hCy + hR*Math.Sin(a1), fZ));
            }
            // ② 목
            Seg(person,  0,   neck,  fZ,   0,    sY,  fZ);
            // ③ 몸통
            Seg(person,  0,   sY,    fZ,   0,   hipY, fZ);
            // ④ 왼팔
            Seg(person,  0,   sY,    fZ, -sw,    eY,  fZ);
            Seg(person, -sw,  eY,    fZ, -sw2,   wY,  fZ);
            // ⑤ 오른팔
            Seg(person,  0,   sY,    fZ,  sw,    eY,  fZ);
            Seg(person,  sw,  eY,    fZ,  sw2,   wY,  fZ);
            // ⑥ 왼다리
            Seg(person,  0,   hipY,  fZ, -hw,   knY,  fZ);
            Seg(person, -hw,  knY,   fZ, -hw2,  ftY,  fZ);
            // ⑦ 오른다리
            Seg(person,  0,   hipY,  fZ,  hw,   knY,  fZ);
            Seg(person,  hw,  knY,   fZ,  hw2,  ftY,  fZ);
            // ⑧ 발
            Seg(person, -hw2, fY, fZ-0.03*scale, -hw2, fY, fZ+0.03*scale);
            Seg(person,  hw2, fY, fZ-0.03*scale,  hw2, fY, fZ+0.03*scale);

            // TranslateTransform3D — 렌더 루프에서 Offset 갱신
            var translate = new TranslateTransform3D(0, 0, 0);
            person.Transform = translate;

            vp.Children.Add(person);
        }

        // ── 피겨 + Billboard 그룹 헬퍼 ───────────────────────────────────────────
        // AddPersonFigure 결과를 ModelVisual3D 그룹으로 감싸고 BillboardTextVisual3D 이름 레이블 추가
        // figure + billboard가 TranslateTransform3D 하나를 공유 → OffsetX/Z 갱신 시 함께 이동
        private static (LinesVisual3D figure, BillboardTextVisual3D billboard, TranslateTransform3D transform)
            AddPersonGroup(HelixViewport3D vp,
                Dictionary<Visual3D, string> cl,
                Dictionary<Visual3D, (Action onHover, Action onLeave)> ha,
                string name, double scale = 1.0)
        {
            // 1. 피겨 생성 (vp에 직접 추가됨)
            AddPersonFigure(vp, cl, ha, out var figure, scale);

            // 2. 그룹으로 래핑
            return WrapInPersonGroup(vp, figure, scale, name);
        }

        // BuildScene 내부에서 생성된 figure를 그룹으로 래핑 (별도 경로)
        private static (LinesVisual3D figure, BillboardTextVisual3D billboard, TranslateTransform3D transform)
            WrapInPersonGroup(HelixViewport3D vp, LinesVisual3D figure, double scale, string name)
        {
            // figure 개별 TranslateTransform 제거 후 viewport에서 분리
            figure.Transform = null;
            vp.Children.Remove(figure);

            // 3D 이름 레이블 — 머리 위 고정 위치 (로컬 좌표)
            double headTopY = -RY + (0.575 + 0.055 + 0.06) * scale;
            var billboard = new BillboardTextVisual3D
            {
                Position    = new Point3D(0, headTopY, PersonOriginZ),
                Text        = "",          // 진입 시 이름 설정, 퇴장 시 ""로 초기화
                FontSize    = 10,
                Foreground  = Brushes.White,
                Background  = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Padding     = new Thickness(3, 1, 3, 1),
            };

            // 공유 TranslateTransform3D — figure + billboard 동시 이동
            var translate = new TranslateTransform3D(0, 0, 0);
            var group     = new ModelVisual3D { Transform = translate };
            group.Children.Add(figure);
            group.Children.Add(billboard);
            vp.Children.Add(group);

            return (figure, billboard, translate);
        }

        // ── 작업 포즈 재빌드 ─────────────────────────────────────────────────────
        // working=true  : 장비 방향(X축)으로 팔 뻗기 + 상체 미세 숙임
        // working=false : 기본 직립 포즈
        // 매 틱마다 스틱 피겨 포즈 재계산: working=true이면 팔을 사인파로 흔들어 작업 모션 표현
        private static void RebuildPersonPose(LinesVisual3D? person, double t,
                                              bool working, double workX = 0.0)
        {
            if (person == null) return;

            double fZ = PersonOriginZ;
            double fY = -RY;

            // ── 크기 0.75 스케일 ─────────────────────────────────
            const double hR  = 0.041;   // 0.055 × 0.75
            double hCy  = fY + 0.431;   // 0.575 × 0.75
            double neck = hCy - hR;
            double sY   = neck - 0.034; // 0.045 × 0.75
            double eY   = sY  - 0.101;  // 0.135 × 0.75
            double wY   = eY  - 0.083;  // 0.110 × 0.75
            double hipY = sY  - 0.165;  // 0.220 × 0.75
            double knY  = hipY - 0.109; // 0.145 × 0.75
            double ftY  = fY  + 0.01;
            const double sW = 0.086;    // 어깨 너비 0.115 × 0.75
            const double eW = 0.086;
            const double wW = 0.068;    // 손목 너비 0.090 × 0.75
            const double hX = 0.049;    // 힙·무릎 0.065 × 0.75
            const double fX = 0.034;    // 발 0.045 × 0.75

            double cycle = working ? 2 * Math.PI * t / WorkPeriod : 0;
            double lean  = working ? 0.009 * Math.Abs(Math.Sin(cycle)) : 0;

            person.Points.Clear();

            // ① 머리
            double hZ = fZ - lean * 0.5;
            int sg = 18;
            for (int i = 0; i < sg; i++)
            {
                double a0 = 2*Math.PI*i/sg, a1 = 2*Math.PI*(i+1)/sg;
                person.Points.Add(new Point3D(hR*Math.Cos(a0), hCy + hR*Math.Sin(a0), hZ));
                person.Points.Add(new Point3D(hR*Math.Cos(a1), hCy + hR*Math.Sin(a1), hZ));
            }
            // ② 목
            Seg(person, 0, neck, hZ, 0, sY, fZ - lean);
            // ③ 몸통
            Seg(person, 0, sY, fZ - lean, 0, hipY, fZ);
            // ⑥⑦ 다리
            Seg(person,  0,   hipY, fZ,  hX, knY, fZ);
            Seg(person,  hX,  knY,  fZ,  fX, ftY, fZ);
            Seg(person,  0,   hipY, fZ, -hX, knY, fZ);
            Seg(person, -hX,  knY,  fZ, -fX, ftY, fZ);
            // ⑧ 발
            Seg(person, -fX, fY, fZ-0.023, -fX, fY, fZ+0.023);
            Seg(person,  fX, fY, fZ-0.023,  fX, fY, fZ+0.023);

            if (working)
            {
                // ── 장비 방향(X축) 팔 동작 ──────────────────────
                int side = Math.Sign(workX); // -1=좌벽, +1=우벽, 0=중앙
                if (side == 0) side = -1;    // 기본값: 왼쪽

                double reach  = 0.065 * Math.Abs(Math.Sin(cycle)); // 0~0.065 왕복
                double touchY = wY + 0.040 * Math.Sin(cycle);      // 패널 위아래 터치

                // 장비 쪽 팔: X 방향으로 뻗음
                double wx = side * (wW + reach);
                double ex = side * eW * 0.9;
                Seg(person, 0,  sY, fZ-lean, ex, eY, fZ);
                Seg(person, ex, eY, fZ,      wx, touchY, fZ);

                // 반대 팔: 밸런스용 소폭 반대 방향
                double ox = -side * eW * 0.55;
                Seg(person, 0,  sY, fZ-lean,  ox, eY, fZ);
                Seg(person, ox, eY, fZ, -side*wW*0.4, wY, fZ);
            }
            else
            {
                // ── 기본 직립 팔 ────────────────────────────────
                Seg(person,  0,  sY, fZ,  -sW, eY, fZ);
                Seg(person, -sW, eY, fZ,  -wW, wY, fZ);
                Seg(person,  0,  sY, fZ,   sW, eY, fZ);
                Seg(person,  sW, eY, fZ,   wW, wY, fZ);
            }
        }

        // ── 거리 빔 동적 업데이트 (DataUpdated 호출마다 빔 길이·색상 갱신) ──
        // personZ: 현재 사람 Z 위치 (이미 클램프 적용된 값)
        // CR1 페이즈 전환: 페이즈 변경 후 관련 UI(배너·인증팝업·문)를 상태에 맞게 초기화
        private void PersonTransition(PersonPhase next)
        {
            _personPhase      = next;
            _personPhaseCount = 0;
            switch (next)
            {
                case PersonPhase.WalkToEquip:
                    if (_waypointIdx == 0)
                    {
                        InitWaypoints();
                        if (_cr1bCountdown == -1) _cr1bCountdown = 200; // 10초 후 1b 투입
                    }
                    break;
                case PersonPhase.WorkingAtEquip:
                    _personAnimT   = 0;
                    _nextWorkTicks = EqWorkMinTick + _personRng.Next(EqWorkRndTick + 1);
                    break;
                case PersonPhase.CorridorWait:
                    _nextWaitTicks = 80 + _personRng.Next(160);
                    if (_nameText1  != null) _nameText1.Text  = "";
                    break;
                case PersonPhase.CorridorWalk:
                    // 새 섹터 랜덤 선택 후 즉시 텔레포트
                    _personSectorIdx = _personRng.Next(3);
                    (_personStartX, _personStartZ) = Sectors[_personSectorIdx];
                    _personCurrentX = _personStartX;
                    _personCurrentZ = _personStartZ;
                    break;
                case PersonPhase.AuthWait:
                    // CR2 출입 요청 배너
                    ShowAuthRequest1(isEngineer: false);
                    break;
                case PersonPhase.DoorOpening:
                    // CR1 비밀번호 팝업만 닫고 문 열기 시작
                    authPopup1.Visibility  = Visibility.Collapsed;
                    authInput1.Text        = string.Empty;
                    authError1.Visibility  = Visibility.Collapsed;
                    _doorOpenOffset        = 0.0;
                    break;
                case PersonPhase.CorridorEntry:
                    // 문 서서히 닫힘 — 업데이트 루프에서 처리
                    break;
                case PersonPhase.AirShowerEntry:
                    SimulateAirShowerOn(_sensorService1);
                    break;
                case PersonPhase.AirShowerExit:
                    SimulateAirShowerOff(_sensorService1);
                    break;
                case PersonPhase.CorridorExitWalk:
                    _personCurrentX = _personStartX;
                    CloseDoor();
                    break;
            }
        }

        // 외부 통로 3개: 섹터별 복도 박스 + 슬라이드 자동문(TranslateTransform3D 부착)
        private void AddExternalCorridors(HelixViewport3D vp,
            TranslateTransform3D?[] doorLeftT, TranslateTransform3D?[] doorRightT)
        {
            double Yf        = -RY;      // 클린룸 바닥과 동일 (RY 연동)
            const double Yt  =  0.3;    // 낮은 천장
            const double CW   =  0.32;
            const double OZf  =  5.5;
            const double CZe  =  8.75; // 통로 (이전 12.0의 절반 길이)
            double[] centerXs = { -2.2, 0.0, 2.2 };

            var c = new LinesVisual3D { Color = Color.FromRgb(0x33, 0x55, 0x88), Thickness = 1.2 };

            void CorridorZ(double xc, double zS, double zE)
            {
                // 안쪽 면은 자동문이 대신하므로 생략 (zS 쪽 면 없음)
                // 바깥 면 (통로 끝)
                Seg(c, xc-CW,Yf,zE,  xc+CW,Yf,zE);
                Seg(c, xc+CW,Yf,zE,  xc+CW,Yt,zE);
                Seg(c, xc+CW,Yt,zE,  xc-CW,Yt,zE);
                Seg(c, xc-CW,Yt,zE,  xc-CW,Yf,zE);
                // 4개 모서리 엣지
                Seg(c, xc-CW,Yf,zS,  xc-CW,Yf,zE);
                Seg(c, xc+CW,Yf,zS,  xc+CW,Yf,zE);
                Seg(c, xc-CW,Yt,zS,  xc-CW,Yt,zE);
                Seg(c, xc+CW,Yt,zS,  xc+CW,Yt,zE);
                // 천장 안쪽 보
                Seg(c, xc-CW,Yt,zS,  xc+CW,Yt,zS);
            }

            for (int i = 0; i < 3; i++)
            {
                double xc = centerXs[i];
                CorridorZ(xc, OZf, CZe);

                // ── 자동문 패널 (좌·우) at Z=OZf ────────────────
                // 좌 패널: xc-CW ~ xc (닫혔을 때)
                var leftPanel = new LinesVisual3D
                {
                    Color = Color.FromRgb(0x4A, 0x8A, 0xB0), Thickness = 1.5
                };
                Seg(leftPanel, xc-CW, Yf, OZf,  xc, Yf, OZf);
                Seg(leftPanel, xc,    Yf, OZf,  xc, Yt, OZf);
                Seg(leftPanel, xc,    Yt, OZf,  xc-CW, Yt, OZf);
                Seg(leftPanel, xc-CW, Yt, OZf,  xc-CW, Yf, OZf);

                var leftT = new TranslateTransform3D(0, 0, 0);
                leftPanel.Transform = leftT;
                doorLeftT[i] = leftT;
                vp.Children.Add(leftPanel);

                // 우 패널: xc ~ xc+CW (닫혔을 때)
                var rightPanel = new LinesVisual3D
                {
                    Color = Color.FromRgb(0x4A, 0x8A, 0xB0), Thickness = 1.5
                };
                Seg(rightPanel, xc,    Yf, OZf,  xc+CW, Yf, OZf);
                Seg(rightPanel, xc+CW, Yf, OZf,  xc+CW, Yt, OZf);
                Seg(rightPanel, xc+CW, Yt, OZf,  xc,    Yt, OZf);
                Seg(rightPanel, xc,    Yt, OZf,  xc,    Yf, OZf);

                var rightT = new TranslateTransform3D(0, 0, 0);
                rightPanel.Transform = rightT;
                doorRightT[i] = rightT;
                vp.Children.Add(rightPanel);
            }

            vp.Children.Add(c);
        }

        // ── 복도 섹터 번호 마커 (바닥에 1·2·3 선분 숫자) ────────
        // 섹터 마커: 3개 통로 끝 바닥에 ①②③ 숫자와 테두리 라인 표시
        private static void AddSectorMarkers(HelixViewport3D vp)
        {
            double Yf = -RY + 0.002;  // 바닥 살짝 위 (RY 연동)
            const double W  = 0.13;           // 숫자 반-너비 (X)
            const double H  = 0.20;           // 숫자 반-높이 (Z)
            // 섹터 중심 (Sectors 배열과 동일)
            (double cx, double cz)[] centers = { (-2.2, 8.2), (0.0, 8.2), (2.2, 8.2) };

            // 세그먼트 정의 (정규화 좌표 -1~+1): (x1,z1,x2,z2)
            // Z축이 "높이" 방향 (−Z=위, +Z=아래)
            // a=상단, b=우상, c=우하, d=하단, e=좌하, f=좌상, g=중간
            (float x1, float z1, float x2, float z2)[] segs =
            {
                (-1,-1, 1,-1),  // a 상단
                ( 1,-1, 1, 0),  // b 우상
                ( 1, 0, 1, 1),  // c 우하
                (-1, 1, 1, 1),  // d 하단
                (-1, 0,-1, 1),  // e 좌하
                (-1,-1,-1, 0),  // f 좌상
                (-1, 0, 1, 0),  // g 중간
            };

            // digit → 켜야 할 세그먼트 인덱스 (a=0..g=6)
            int[][] digits =
            {
                new[]{ 1, 2 },               // "1": b,c
                new[]{ 0, 1, 6, 4, 3 },      // "2": a,b,g,e,d
                new[]{ 0, 1, 6, 2, 3 },      // "3": a,b,g,c,d
            };

            var marker = new LinesVisual3D
            {
                Color     = Color.FromRgb(0xCC, 0xCC, 0xCC),
                Thickness = 1.8,
            };

            for (int i = 0; i < 3; i++)
            {
                (double cx, double cz) = centers[i];
                foreach (int si in digits[i])
                {
                    var s = segs[si];
                    marker.Points.Add(new Point3D(cx + s.x1 * W, Yf, cz + s.z1 * H));
                    marker.Points.Add(new Point3D(cx + s.x2 * W, Yf, cz + s.z2 * H));
                }
            }
            vp.Children.Add(marker);
        }

        // ── 출입 인증 UI 이벤트 핸들러 ─────────────────────────

        // 관리자 허용 버튼
        // CR1 허용 버튼 — 자동 시뮬레이션 출입 인증
        private void BtnAllow1_Click(object sender, RoutedEventArgs e) => ShowAuthPopup();
        // CR2 허용 버튼 — 관리자 지시 모드에서는 사용 안 함
        private void BtnAllow2_Click(object sender, RoutedEventArgs e)
        {
            if (_cr2Phase == CR2Phase.AuthWait)
                ShowAuthPopup2();
        }
        // CR3 허용 버튼 — CR1과 동일 방식
        private void BtnAllow3_Click(object sender, RoutedEventArgs e) => ShowAuthPopup3();

        // CR1 인증 팝업 표시 — AuthWait 중인 작업자(1 우선, 아니면 1b)를 진행
        private void ShowAuthPopup()
        {
            authRequest1.Visibility = Visibility.Collapsed;

            var dlg = new WorkerAuthDialog("작업자", "A+클린룸") { Owner = this };
            if (dlg.ShowDialog() != true) return;

            User? authedUser = dlg.AuthenticatedUser;
            string label = authedUser != null ? $"{authedUser.Role}:{authedUser.FullName}" : "";

            if (_personPhase == PersonPhase.AuthWait)
            {
                PersonTransition(PersonPhase.DoorOpening);
            }
            else if (_personPhase1b == PersonPhase.AuthWait)
            {
                PersonTransition1b(PersonPhase.DoorOpening);
            }
        }

        // CR3 인증 팝업 표시 — AuthWait 중인 작업자(3 > 3b > 엔지니어 순 우선)
        // 작업자: 1차 ID+PW(DB) → 2차 "A+클린룸"
        // 장비 엔지니어: 1차 ID+PW(DB) → 2차 "천재엔지니어스"
        private void ShowAuthPopup3()
        {
            authRequest3.Visibility = Visibility.Collapsed;
            btnAllow3.Visibility    = Visibility.Collapsed;

            // 누가 AuthWait 중인지 파악해 역할 결정
            string role;
            if (_personPhase3 == PersonPhase.AuthWait || _personPhase3b == PersonPhase.AuthWait)
                role = "작업자";
            else if (_engineerActive && _engineerPhase == PersonPhase.AuthWait)
                role = "장비 엔지니어";
            else
            {
                authRequest3.Visibility = Visibility.Visible;
                btnAllow3.Visibility    = Visibility.Visible;
                return;
            }

            string label;
            if (role == "장비 엔지니어")
            {
                var engDlg = new EngineerAuthDialog("천재엔지니어스") { Owner = this };
                if (engDlg.ShowDialog() != true) return;
                User? u = engDlg.AuthenticatedUser;
                label = u != null ? $"{u.Role}:{u.FullName}" : EngineerName3;
                EngineerName3 = label;
            }
            else
            {
                var wrkDlg = new WorkerAuthDialog("작업자", "A+클린룸") { Owner = this };
                if (wrkDlg.ShowDialog() != true) return;
                User? u = wrkDlg.AuthenticatedUser;
                label = u != null ? $"{u.Role}:{u.FullName}" : "";
                if (_personPhase3 == PersonPhase.AuthWait)  PersonName3  = label;
                else                                         PersonName3b = label;
            }

            if (_personPhase3 == PersonPhase.AuthWait)
                PersonTransition3(PersonPhase.DoorOpening);
            else if (_personPhase3b == PersonPhase.AuthWait)
                PersonTransition3b(PersonPhase.DoorOpening);
            else if (_engineerActive && _engineerPhase == PersonPhase.AuthWait)
                EngineerTransition3(PersonPhase.DoorOpening);
        }

        // 출입 요청 배너 표시 — 역할에 따라 문구·색 변경
        private void ShowAuthRequest1(bool isEngineer = false)
        {
            if (authRequestText1 != null)
            {
                authRequestText1.Text       = isEngineer ? "장비 엔지니어가 출입을 요청합니다" : "작업자가 출입을 요청합니다";
                authRequestText1.Foreground = isEngineer
                    ? new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD))  // 파랑
                    : new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC)); // 초록
            }
            if (authRequestIcon1 != null)
                authRequestIcon1.Text = isEngineer ? "🔧" : "🔔";
            authRequest1.Visibility = Visibility.Visible;
        }

        private void ShowAuthRequest3(bool isEngineer = false)
        {
            if (authRequestText3 != null)
            {
                authRequestText3.Text       = isEngineer ? "장비 엔지니어가 출입을 요청합니다" : "작업자가 출입을 요청합니다";
                authRequestText3.Foreground = isEngineer
                    ? new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD))  // 파랑
                    : new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC)); // 초록
            }
            if (authRequestIcon3 != null)
                authRequestIcon3.Text = isEngineer ? "🔧" : "🔔";
            authRequest3.Visibility = Visibility.Visible;
            btnAllow3.Visibility    = Visibility.Visible;  // 버튼 별도 레이어
        }

        // CR1/CR3 인증 확인 버튼: 입력된 비밀번호를 TryAuth/TryAuth3에 전달
        private void BtnAuthConfirm1_Click(object sender, RoutedEventArgs e) => TryAuth(authInput1.Text);
        private void BtnAuthConfirm2_Click(object sender, RoutedEventArgs e) { }  // CR2는 ShowAuthPopup2() 사용
        private void BtnAuthConfirm3_Click(object sender, RoutedEventArgs e) => TryAuth3(authInput3.Text);

        // CR1/CR3 비밀번호 입력창 Enter 키 처리: Return 입력 시 TryAuth 호출
        private void AuthInput1_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        { if (e.Key == System.Windows.Input.Key.Return) TryAuth(authInput1.Text); }
        private void AuthInput2_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { }  // 미사용

        // CR2 거리 센서 추적은 TglDist2_Click에서 처리
        private void AuthInput3_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        { if (e.Key == System.Windows.Input.Key.Return) TryAuth3(authInput3.Text); }

        // CR1 비밀번호 검증 — AuthWait 중인 작업자(1 우선, 아니면 1b)를 진행
        private void TryAuth(string input)
        {
            if (input == AuthPassword)
            {
                if (_personPhase == PersonPhase.AuthWait)
                    PersonTransition(PersonPhase.DoorOpening);
                else if (_personPhase1b == PersonPhase.AuthWait)
                    PersonTransition1b(PersonPhase.DoorOpening);
            }
            else
            {
                authError1.Visibility = Visibility.Visible;
                authInput1.Text       = string.Empty;
                authInput1.Focus();
            }
        }

        // CR3 비밀번호 검증 — 3 > 3b > 엔지니어 순 우선
        private void TryAuth3(string input)
        {
            if (input == AuthPassword)
            {
                if (_personPhase3 == PersonPhase.AuthWait)
                    PersonTransition3(PersonPhase.DoorOpening);
                else if (_personPhase3b == PersonPhase.AuthWait)
                    PersonTransition3b(PersonPhase.DoorOpening);
                else if (_engineerActive && _engineerPhase == PersonPhase.AuthWait)
                    EngineerTransition3(PersonPhase.DoorOpening);
            }
            else
            {
                authError3.Visibility = Visibility.Visible;
                authInput3.Text       = string.Empty;
                authInput3.Focus();
            }
        }

        // ── 자동문 Transform 적용 / 닫기 ─────────────────────────

        // CR1 자동문: 섹터 통로의 좌·우 문 패널 X 오프셋 적용 (열림=AutoDoorOpenX)
        private void ApplyDoorOffset(double offset)
        {
            int s = _personSectorIdx;
            if (_doorLeftT1[s]  != null) _doorLeftT1[s]!.OffsetX  = -offset;
            if (_doorRightT1[s] != null) _doorRightT1[s]!.OffsetX =  offset;
        }

        // CR2 자동문 오프셋 적용
        private void ApplyDoorOffset2(double offset)
        {
            int s = _p2SectorIdx;
            if (_doorLeftT2[s]  != null) _doorLeftT2[s]!.OffsetX  = -offset;
            if (_doorRightT2[s] != null) _doorRightT2[s]!.OffsetX =  offset;
        }

        // CR1 자동문 즉시 닫기 (스냅)
        private void CloseDoor()
        {
            // 즉시 닫기 (애니메이션 없이 스냅)
            _doorOpenOffset = 0.0;
            ApplyDoorOffset(0.0);
        }

        // 현재 값을 target 방향으로 speed만큼 이동 (오버슈트 방지)
        private static double MoveTowardZ(double current, double target, double speed)
        {
            double d = target - current;
            return Math.Abs(d) <= speed ? target : current + Math.Sign(d) * speed;
        }

        // 거리 센서 빔: 작업자 머리 중심을 향해 라인을 재빌드 (매 틱 재빌드, 전역 캐시 제거)
        private static void UpdateDistBeam(LinesVisual3D? beam, double personX, double personZ)
        {
            if (beam == null) return;

            const double beamZ0  = RZ + 0.55 + 0.018;        // 2.568  센서 앞면
            const double sensorY = -RY + RY * 1.70 - 0.015;  // 0.685  센서 높이
            const double personY = -RY + 0.575;               // ≈-0.425 사람 머리 중심

            beam.Points.Clear();
            beam.Points.Add(new Point3D(0, sensorY, beamZ0));
            beam.Points.Add(new Point3D(personX, personY, personZ));

            // 트리거 여부: 사람이 에어샤워 문(outerZ=2.55) 기준 1m 이내
            bool triggered = personZ - (RZ + 0.55) < AirShowerTriggerDist;
            beam.Color = triggered
                ? Color.FromRgb(0xEF, 0x44, 0x44)
                : Color.FromRgb(0xF4, 0x72, 0x18);
        }

        // 3D 선분 한 쌍을 LinesVisual3D에 추가하는 최소 헬퍼
        private static void Seg(LinesVisual3D L,
            double x1,double y1,double z1, double x2,double y2,double z2)
        {
            L.Points.Add(new Point3D(x1,y1,z1));
            L.Points.Add(new Point3D(x2,y2,z2));
        }

        // XZ 평면 직사각형 4선 추가 (수평 사각형)
        private static void RectXZ(LinesVisual3D L,
            double cx, double cy, double cz, double w, double d)
        {
            double hw=w/2, hd=d/2;
            Seg(L,cx-hw,cy,cz-hd, cx+hw,cy,cz-hd);
            Seg(L,cx+hw,cy,cz-hd, cx+hw,cy,cz+hd);
            Seg(L,cx+hw,cy,cz+hd, cx-hw,cy,cz+hd);
            Seg(L,cx-hw,cy,cz+hd, cx-hw,cy,cz-hd);
        }

        // YZ 평면 직사각형 4선 추가 (수직 사각형)
        private static void RectYZ(LinesVisual3D L,
            double cx, double cy, double cz, double h, double d)
        {
            double hh=h/2, hd=d/2;
            Seg(L,cx,cy-hh,cz-hd, cx,cy+hh,cz-hd);
            Seg(L,cx,cy+hh,cz-hd, cx,cy+hh,cz+hd);
            Seg(L,cx,cy+hh,cz+hd, cx,cy-hh,cz+hd);
            Seg(L,cx,cy-hh,cz+hd, cx,cy-hh,cz-hd);
        }

        // 프리셋 시점 적용: 등각(0)/정면(1)/측면(2)/상단(3)
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
        private void btnView3_Iso_Click  (object s, RoutedEventArgs e) => SetView(_cam3!, 0);
        private void btnView3_Front_Click(object s, RoutedEventArgs e) => SetView(_cam3!, 1);
        private void btnView3_Side_Click (object s, RoutedEventArgs e) => SetView(_cam3!, 2);
        private void btnView3_Top_Click  (object s, RoutedEventArgs e) => SetView(_cam3!, 3);

        // CR1 자동회전 토글: ToggleButton IsChecked → _autoRotate1 플래그 반영
        private void btnAutoRotate1_Click(object s, RoutedEventArgs e)
        {
            _autoRotate1 = (s as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true;
        }
        // CR3 자동회전 토글: ToggleButton IsChecked → _autoRotate3 플래그 반영
        private void btnAutoRotate3_Click(object s, RoutedEventArgs e)
        {
            _autoRotate3 = (s as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true;
        }

        // CR3 애니메이션 일시정지/재생 토글 (버튼 색상 황색↔청록으로 상태 표시)
        private void BtnPause3_Click(object s, RoutedEventArgs e)
        {
            _paused3 = !_paused3;
            btnPause3.Content = _paused3 ? "▶  재생" : "⏸  정지";
            var clr3 = _paused3
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xD3, 0xEE));
            btnPause3.BorderBrush = clr3;
            btnPause3.Foreground  = clr3;
        }

        // CR1 애니메이션 일시정지/재생 토글 (버튼 색상 황색↔청록으로 상태 표시)
        private void BtnPause1_Click(object s, RoutedEventArgs e)
        {
            _paused1 = !_paused1;
            if (_paused1)
            {
                btnPause1.Content = "▶  재생";
                var amber = new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
                btnPause1.BorderBrush = amber;
                btnPause1.Foreground  = amber;
            }
            else
            {
                btnPause1.Content = "⏸  정지";
                var cyan = new System.Windows.Media.SolidColorBrush(
                               System.Windows.Media.Color.FromRgb(0x22, 0xD3, 0xEE));
                btnPause1.BorderBrush = cyan;
                btnPause1.Foreground  = cyan;
            }
        }
        // CR2 정지/재생 버튼
        private void BtnPause2_Click(object s, RoutedEventArgs e)
        {
            _paused2 = !_paused2;
            if (_paused2)
            {
                btnPause2.Content = "▶  재생";
                var amber = new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
                btnPause2.BorderBrush = amber;
                btnPause2.Foreground  = amber;
            }
            else
            {
                btnPause2.Content = "⏸  정지";
                var purple = new System.Windows.Media.SolidColorBrush(
                                 System.Windows.Media.Color.FromRgb(0x81, 0x8C, 0xF8));
                btnPause2.BorderBrush = purple;
                btnPause2.Foreground  = purple;
            }
        }

        // CR2 거리 센서 추적 토글 (📡거리 아이콘)
        private void TglDist2_Click(object s, RoutedEventArgs e)
        {
            if (tglDist2.IsChecked != true)
            {
                // 수동 OFF — 빔만 끔 (문은 이미 닫히는 중이면 유지)
                if (_distBeam2 != null) _distBeam2.Points.Clear();
                return;
            }
            // 추적 ON: 섹터 문 닫기 시작
            _doorClosing2 = true;
            // BoxIdle 또는 CorridorEntry 상태면 에어샤워로 이동
            if (_cr2Phase == CR2Phase.BoxIdle)
                CR2Transition(CR2Phase.AirShowerEntry);
            // CorridorEntry 중이면 BoxIdle 도착 후 자동으로 AirShowerEntry 전환됨
        }

        // CR2 자동회전 토글: ToggleButton IsChecked → _autoRotate2 플래그 반영
        private void btnAutoRotate2_Click(object s, RoutedEventArgs e)
        {
            _autoRotate2 = (s as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true;
        }

        // Space 누름: 커서를 이동 아이콘으로 변경하여 패닝 모드 진입 예고
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) { Cursor = Cursors.SizeAll; e.Handled = true; }
        }

        // Space 해제: 패닝 중이면 종료하고 카메라 컨트롤 복원
        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                Cursor = Cursors.Arrow;
                if (_isPanning)
                {
                    _isPanning = false;
                    _panCam    = null;
                    Mouse.Capture(null);
                    // Helix 컨트롤 복원 (카메라 고정은 우클릭으로)
                    if (_panVp != null)
                    {
                        _panVp.IsRotationEnabled = true;
                        _panVp.IsPanEnabled      = true;
                        _panVp = null;
                    }
                }
            }
        }

        // 카메라 애니메이션 정지: BeginAnimation(null)로 Helix 자동 보간을 즉시 끊음
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

        // 좌클릭: Space 누른 상태면 패닝 시작, Ctrl이면 궤도 회전 시작, 아니면 장비 클릭 정보 표시
        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 오버레이 WPF 컨트롤(Button, ToggleButton, TextBox 등)이면 이벤트 양보
            DependencyObject? hitCur = e.OriginalSource as DependencyObject;
            while (hitCur != null)
            {
                if (hitCur is System.Windows.Controls.Primitives.ButtonBase || hitCur is TextBox)
                    return;
                hitCur = VisualTreeHelper.GetParent(hitCur);
            }

            bool space       = Keyboard.IsKeyDown(Key.Space);
            bool ctrl        = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            bool over1       = IsOverViewport(viewport1);
            bool over2       = IsOverViewport(viewport2);
            bool over3       = IsOverViewport(viewport3);
            bool overViewport = over1 || over2 || over3;

            // 뷰포트 밖(버튼 등)이면 아무것도 하지 않음
            if (!overViewport) return;

            if (space)
            {
                // 스페이스+좌클릭 → 패닝
                _panVp  = over1 ? viewport1 : over2 ? viewport2 : viewport3;
                _panCam = over1 ? _cam1 : over2 ? _cam2 : _cam3;
                // HelixToolkit 카메라 컨트롤 비활성화 → 패닝/Space해제 시 간섭 차단
                _panVp.IsRotationEnabled = false;
                _panVp.IsPanEnabled      = false;
                // 현재 위치를 추적 시작값으로 초기화
                _trackedPos  = _panCam!.Position;
                _trackedLook = _panCam.LookDirection;
                _trackedUp   = _panCam.UpDirection;
                _isPanning = true;
                _panLast   = e.GetPosition(this);
                Mouse.Capture(this);
                e.Handled = true;
            }
            else if (ctrl)
            {
                // Ctrl+좌클릭 → 궤도 회전 (viewport1, 2, 3 모두 지원)
                _orbitCam   = over1 ? _cam1 : over2 ? _cam2 : _cam3;
                _isOrbiting = true;
                _orbitLast  = e.GetPosition(this);
                Mouse.Capture(this);
                e.Handled = true;
            }
            else
            {
                // 뷰포트 위 일반 좌클릭 → hit test 후 orbit 차단
                var vp    = over1 ? viewport1 : over2 ? viewport2 : viewport3;
                var dict  = over1 ? _clickables1 : over2 ? _clickables2 : _clickables3;
                var panel = over1 ? infoPanel1 : over2 ? infoPanel2 : infoPanel3;
                var text  = over1 ? infoText1 : over2 ? infoText2 : infoText3;

                var process = over1 ? infoProcess1 : over2 ? infoProcess2 : infoProcess3;
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

        // 마우스 이동: 패닝·궤도 회전 처리 + 호버 하이라이트 30fps 쓰로틀
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
                // 패닝 위치를 직접 추적 (cam.Position은 Helix 애니메이션에 의해 오염될 수 있음)
                _trackedPos  = _panCam.Position;
                _trackedLook = _panCam.LookDirection;
                _trackedUp   = _panCam.UpDirection;
            }
            else if (_isOrbiting && _orbitCam != null)
            {
                double dx = cur.X - _orbitLast.X;
                double dy = cur.Y - _orbitLast.Y;
                _orbitLast = cur;
                OrbitCamera(_orbitCam!, dx, dy);
            }

            // 호버 하이라이트 (패닝·회전 중이 아닐 때만, 33ms 쓰로틀 = 최대 30fps)
            if (!_isPanning && !_isOrbiting)
            {
                long nowMs = Environment.TickCount64;
                if (nowMs - _lastHoverTick >= 33)
                {
                    _lastHoverTick = nowMs;
                    DoHoverHighlight(viewport1, _hoverActions1, ref _hovered1, ref _restoreHover1, e);
                    DoHoverHighlight(viewport2, _hoverActions2, ref _hovered2, ref _restoreHover2, e);
                    DoHoverHighlight(viewport3, _hoverActions3, ref _hovered3, ref _restoreHover3, e);
                }
            }
        }

        // 카메라를 원점 기준으로 구면 좌표 회전 (dx=수평, dy=수직 델타)
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

        // 좌클릭 해제: 패닝·궤도 회전 종료 및 마우스 캡처 해제
        private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                _panCam    = null;
                Mouse.Capture(null);
                if (_panVp != null)
                {
                    _panVp.IsRotationEnabled = true;
                    _panVp.IsPanEnabled      = true;
                    _panVp = null;
                }
            }
            else if (_isOrbiting)
            {
                _isOrbiting = false;
                _orbitCam   = null;
                Mouse.Capture(null);
            }
        }


        // ── 호버 하이라이트 헬퍼 ─────────────────────────────────
        // 마우스 위치의 Visual3D를 히트테스트해 onHover/onLeave 액션으로 하이라이트 전환
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

        // 마우스 포인터가 해당 UI 요소 영역 안에 있는지 확인
        private static bool IsOverViewport(System.Windows.UIElement el)
        {
            var p = Mouse.GetPosition(el);
            return p.X >= 0 && p.Y >= 0
                && p.X <= el.RenderSize.Width
                && p.Y <= el.RenderSize.Height;
        }

        // 레이어 켜기/끄기: show=false이면 Children에서 제거, true이면 다시 추가
        private static void ToggleLayer(HelixViewport3D vp, List<Visual3D> layer, bool show)
        {
            foreach (var v in layer)
            {
                if (show  && !vp.Children.Contains(v)) vp.Children.Add(v);
                if (!show &&  vp.Children.Contains(v)) vp.Children.Remove(v);
            }
        }

        // 클린룸 1
        private void Tgl1Equip_Click(object s, RoutedEventArgs e) => ToggleLayer(viewport1, _layer1Equip, tgl1Equip.IsChecked == true);
        private void Tgl1Vib_Click  (object s, RoutedEventArgs e) => ToggleLayer(viewport1, _layer1Vib,   tgl1Vib.IsChecked   == true);
        private void Tgl1TH_Click   (object s, RoutedEventArgs e) => ToggleLayer(viewport1, _layer1TH,    tgl1TH.IsChecked    == true);
        private void Tgl1FFU_Click  (object s, RoutedEventArgs e) => ToggleLayer(viewport1, _layer1FFU,   tgl1FFU.IsChecked   == true);

        // 클린룸 2
        private void Tgl2Equip_Click(object s, RoutedEventArgs e) => ToggleLayer(viewport2, _layer2Equip, tgl2Equip.IsChecked == true);
        private void Tgl2Vib_Click  (object s, RoutedEventArgs e) => ToggleLayer(viewport2, _layer2Vib,   tgl2Vib.IsChecked   == true);
        private void Tgl2TH_Click   (object s, RoutedEventArgs e) => ToggleLayer(viewport2, _layer2TH,    tgl2TH.IsChecked    == true);
        private void Tgl2FFU_Click  (object s, RoutedEventArgs e) => ToggleLayer(viewport2, _layer2FFU,   tgl2FFU.IsChecked   == true);

        // 클린룸 3
        private void Tgl3Equip_Click(object s, RoutedEventArgs e) => ToggleLayer(viewport3, _layer3Equip, tgl3Equip.IsChecked == true);
        private void Tgl3Vib_Click  (object s, RoutedEventArgs e) => ToggleLayer(viewport3, _layer3Vib,   tgl3Vib.IsChecked   == true);
        private void Tgl3TH_Click   (object s, RoutedEventArgs e) => ToggleLayer(viewport3, _layer3TH,    tgl3TH.IsChecked    == true);
        private void Tgl3FFU_Click  (object s, RoutedEventArgs e) => ToggleLayer(viewport3, _layer3FFU,   tgl3FFU.IsChecked   == true);

        // ── 테스트 버튼: 장비 연결 없이 엔지니어 투입 즉시 트리거 ──
        private void TestVib_Click  (object s, RoutedEventArgs e) => TriggerEngineerDispatch(EngineerTriggerReason.Vibration);
        private void TestTH_Click   (object s, RoutedEventArgs e) => TriggerEngineerDispatch(EngineerTriggerReason.TempHumidity);
        private void TestPress_Click (object s, RoutedEventArgs e) => TriggerEngineerDispatch(EngineerTriggerReason.Pressure);

        // 📡거리 버튼 — 추적 ON: BoxWait 인원 진행 + 빔 추적 시작 / OFF: 추적 종료
        private void TglDist3_Click(object s, RoutedEventArgs e)
        {
            if (tglDist3.IsChecked != true)
            {
                // 수동 OFF
                _distTracking3 = false;
                if (_distBeam3 != null) _distBeam3.Points.Clear();
                return;
            }

            // 추적 ON — BoxWait 중인 인원 CorridorEntry로 전환
            _distTracking3 = true;
            if (_personPhase3  == PersonPhase.BoxWait) PersonTransition3 (PersonPhase.CorridorEntry);
            if (_personPhase3b == PersonPhase.BoxWait) PersonTransition3b(PersonPhase.CorridorEntry);
            if (_engineerPhase == PersonPhase.BoxWait) EngineerTransition3(PersonPhase.CorridorEntry);
            // 토글은 추적이 끝날 때 자동 해제 (틱 루프에서)
        }

        // ── 로그아웃 / 종료 ──────────────────────────────────────
        private AdsDataService?   _adsService;
        private readonly Dictionary<int, SensorDashboard?>   _dashboards = new();
        private readonly Dictionary<int, SensorGraphWindow?> _graphs     = new();
        // ADS 연결 버튼: 연결 중이면 해제, 미연결이면 백그라운드 스레드로 연결 시도
        private void BtnAdsConnect_Click(object sender, RoutedEventArgs e)
        {
            // 이미 연결됨 → 수동 해제
            if (_adsService?.IsConnected == true)
            {
                _adsService.Disconnect();
                btnAdsConnect.Content    = "🔌 ADS 연결";
                btnAdsConnect.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;
                return;
            }

            // 자동 연결이 아직 진행 중이거나 실패한 경우 → 수동 재시도
            btnAdsConnect.Content    = "⏳ 연결 중...";
            btnAdsConnect.IsEnabled  = false;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _adsService?.Dispose();
                    _adsService = new AdsDataService();
                    _adsService.StatusChanged += msg =>
                        Dispatcher.Invoke(() => btnAdsConnect.ToolTip = msg);

                    _adsService.Connect(_sensorService1!, _sensorService3, amsNetId: "127.0.0.1.1.1", port: 851);

                    Dispatcher.Invoke(() =>
                    {
                        btnAdsConnect.Content    = "✔ ADS 연결됨";
                        btnAdsConnect.Foreground = System.Windows.Media.Brushes.Cyan;
                        btnAdsConnect.IsEnabled  = true;
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        btnAdsConnect.Content    = "🔌 ADS 연결";
                        btnAdsConnect.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;
                        btnAdsConnect.IsEnabled  = true;
                        btnAdsConnect.ToolTip    = $"연결 실패: {ex.Message}";
                    });
                }
            });
        }

        // 대시보드 버튼: 센서 대시보드 창 열기 (이미 열려있으면 포커스만 이동)
        private void BtnDashboard_Click(object sender, RoutedEventArgs e)
            => OpenDashboard(0);

        // 대시보드 창: 이미 열려있으면 Activate, 없으면 새로 생성 (분리창도 동일 경로로 관리)
        public void OpenDashboard(int filter)
        {
            if (!_dashboards.TryGetValue(filter, out var win) || win == null || !win.IsLoaded)
            {
                win = new SensorDashboard(_sensorService1!, _sensorService2!, filter) { Owner = this };
                win.Show();
                _dashboards[filter] = win;
            }
            else win.Activate();
        }

        // 실시간 그래프 버튼: 센서 그래프 창 열기
        private void BtnGraph_Click(object sender, RoutedEventArgs e)
            => OpenGraph(0);

        // 그래프 창: 이미 열려있으면 Activate, 없으면 새로 생성
        private void OpenGraph(int filter)
        {
            if (!_graphs.TryGetValue(filter, out var win) || win == null || !win.IsLoaded)
            {
                win = new SensorGraphWindow(_sensorService1!, _sensorService2!, filter) { Owner = this };
                win.Show();
                _graphs[filter] = win;
            }
            else win.Activate();
        }

        // ── 클린룸 선택 표시 ────────────────────────────────────────────────────
        // CR1/CR2/CR3 토글 버튼 클릭 시 레이아웃 재계산
        private void TglShowRoom_Click(object sender, RoutedEventArgs e) => UpdateRoomLayout(sender);

        // 표시할 클린룸 수에 따라 레이아웃 결정:
        //   3개 → 2열 2행 (CR1 상좌, CR2 상우, CR3 하좌)
        //   2개 → 2열 1행 (표시 패널을 좌·우로 배치)
        //   1개 → 1열 1행 (전체 폭)
        private void UpdateRoomLayout(object? sender = null)
        {
            bool s1 = false; // 기존 CR1 패널 삭제 예정 — 항상 숨김
            bool s2 = tglShowCR2.IsChecked == true;
            bool s3 = tglShowCR3.IsChecked == true; // 새 클린룸 1 (표시명 CR1)

            // 최소 1개는 항상 표시
            if (!s2 && !s3)
            {
                if (sender == tglShowCR2) { tglShowCR2.IsChecked = true; s2 = true; }
                else                      { tglShowCR3.IsChecked = true; s3 = true; }
            }

            panelCR1.Visibility = Visibility.Collapsed; // 항상 숨김
            panelCR2.Visibility = s2 ? Visibility.Visible : Visibility.Collapsed;
            panelCR3.Visibility = s3 ? Visibility.Visible : Visibility.Collapsed;

            int count = (s1 ? 1 : 0) + (s2 ? 1 : 0) + (s3 ? 1 : 0);

            var H560 = new System.Windows.GridLength(560);
            var Gap  = new System.Windows.GridLength(16);
            var Zero = new System.Windows.GridLength(0);
            var Star = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);

            if (count == 3)
            {
                // 2열 2행: CR1 상좌, CR2 상우, CR3 하좌
                Grid.SetRow(panelCR1, 0); Grid.SetColumn(panelCR1, 0); Grid.SetColumnSpan(panelCR1, 1);
                Grid.SetRow(panelCR2, 0); Grid.SetColumn(panelCR2, 2); Grid.SetColumnSpan(panelCR2, 1);
                Grid.SetRow(panelCR3, 2); Grid.SetColumn(panelCR3, 0); Grid.SetColumnSpan(panelCR3, 1);

                bodyGrid.RowDefinitions[0].Height = H560;
                bodyGrid.RowDefinitions[1].Height = Gap;
                bodyGrid.RowDefinitions[2].Height = H560;
                bodyGrid.ColumnDefinitions[0].Width = Star;
                bodyGrid.ColumnDefinitions[1].Width = Gap;
                bodyGrid.ColumnDefinitions[2].Width = Star;
            }
            else if (count == 2)
            {
                // 2열 1행: 표시 패널을 순서대로 좌(col0)·우(col2)에 배치
                var visible = new[] { (show: s1, panel: panelCR1),
                                      (show: s2, panel: panelCR2),
                                      (show: s3, panel: panelCR3) }
                              .Where(x => x.show).Select(x => x.panel).ToArray();

                Grid.SetRow(visible[0], 0); Grid.SetColumn(visible[0], 0); Grid.SetColumnSpan(visible[0], 1);
                Grid.SetRow(visible[1], 0); Grid.SetColumn(visible[1], 2); Grid.SetColumnSpan(visible[1], 1);

                bodyGrid.RowDefinitions[0].Height = H560;
                bodyGrid.RowDefinitions[1].Height = Zero;
                bodyGrid.RowDefinitions[2].Height = Zero;
                bodyGrid.ColumnDefinitions[0].Width = Star;
                bodyGrid.ColumnDefinitions[1].Width = Gap;
                bodyGrid.ColumnDefinitions[2].Width = Star;
            }
            else
            {
                // 1열 1행: 전체 폭 사용
                var panel = s1 ? panelCR1 : (s2 ? panelCR2 : panelCR3);
                Grid.SetRow(panel, 0); Grid.SetColumn(panel, 0); Grid.SetColumnSpan(panel, 3);

                bodyGrid.RowDefinitions[0].Height = H560;
                bodyGrid.RowDefinitions[1].Height = Zero;
                bodyGrid.RowDefinitions[2].Height = Zero;
                bodyGrid.ColumnDefinitions[0].Width = Star;
                bodyGrid.ColumnDefinitions[1].Width = Zero;
                bodyGrid.ColumnDefinitions[2].Width = Zero;
            }
        }

        // 로그아웃: 시계 타이머 정지 → 로그인 창 띄우고 관리자 창 닫기
        private void btnLogout_Click(object sender, RoutedEventArgs e)
        {
            _clockTimer?.Stop();
            new LoginWindow().Show();
            this.Close();
        }

        // CR1 웨이포인트 초기화: 8개 장비 중 랜덤 EqVisitCount개를 순서에 맞게 선택
        private void InitWaypoints()
        {
            // ① 1~6번(인덱스 0~5) 중 랜덤 2~5개 선택 후 셔플
            int pickCount = 3 + _personRng.Next(4); // 3, 4, 5, 6 중 랜덤
            var front = Enumerable.Range(0, 6)
                                  .OrderBy(_ => _personRng.Next())
                                  .Take(pickCount)
                                  .Select(i => AllEquips[i]);
            // ② 7번(PVD)→8번(세정)은 항상 마지막
            _equipWaypoints = front
                              .Append(AllEquips[6])  // ⑦ PVD 스퍼터
                              .Append(AllEquips[7])  // ⑧ 세정기
                              .ToArray();
            _waypointIdx = 0;
        }

        // ── CR1 두 작업자 업데이트 ─────────────────────────────────

        private void UpdateCR1()
        {
            if (_paused1) return;

            _personAnimT += 0.05;
            _personPhaseCount++;

            switch (_personPhase)
            {
                case PersonPhase.CorridorWalk:
                    _personCurrentZ = MoveTowardZ(_personCurrentZ, AuthTriggerZ, _walkSpeed1);
                    if (_personCurrentZ <= AuthTriggerZ + 0.001)
                        PersonTransition(PersonPhase.AuthWait);
                    break;
                case PersonPhase.AuthWait:
                    break;
                case PersonPhase.DoorOpening:
                    _doorOpenOffset = Math.Min(_doorOpenOffset + 0.025, AutoDoorOpenX);
                    ApplyDoorOffset(_doorOpenOffset);
                    if (_doorOpenOffset >= AutoDoorOpenX)
                        PersonTransition(PersonPhase.CorridorEntry);
                    break;
                case PersonPhase.CorridorEntry:
                    _doorOpenOffset = Math.Max(0.0, _doorOpenOffset - 0.015);
                    ApplyDoorOffset(_doorOpenOffset);
                    _personCurrentZ = MoveTowardZ(_personCurrentZ, AirShowerMidZ, _walkSpeed1);
                    if (_personCurrentZ <= OuterFrontZ)
                        _personCurrentX = MoveTowardZ(_personCurrentX, 0.0, _walkSpeed1);
                    if (_personCurrentZ <= AirShowerMidZ + 0.001 && Math.Abs(_personCurrentX) < 0.05)
                        PersonTransition(PersonPhase.AirShowerEntry);
                    break;
                case PersonPhase.AirShowerEntry:
                    // ※ 에어샤워 압력 조건 주석 처리 (ADS 미연결 임시)
                    // if (_sensorService1?.Current.AirShowerPressure >= AirShowerPressureThreshold && ...)
                    if (_personPhaseCount >= AirShowerWaitTicks)
                        PersonTransition(PersonPhase.WalkToRoom);
                    break;
                case PersonPhase.WalkToRoom:
                    _personCurrentZ = MoveTowardZ(_personCurrentZ, RoomEntryZ, _walkSpeed1);
                    if (_personCurrentZ <= RoomEntryZ + 0.001)
                        PersonTransition(PersonPhase.WalkToEquip);
                    break;
                case PersonPhase.WalkToEquip:
                {
                    var wp = _equipWaypoints[_waypointIdx];
                    _personCurrentZ = MoveTowardZ(_personCurrentZ, wp.z, _walkSpeed1);
                    _personCurrentX = MoveTowardZ(_personCurrentX, wp.x, _walkSpeed1);
                    if (Math.Abs(_personCurrentZ - wp.z) < 0.03 && Math.Abs(_personCurrentX - wp.x) < 0.03)
                        PersonTransition(PersonPhase.WorkingAtEquip);
                    break;
                }
                case PersonPhase.WorkingAtEquip:
                    if (_personPhaseCount >= _nextWorkTicks)
                    {
                        _waypointIdx++;
                        if (_waypointIdx < _equipWaypoints.Length)
                            PersonTransition(PersonPhase.WalkToEquip);
                        else
                        {
                            _waypointIdx = 0;
                            ShowWaferSuccess();
                            PersonTransition(PersonPhase.WalkToExitShower);
                        }
                    }
                    break;
                case PersonPhase.WalkToExitShower:
                    if (_personPhaseCount == 1) RebuildPersonPose(_person1, 0, false);
                    _personCurrentZ = MoveTowardZ(_personCurrentZ, AirShowerMidZ, _walkSpeed1);
                    _personCurrentX = MoveTowardZ(_personCurrentX, 0.0, _walkSpeed1);
                    if (_personCurrentZ >= AirShowerMidZ - 0.001 && Math.Abs(_personCurrentX) < 0.05)
                        PersonTransition(PersonPhase.AirShowerExit);
                    break;
                case PersonPhase.AirShowerExit:
                    if (_personPhaseCount >= AirShowerWaitTicks)
                        PersonTransition(PersonPhase.CorridorExit);
                    break;
                case PersonPhase.CorridorExit:
                    _personCurrentZ = MoveTowardZ(_personCurrentZ, OuterFrontZ, _walkSpeed1);
                    if (_personCurrentZ < OuterFrontZ)
                        _personCurrentX = MoveTowardZ(_personCurrentX, _personStartX, _walkSpeed1);
                    if (_personCurrentZ >= OuterFrontZ - 0.001)
                        PersonTransition(PersonPhase.CorridorExitWalk);
                    break;
                case PersonPhase.CorridorExitWalk:
                    _personCurrentZ = MoveTowardZ(_personCurrentZ, _personStartZ, _walkSpeed1);
                    if (_personCurrentZ >= _personStartZ - 0.001)
                        PersonTransition(PersonPhase.CorridorWait);
                    break;
                case PersonPhase.CorridorWait:
                    // 작업자 재생성 없음 — 한 번 퇴장하면 대기 상태 유지
                    break;
            }

            double off1 = _personCurrentZ - PersonOriginZ;
            if (_personT1 != null) { _personT1.OffsetZ = off1; _personT1.OffsetX = _personCurrentX; }
            RebuildPersonPose(_person1, _personAnimT, _personPhase == PersonPhase.WorkingAtEquip, _personCurrentX);
            UpdateNameLabel(_nameText1, _personCurrentX, -0.37, _personCurrentZ, _cam1,
                            nameCanvas1.ActualWidth, nameCanvas1.ActualHeight);

            // 에어샤워 + 거리 센서
            // inAirShower1 : 작업자가 에어샤워 챔버 안에 있는지 (이동 페이즈 기반, 사람 색상용)
            // airOn1       : 에어샤워 장비 실제 가동 여부 → 입자 농도 센서값으로 결정
            bool inAirShower1 = _personPhase == PersonPhase.AirShowerEntry || _personPhase == PersonPhase.AirShowerExit;
            // 에어샤워 장비 가동: 실제 장비에서 ADS로 전달되는 블로어 압력값으로 판정
            // ※ 에어샤워 주석 처리 (ADS 미연결 임시)
            // bool airOn1 = _sensorService1 != null && _sensorService1.Current.AirShowerPressure > AirShowerPressureThreshold;
            bool airOn1 = false;
            bool inBox1 = _personCurrentZ >= (RZ + 0.55) && _personCurrentZ <= OuterFrontZ;
            if (inBox1 && !inAirShower1)
            {
                if (_sensorService1 != null) _sensorService1.Current.Distance = _personCurrentZ - (RZ + 0.55);
                UpdateDistBeam(_distBeam1, _personCurrentX, _personCurrentZ);
            }
            else { if (_distBeam1 != null) { _distBeam1.Points.Clear(); } }

            // 사람 색상: 챔버 안에 있을 때 트리거 색 (위치 기반 유지)
            if (_person1 != null) _person1.Color = inAirShower1 ? PersonTriggerColor : PersonIdleColor;
            // 에어샤워 장비 가동: 입자 농도 기반
            _airActive1 = airOn1;
            if (airOn1 != _prevAirTriggered)
            {
                _prevAirTriggered = airOn1;
                if (_airDoor1 != null) _airDoor1.Color = airOn1 ? DoorActiveColor : Colors.White;
                if (!airOn1) { if (_airSpray1 != null) _airSpray1.Color = Colors.Transparent; airBanner1.Visibility = Visibility.Collapsed; }
            }

            // ISO 5 등급 배지: 센서 값이 모두 정상 범위일 때만 표시
            bool iso5Ok1 = _sensorService1 != null
                        && _sensorService1.Current.Vibration    <= VibDangerHi
                        && _sensorService1.Current.Temperature  <= TempDangerHi
                        && _sensorService1.Current.Humidity     <= HumDangerHi
                        && _sensorService1.Current.Pressure     >= PressDangerLo;
            iso5Badge1.Visibility = iso5Ok1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCR1B()
        {
            if (_paused1) return;

            if (_personPhase1b == PersonPhase.CorridorWait)
            {
                // 작업자 재생성 없음 — person1b는 등장하지 않음
                return;
                return;
            }

            _personAnimT1b += 0.05;
            _personPhaseCount1b++;

            switch (_personPhase1b)
            {
                case PersonPhase.CorridorWalk:
                    _personCurrentZ1b = MoveTowardZ(_personCurrentZ1b, AuthTriggerZ, _walkSpeed1b);
                    if (_personCurrentZ1b <= AuthTriggerZ + 0.001) PersonTransition1b(PersonPhase.AuthWait);
                    break;
                case PersonPhase.AuthWait:
                    if (_personPhase != PersonPhase.AuthWait && authRequest1.Visibility != Visibility.Visible)
                        ShowAuthRequest1(isEngineer: false);
                    break;
                case PersonPhase.DoorOpening:
                    _doorOpenOffset1b = Math.Min(_doorOpenOffset1b + 0.025, AutoDoorOpenX);
                    ApplyDoorOffset1b(_doorOpenOffset1b);
                    if (_doorOpenOffset1b >= AutoDoorOpenX) PersonTransition1b(PersonPhase.CorridorEntry);
                    break;
                case PersonPhase.CorridorEntry:
                    _doorOpenOffset1b = Math.Max(0.0, _doorOpenOffset1b - 0.015);
                    ApplyDoorOffset1b(_doorOpenOffset1b);
                    _personCurrentZ1b = MoveTowardZ(_personCurrentZ1b, AirShowerMidZ, _walkSpeed1b);
                    if (_personCurrentZ1b <= OuterFrontZ)
                        _personCurrentX1b = MoveTowardZ(_personCurrentX1b, 0.0, _walkSpeed1b);
                    if (_personCurrentZ1b <= AirShowerMidZ + 0.001 && Math.Abs(_personCurrentX1b) < 0.05)
                        PersonTransition1b(PersonPhase.AirShowerEntry);
                    break;
                case PersonPhase.AirShowerEntry:
                    // ※ 에어샤워 압력 조건 주석 처리 (ADS 미연결 임시)
                    if (_personPhaseCount1b >= AirShowerWaitTicks)
                        PersonTransition1b(PersonPhase.WalkToRoom);
                    break;
                case PersonPhase.WalkToRoom:
                    _personCurrentZ1b = MoveTowardZ(_personCurrentZ1b, RoomEntryZ, _walkSpeed1b);
                    if (_personCurrentZ1b <= RoomEntryZ + 0.001) PersonTransition1b(PersonPhase.WalkToEquip);
                    break;
                case PersonPhase.WalkToEquip:
                {
                    var wp = _equipWaypoints1b[_waypointIdx1b];
                    _personCurrentZ1b = MoveTowardZ(_personCurrentZ1b, wp.z, _walkSpeed1b);
                    _personCurrentX1b = MoveTowardZ(_personCurrentX1b, wp.x, _walkSpeed1b);
                    if (Math.Abs(_personCurrentZ1b - wp.z) < 0.03 && Math.Abs(_personCurrentX1b - wp.x) < 0.03)
                        PersonTransition1b(PersonPhase.WorkingAtEquip);
                    break;
                }
                case PersonPhase.WorkingAtEquip:
                    if (_personPhaseCount1b >= _nextWorkTicks1b)
                    {
                        _waypointIdx1b++;
                        if (_waypointIdx1b < _equipWaypoints1b.Length)
                            PersonTransition1b(PersonPhase.WalkToEquip);
                        else { _waypointIdx1b = 0; ShowWaferSuccess1b(); PersonTransition1b(PersonPhase.WalkToExitShower); }
                    }
                    break;
                case PersonPhase.WalkToExitShower:
                    if (_personPhaseCount1b == 1) RebuildPersonPose(_person1b, 0, false);
                    _personCurrentZ1b = MoveTowardZ(_personCurrentZ1b, AirShowerMidZ, _walkSpeed1b);
                    _personCurrentX1b = MoveTowardZ(_personCurrentX1b, 0.0, _walkSpeed1b);
                    if (_personCurrentZ1b >= AirShowerMidZ - 0.001 && Math.Abs(_personCurrentX1b) < 0.05)
                        PersonTransition1b(PersonPhase.AirShowerExit);
                    break;
                case PersonPhase.AirShowerExit:
                    if (_personPhaseCount1b >= AirShowerWaitTicks) PersonTransition1b(PersonPhase.CorridorExit);
                    break;
                case PersonPhase.CorridorExit:
                    _personCurrentZ1b = MoveTowardZ(_personCurrentZ1b, OuterFrontZ, _walkSpeed1b);
                    if (_personCurrentZ1b < OuterFrontZ)
                        _personCurrentX1b = MoveTowardZ(_personCurrentX1b, _personStartX1b, _walkSpeed1b);
                    if (_personCurrentZ1b >= OuterFrontZ - 0.001) PersonTransition1b(PersonPhase.CorridorExitWalk);
                    break;
                case PersonPhase.CorridorExitWalk:
                    _personCurrentZ1b = MoveTowardZ(_personCurrentZ1b, _personStartZ1b, _walkSpeed1b);
                    if (_personCurrentZ1b >= _personStartZ1b - 0.001) PersonTransition1b(PersonPhase.CorridorWait);
                    break;
            }

            double off1b = _personCurrentZ1b - PersonOriginZ;
            if (_personT1b != null) { _personT1b.OffsetZ = off1b; _personT1b.OffsetX = _personCurrentX1b; }
            RebuildPersonPose(_person1b, _personAnimT1b, _personPhase1b == PersonPhase.WorkingAtEquip, _personCurrentX1b);
            if (_person1b != null) _person1b.Color = PersonIdleColor;
            UpdateNameLabel(_nameText1b, _personCurrentX1b, -0.37, _personCurrentZ1b, _cam1,
                            nameCanvas1.ActualWidth, nameCanvas1.ActualHeight);
        }

        private void PersonTransition1b(PersonPhase next)
        {
            _personPhase1b      = next;
            _personPhaseCount1b = 0;
            switch (next)
            {
                case PersonPhase.WalkToEquip:
                    if (_waypointIdx1b == 0) InitWaypoints1b();
                    break;
                case PersonPhase.WorkingAtEquip:
                    _personAnimT1b   = 0;
                    _nextWorkTicks1b = EqWorkMinTick + _personRng.Next(EqWorkRndTick + 1);
                    break;
                case PersonPhase.CorridorWait:
                    _nextWaitTicks1b = 80 + _personRng.Next(160);
                    _cr1bCountdown   = -3;
                    if (_person1b    != null) _person1b.Color    = PersonIdleColor;
                    if (_nameText1b  != null) _nameText1b.Text   = "";
                    break;
                case PersonPhase.CorridorWalk:
                    int ns1b;
                    do { ns1b = _personRng.Next(3); } while (ns1b == _personSectorIdx);
                    _personSectorIdx1b = ns1b;
                    (_personStartX1b, _personStartZ1b) = Sectors[_personSectorIdx1b];
                    _personCurrentX1b = _personStartX1b; _personCurrentZ1b = _personStartZ1b;
                    // Transform 즉시 갱신 — 색이 보이기 전에 올바른 위치로 이동
                    if (_personT1b != null) { _personT1b.OffsetZ = _personCurrentZ1b - PersonOriginZ; _personT1b.OffsetX = _personCurrentX1b; }
                    RebuildPersonPose(_person1b, 0, false, _personCurrentX1b);
                    if (_person1b != null) _person1b.Color = PersonIdleColor;
                    break;
                case PersonPhase.AuthWait:
                    break;
                case PersonPhase.DoorOpening:
                    authRequest1.Visibility = Visibility.Collapsed;
                    authPopup1.Visibility   = Visibility.Collapsed;
                    authInput1.Text         = string.Empty;
                    authError1.Visibility   = Visibility.Collapsed;
                    _doorOpenOffset1b = 0.0;
                    break;
                case PersonPhase.CorridorEntry:
                    // 문 서서히 닫힘 — 업데이트 루프에서 처리
                    break;
                case PersonPhase.AirShowerEntry:
                    SimulateAirShowerOn(_sensorService1);
                    break;
                case PersonPhase.AirShowerExit:
                    SimulateAirShowerOff(_sensorService1);
                    break;
                case PersonPhase.CorridorExitWalk:
                    _personCurrentX1b = _personStartX1b;
                    CloseDoor1b();
                    break;
            }
        }

        private void ApplyDoorOffset1b(double offset)
        {
            int s = _personSectorIdx1b;
            if (_doorLeftT1[s]  != null) _doorLeftT1[s]!.OffsetX  = -offset;
            if (_doorRightT1[s] != null) _doorRightT1[s]!.OffsetX =  offset;
        }

        private void CloseDoor1b()
        {
            _doorOpenOffset1b = 0.0;
            ApplyDoorOffset1b(0.0);
        }

        private void InitWaypoints1b()
        {
            int pickCount = 3 + _personRng.Next(4);
            var front = Enumerable.Range(0, 6)
                .OrderBy(_ => _personRng.Next()).Take(pickCount).OrderBy(i => i)
                .Select(i => AllEquips[i]).ToList();
            _equipWaypoints1b = front.ToArray();
        }

        private void ShowWaferSuccess1b()
        {
            string visited = string.Join(" → ", _equipWaypoints1b.Select(w => w.label));
            AddWaferBanner("CR1-B", visited);
        }

        // CR2 상태 머신 업데이트 (50ms 틱): 섹터 → 에어샤워 → 클린룸 (CR1과 동일 이동 로직)
        private void UpdateCR2()
        {
            if (_paused2) return;
            _p2AnimT += 0.05;
            _p2PhaseCount++;

            switch (_cr2Phase)
            {
                case CR2Phase.Hidden:
                    if (_person2 != null) { _person2.Points.Clear(); _person2.Color = Colors.Transparent; }
                    return;

                case CR2Phase.CorridorWalk:
                    _p2Z = MoveTowardZ(_p2Z, AuthTriggerZ, PersonWalkSpeed);
                    if (_p2Z <= AuthTriggerZ + 0.001)
                        CR2Transition(CR2Phase.AuthWait);  // 문 앞 정지 → 인증 배너 표시
                    break;

                case CR2Phase.AuthWait:
                    // 인증 배너가 표시된 채 정지 — BtnAllow2_Click에서 ShowAuthPopup2() 호출
                    break;

                case CR2Phase.DoorOpening:
                    _doorOpenOffset2 = Math.Min(_doorOpenOffset2 + 0.025, AutoDoorOpenX);
                    ApplyDoorOffset2(_doorOpenOffset2);
                    if (_doorOpenOffset2 >= AutoDoorOpenX)
                        CR2Transition(CR2Phase.CorridorEntry);
                    break;

                case CR2Phase.CorridorEntry:
                    // 문은 거리 아이콘 클릭 시 닫힘 (_doorClosing2 플래그) — 여기서 자동 닫지 않음
                    _p2Z = MoveTowardZ(_p2Z, AirShowerMidZ, PersonWalkSpeed);
                    if (_p2Z <= OuterFrontZ)
                        _p2X = MoveTowardZ(_p2X, 0.0, PersonWalkSpeed);
                    if (_p2Z <= AirShowerMidZ + 0.001 && Math.Abs(_p2X) < 0.05)
                        CR2Transition(CR2Phase.BoxIdle);   // 에어샤워 입구 정지
                    break;

                case CR2Phase.BoxIdle:
                    // 압력 ≥ 0.08 이면 자동으로 에어샤워 진입
                    if ((_sensorService2?.Current.Pressure ?? 0.0) >= AirShowerPressureThreshold)
                        CR2Transition(CR2Phase.AirShowerEntry);
                    break;

                case CR2Phase.AirShowerEntry:
                {
                    // 진입 에어샤워: 압력 ≥ 0.08 확인 후 가동 → 2초 후 통과
                    // ADS 연결 시: 블로어 출력 압력(AirShowerPressure) 사용
                    // 시뮬레이션 시: 센서 대시보드에 표시되는 차압(Pressure) 사용 (항상 ≥ 0.08)
                    double ashPressure = _sensorService2?.Current.Pressure ?? 0.0;
                    if (!_airActive2 && ashPressure >= AirShowerPressureThreshold)
                    {
                        _airActive2   = true;
                        _p2PhaseCount = 0;
                        SetCR2Status("💨  에어샤워 가동 중…");
                    }
                    if (_airActive2 && _p2PhaseCount >= AirShowerWaitTicks)
                    {
                        _airActive2 = false;
                        SimulateAirShowerOff(_sensorService2);
                        CR2Transition(CR2Phase.WalkToRoom);
                    }
                    break;
                }

                case CR2Phase.WalkToRoom:
                    _p2Z = MoveTowardZ(_p2Z, RoomEntryZ, PersonWalkSpeed);
                    if (_p2Z <= RoomEntryZ + 0.001)
                        CR2Transition(CR2Phase.Idle);
                    break;

                case CR2Phase.Idle:
                    break;

                case CR2Phase.WalkToEquip:
                    if (_p2Target.HasValue)
                    {
                        _p2Z = MoveTowardZ(_p2Z, _p2Target.Value.z, PersonWalkSpeed);
                        _p2X = MoveTowardZ(_p2X, _p2Target.Value.x, PersonWalkSpeed);
                        if (Math.Abs(_p2Z - _p2Target.Value.z) < 0.03
                         && Math.Abs(_p2X - _p2Target.Value.x) < 0.03)
                        {
                            _p2Z = _p2Target.Value.z;
                            _p2X = _p2Target.Value.x;
                            CR2Transition(CR2Phase.AtEquip);
                        }
                    }
                    break;

                case CR2Phase.AtEquip:
                    break;

                case CR2Phase.WalkToExitShower:
                    if (_p2PhaseCount == 1) RebuildPersonPose(_person2, 0, false, _p2X);
                    _p2Z = MoveTowardZ(_p2Z, AirShowerMidZ, PersonWalkSpeed);
                    _p2X = MoveTowardZ(_p2X, 0.0, PersonWalkSpeed);
                    if (_p2Z >= AirShowerMidZ - 0.001 && Math.Abs(_p2X) < 0.05)
                        CR2Transition(CR2Phase.AirShowerExit);
                    break;

                case CR2Phase.AirShowerExit:
                    // 퇴실 에어샤워: 대기 없이 즉시 통과
                    CR2Transition(CR2Phase.CorridorExit);
                    break;

                case CR2Phase.CorridorExit:
                    _p2Z = MoveTowardZ(_p2Z, OuterFrontZ, PersonWalkSpeed);
                    if (_p2Z < OuterFrontZ)
                        _p2X = MoveTowardZ(_p2X, Sectors[_p2SectorIdx].x, PersonWalkSpeed);
                    if (_p2Z >= OuterFrontZ - 0.001)
                        CR2Transition(CR2Phase.CorridorExitWalk);
                    break;

                case CR2Phase.CorridorExitWalk:
                    _p2Z = MoveTowardZ(_p2Z, Sectors[_p2SectorIdx].z, PersonWalkSpeed);
                    if (_p2Z >= Sectors[_p2SectorIdx].z - 0.001)
                        CR2Transition(CR2Phase.Hidden);
                    break;
            }

            if (_personT2 != null) { _personT2.OffsetZ = _p2Z - PersonOriginZ; _personT2.OffsetX = _p2X; }

            bool cr2Working = _cr2Phase == CR2Phase.AtEquip && _p2Target.HasValue;
            RebuildPersonPose(_person2, _p2AnimT, cr2Working, _p2X);

            bool inAirShower2 = _cr2Phase == CR2Phase.AirShowerEntry || _cr2Phase == CR2Phase.AirShowerExit;
            if (_person2 != null)
                _person2.Color = inAirShower2 ? PersonTriggerColor : PersonIdleColor2;
        }

        // CR3 상태 머신 업데이트: CR1과 동일 로직, 필드만 3 suffix로 분리
        private void UpdateCR3()
        {
            if (_paused3) return;

            _personAnimT3 += 0.05;
            _personPhaseCount3++;

            switch (_personPhase3)
            {
                case PersonPhase.CorridorWalk:
                    _personCurrentZ3 = MoveTowardZ(_personCurrentZ3, AuthTriggerZ, _walkSpeed3);
                    if (_personCurrentZ3 <= AuthTriggerZ + 0.001)
                        PersonTransition3(PersonPhase.AuthWait);
                    break;

                case PersonPhase.AuthWait:
                    break;

                case PersonPhase.DoorOpening:
                    _doorOpenOffset3 = Math.Min(_doorOpenOffset3 + 0.025, AutoDoorOpenX);
                    ApplyDoorOffset3(_doorOpenOffset3);
                    if (_doorOpenOffset3 >= AutoDoorOpenX)
                        PersonTransition3(PersonPhase.BoxWait);  // 박스 진입 → 즉시 정지
                    break;

                case PersonPhase.BoxWait:
                    // 박스 라인(OuterFrontZ)까지 이동 후 정지 — 📡거리 버튼 클릭 시 진행
                    if (_personCurrentZ3 > OuterFrontZ + 0.001)
                        _personCurrentZ3 = MoveTowardZ(_personCurrentZ3, OuterFrontZ, _walkSpeed3);
                    // else: 대기 (TglDist3_Click에서 CorridorEntry로 전환)
                    break;

                case PersonPhase.CorridorEntry:
                    _doorOpenOffset3 = Math.Max(0.0, _doorOpenOffset3 - 0.015);
                    ApplyDoorOffset3(_doorOpenOffset3);
                    _personCurrentZ3 = MoveTowardZ(_personCurrentZ3, AirShowerMidZ, _walkSpeed3);
                    if (_personCurrentZ3 <= OuterFrontZ)
                        _personCurrentX3 = MoveTowardZ(_personCurrentX3, 0.0, _walkSpeed3);
                    if (_personCurrentZ3 <= AirShowerMidZ + 0.001 && Math.Abs(_personCurrentX3) < 0.05)
                        PersonTransition3(PersonPhase.AirShowerEntry);
                    break;

                case PersonPhase.AirShowerEntry:
                    // ※ 에어샤워 압력 조건 주석 처리 (ADS 미연결 임시)
                    if (_personPhaseCount3 >= AirShowerWaitTicks)
                        PersonTransition3(PersonPhase.WalkToRoom);
                    break;

                case PersonPhase.WalkToRoom:
                    _personCurrentZ3 = MoveTowardZ(_personCurrentZ3, RoomEntryZ, _walkSpeed3);
                    if (_personCurrentZ3 <= RoomEntryZ + 0.001)
                        PersonTransition3(PersonPhase.WalkToEquip);
                    break;

                case PersonPhase.WalkToEquip:
                {
                    var wp = _equipWaypoints3[_waypointIdx3];
                    _personCurrentZ3 = MoveTowardZ(_personCurrentZ3, wp.z, _walkSpeed3);
                    _personCurrentX3 = MoveTowardZ(_personCurrentX3, wp.x, _walkSpeed3);
                    bool atZ = Math.Abs(_personCurrentZ3 - wp.z) < 0.03;
                    bool atX = Math.Abs(_personCurrentX3 - wp.x) < 0.03;
                    if (atZ && atX) PersonTransition3(PersonPhase.WorkingAtEquip);
                    break;
                }

                case PersonPhase.WorkingAtEquip:
                    _cr3TotalWorkTicks++;
                    if (_personPhaseCount3 >= _nextWorkTicks3)
                    {
                        _waypointIdx3++;
                        if (_waypointIdx3 < _equipWaypoints3.Length)
                        {
                            PersonTransition3(PersonPhase.WalkToEquip);
                        }
                        else
                        {
                            // 모든 장비 방문 완료 → 웨이퍼 생산 성공 배너
                            _cr3TotalWorkTicks = 0;
                            _waypointIdx3 = 0;
                            ShowWaferSuccess3();
                            PersonTransition3(PersonPhase.WalkToExitShower);
                        }
                    }
                    break;

                case PersonPhase.WalkToExitShower:
                    if (_personPhaseCount3 == 1) RebuildPersonPose(_person3, 0, false);
                    _personCurrentZ3 = MoveTowardZ(_personCurrentZ3, AirShowerMidZ, _walkSpeed3);
                    _personCurrentX3 = MoveTowardZ(_personCurrentX3, 0.0, _walkSpeed3);
                    if (_personCurrentZ3 >= AirShowerMidZ - 0.001 && Math.Abs(_personCurrentX3) < 0.05)
                        PersonTransition3(PersonPhase.AirShowerExit);
                    break;

                case PersonPhase.AirShowerExit:
                    if (_personPhaseCount3 >= AirShowerWaitTicks)
                        PersonTransition3(PersonPhase.CorridorExit);
                    break;

                case PersonPhase.CorridorExit:
                    _personCurrentZ3 = MoveTowardZ(_personCurrentZ3, OuterFrontZ, _walkSpeed3);
                    if (_personCurrentZ3 < OuterFrontZ)
                        _personCurrentX3 = MoveTowardZ(_personCurrentX3, _personStartX3, _walkSpeed3);
                    if (_personCurrentZ3 >= OuterFrontZ - 0.001)
                        PersonTransition3(PersonPhase.CorridorExitWalk);
                    break;

                case PersonPhase.CorridorExitWalk:
                    _personCurrentZ3 = MoveTowardZ(_personCurrentZ3, _personStartZ3, _walkSpeed3);
                    if (_personCurrentZ3 >= _personStartZ3 - 0.001)
                        PersonTransition3(PersonPhase.CorridorWait);
                    break;

                case PersonPhase.CorridorWait:
                    // 작업자 재생성 없음 — 한 번 퇴장하면 대기 상태 유지
                    break;
            }

            // Transform + Pose
            double offset3 = _personCurrentZ3 - PersonOriginZ;
            if (_personT3 != null) { _personT3.OffsetZ = offset3; _personT3.OffsetX = _personCurrentX3; }
            bool inWorkPose3 = _personPhase3 == PersonPhase.WorkingAtEquip;
            RebuildPersonPose(_person3, _personAnimT3, inWorkPose3, _personCurrentX3);
            // 이름 레이블은 Billboard (3D 그룹)가 자동으로 figure와 함께 이동 — 별도 위치 갱신 불필요

            // 에어샤워 트리거 판정
            // inAirShower3 : 작업자가 챔버 안에 있는지 (사람 색상·거리 센서 빔 제어용)
            // airOn3       : 에어샤워 장비 실제 가동 → 입자 농도 센서값으로 결정
            bool inAirShower3 = _personPhase3 == PersonPhase.AirShowerEntry
                             || _personPhase3 == PersonPhase.AirShowerExit;
            // ※ 에어샤워 주석 처리 (ADS 미연결 임시)
            // bool airOn3 = _sensorService3 != null && _sensorService3.Current.AirShowerPressure > AirShowerPressureThreshold;
            bool airOn3 = false;
            // 📡거리 추적 빔 — 추적 중이면 가장 가까운 인원을 향해 빔 연장
            if (_distBeam3 != null)
            {
                if (_distTracking3)
                {
                    // 센서 위치 (AddDistanceSensor 와 동일 계산)
                    const double sensorBz1   = RZ + 0.55 + 0.018;            // 2.568
                    const double sensorTyCen = -RY + RY * 1.70 - 0.015;      // ≈ 0.965
                    const double headY       = -RY + 0.575 + 0.055;          // 머리 높이

                    // 에어샤워 입구(outerZ=2.55) 이전에 있는 인원만 추적
                    const double trackCutZ = RZ + 0.55 + 0.5; // 3.05 이상이면 추적 대상

                    double? bestZ = null, bestX = null;
                    void TryPerson(double pz, double px, LinesVisual3D? fig, PersonPhase ph)
                    {
                        if (fig == null || fig.Color == Colors.Transparent) return;
                        if (pz < trackCutZ) return; // 이미 안으로 들어간 인원 제외
                        if (ph == PersonPhase.CorridorWait || ph == PersonPhase.CorridorExitWalk) return;
                        if (bestZ == null || pz < bestZ) { bestZ = pz; bestX = px; }
                    }
                    TryPerson(_personCurrentZ3,  _personCurrentX3,  _person3,   _personPhase3);
                    TryPerson(_personCurrentZ3b, _personCurrentX3b, _person3b,  _personPhase3b);
                    TryPerson(_engineerCurrentZ, _engineerCurrentX, _personEng, _engineerPhase);

                    if (bestZ != null)
                    {
                        _distBeam3.Points.Clear();
                        _distBeam3.Points.Add(new Point3D(0,          sensorTyCen, sensorBz1));
                        _distBeam3.Points.Add(new Point3D(bestX!.Value, headY,     bestZ.Value));
                    }
                    else
                    {
                        // 추적 대상 없음 → 자동 종료
                        _distBeam3.Points.Clear();
                        _distTracking3       = false;
                        tglDist3.IsChecked   = false;
                    }
                }
                else
                {
                    _distBeam3.Points.Clear();
                }
            }

            // 사람 색상: 챔버 안에 있을 때 트리거 색 (위치 기반 유지)
            if (_person3 != null) _person3.Color = inAirShower3 ? PersonTriggerColor : PersonIdleColor3;
            // 에어샤워 장비 가동: 입자 농도 기반
            _airActive3 = airOn3;
            if (airOn3 != _prevAirTriggered3)
            {
                _prevAirTriggered3 = airOn3;
                if (_airDoor3 != null)  _airDoor3.Color  = airOn3 ? DoorActiveColor : Colors.White;
                if (!airOn3)
                {
                    if (_airSpray3 != null) _airSpray3.Color = Colors.Transparent;
                    airBanner3.Visibility = Visibility.Collapsed;
                }
            }

            // ISO 5 등급 배지: 센서 정상 + 엔지니어 미투입 상태일 때만 표시
            bool iso5Ok3 = _sensorService3 != null
                        && _sensorService3.Current.Vibration    <= VibDangerHi
                        && _sensorService3.Current.Temperature  <= TempDangerHi
                        && _sensorService3.Current.Humidity     <= HumDangerHi
                        && _sensorService3.Current.Pressure     >= PressDangerLo
                        && !_engineerActive
                        && !_engineerTriggerPending
                        && _brokenEquipIndices3.Count == 0;
            iso5Badge3.Visibility = iso5Ok3 ? Visibility.Visible : Visibility.Collapsed;
        }

        // CR3 페이즈 전환 (CR1의 PersonTransition 과 동일 구조, 3 suffix 필드 사용)
        private void PersonTransition3(PersonPhase next)
        {
            _personPhase3      = next;
            _personPhaseCount3 = 0;
            switch (next)
            {
                case PersonPhase.WalkToEquip:
                    if (_waypointIdx3 == 0)
                    {
                        InitWaypoints3();
                        _cr3TotalWorkTicks = 0;   // 새 작업 사이클 시작 시 초기화
                        // 첫 번째 WalkToEquip 진입 시 두 번째 작업자 투입 카운트다운 시작 (10초 = 200틱)
                        if (_cr3bCountdown == -1) _cr3bCountdown = 200;
                    }
                    break;
                case PersonPhase.WorkingAtEquip:
                    _personAnimT3   = 0;
                    _nextWorkTicks3 = 100;  // 5초 고정 (총 3개 × 5초 = 15초)
                    break;
                case PersonPhase.CorridorWait:
                    _nextWaitTicks3 = 80 + _personRng.Next(160);
                    if (_nameBillboard3 != null) _nameBillboard3.Text = ""; // 이름 레이블 초기화
                    break;
                case PersonPhase.CorridorWalk:
                    _personSectorIdx3 = _personRng.Next(3);
                    (_personStartX3, _personStartZ3) = Sectors[_personSectorIdx3];
                    _personCurrentX3 = _personStartX3;
                    _personCurrentZ3 = _personStartZ3;
                    if (_nameBillboard3 != null) _nameBillboard3.Text = "";
                    break;
                case PersonPhase.AuthWait:
                    ShowAuthRequest3(isEngineer: false);
                    break;
                case PersonPhase.DoorOpening:
                    authPopup3.Visibility  = Visibility.Collapsed;
                    authInput3.Text        = string.Empty;
                    authError3.Visibility  = Visibility.Collapsed;
                    _doorOpenOffset3       = 0.0;
                    break;
                case PersonPhase.CorridorEntry:
                    // 문 서서히 닫힘 — 업데이트 루프에서 처리
                    break;
                case PersonPhase.AirShowerEntry:
                    SimulateAirShowerOn(_sensorService3);
                    airShowerBanner3.Visibility = Visibility.Visible;
                    break;
                case PersonPhase.WalkToRoom:
                    airShowerBanner3.Visibility = Visibility.Collapsed;
                    break;
                case PersonPhase.AirShowerExit:
                    SimulateAirShowerOff(_sensorService3);
                    break;
                case PersonPhase.CorridorExitWalk:
                    _personCurrentX3 = _personStartX3;
                    CloseDoor3();
                    break;
            }
        }

        // CR3 자동문 오프셋 적용
        private void ApplyDoorOffset3(double offset)
        {
            int s = _personSectorIdx3;
            if (_doorLeftT3[s]  != null) _doorLeftT3[s]!.OffsetX  = -offset;
            if (_doorRightT3[s] != null) _doorRightT3[s]!.OffsetX =  offset;
        }

        // CR3 자동문 즉시 닫기
        private void CloseDoor3()
        {
            _doorOpenOffset3 = 0.0;
            ApplyDoorOffset3(0.0);
        }

        // CR3 웨이포인트 초기화
        private void InitWaypoints3()
        {
            // 랜덤 3개 장비, 방문 순서도 랜덤 (총 작업 15초 = 3개 × 5초)
            _equipWaypoints3 = Enumerable.Range(0, AllEquips.Length)
                .OrderBy(_ => _personRng.Next()).Take(3)
                .Select(i => AllEquips[i]).ToArray();
        }

        // CR3 웨이퍼 생산 성공 배너 표시
        private void ShowWaferSuccess3()
        {
            string visited = string.Join(" → ", _equipWaypoints3.Select(w => w.label));
            AddWaferBanner("CR3", visited);
        }

        // ── CR3 두 번째 작업자 (3b) ────────────────────────────────
        private void UpdateCR3B()
        {
            if (_paused3) return;

            // 작업자 재생성 없음 — person3b는 등장하지 않음
            if (_personPhase3b == PersonPhase.CorridorWait) return;

            _personAnimT3b += 0.05;
            _personPhaseCount3b++;

            switch (_personPhase3b)
            {
                case PersonPhase.CorridorWalk:
                    _personCurrentZ3b = MoveTowardZ(_personCurrentZ3b, AuthTriggerZ, _walkSpeed3b);
                    if (_personCurrentZ3b <= AuthTriggerZ + 0.001)
                        PersonTransition3b(PersonPhase.AuthWait);
                    break;

                case PersonPhase.AuthWait:
                    // CR3가 AuthWait 중이면 3b는 줄서서 대기 (UI 충돌 방지)
                    if (_personPhase3 != PersonPhase.AuthWait
                        && authRequest3.Visibility != Visibility.Visible)
                        ShowAuthRequest3(isEngineer: false);
                    break;

                case PersonPhase.DoorOpening:
                    _doorOpenOffset3b = Math.Min(_doorOpenOffset3b + 0.025, AutoDoorOpenX);
                    ApplyDoorOffset3b(_doorOpenOffset3b);
                    if (_doorOpenOffset3b >= AutoDoorOpenX)
                        PersonTransition3b(PersonPhase.BoxWait);  // 박스 진입 → 즉시 정지
                    break;

                case PersonPhase.BoxWait:
                    // 박스 라인(OuterFrontZ)까지 이동 후 정지 — 📡거리 버튼 클릭 시 진행
                    if (_personCurrentZ3b > OuterFrontZ + 0.001)
                        _personCurrentZ3b = MoveTowardZ(_personCurrentZ3b, OuterFrontZ, _walkSpeed3b);
                    // else: 대기 (TglDist3_Click에서 CorridorEntry로 전환)
                    break;

                case PersonPhase.CorridorEntry:
                    _doorOpenOffset3b = Math.Max(0.0, _doorOpenOffset3b - 0.015);
                    ApplyDoorOffset3b(_doorOpenOffset3b);
                    _personCurrentZ3b = MoveTowardZ(_personCurrentZ3b, AirShowerMidZ, _walkSpeed3b);
                    if (_personCurrentZ3b <= OuterFrontZ)
                        _personCurrentX3b = MoveTowardZ(_personCurrentX3b, 0.0, _walkSpeed3b);
                    if (_personCurrentZ3b <= AirShowerMidZ + 0.001 && Math.Abs(_personCurrentX3b) < 0.05)
                        PersonTransition3b(PersonPhase.AirShowerEntry);
                    break;

                case PersonPhase.AirShowerEntry:
                    // ※ 에어샤워 압력 조건 주석 처리 (ADS 미연결 임시)
                    if (_personPhaseCount3b >= AirShowerWaitTicks)
                        PersonTransition3b(PersonPhase.WalkToRoom);
                    break;

                case PersonPhase.WalkToRoom:
                    _personCurrentZ3b = MoveTowardZ(_personCurrentZ3b, RoomEntryZ, _walkSpeed3b);
                    if (_personCurrentZ3b <= RoomEntryZ + 0.001)
                        PersonTransition3b(PersonPhase.WalkToEquip);
                    break;

                case PersonPhase.WalkToEquip:
                {
                    var wp = _equipWaypoints3b[_waypointIdx3b];
                    _personCurrentZ3b = MoveTowardZ(_personCurrentZ3b, wp.z, _walkSpeed3b);
                    _personCurrentX3b = MoveTowardZ(_personCurrentX3b, wp.x, _walkSpeed3b);
                    bool atZ = Math.Abs(_personCurrentZ3b - wp.z) < 0.03;
                    bool atX = Math.Abs(_personCurrentX3b - wp.x) < 0.03;
                    if (atZ && atX) PersonTransition3b(PersonPhase.WorkingAtEquip);
                    break;
                }

                case PersonPhase.WorkingAtEquip:
                    if (_personPhaseCount3b >= _nextWorkTicks3b)
                    {
                        _waypointIdx3b++;
                        if (_waypointIdx3b < _equipWaypoints3b.Length)
                            PersonTransition3b(PersonPhase.WalkToEquip);
                        else
                        {
                            _waypointIdx3b = 0;
                            ShowWaferSuccess3b();
                            PersonTransition3b(PersonPhase.WalkToExitShower);
                        }
                    }
                    break;

                case PersonPhase.WalkToExitShower:
                    if (_personPhaseCount3b == 1) RebuildPersonPose(_person3b, 0, false);
                    _personCurrentZ3b = MoveTowardZ(_personCurrentZ3b, AirShowerMidZ, _walkSpeed3b);
                    _personCurrentX3b = MoveTowardZ(_personCurrentX3b, 0.0, _walkSpeed3b);
                    if (_personCurrentZ3b >= AirShowerMidZ - 0.001 && Math.Abs(_personCurrentX3b) < 0.05)
                        PersonTransition3b(PersonPhase.AirShowerExit);
                    break;

                case PersonPhase.AirShowerExit:
                    if (_personPhaseCount3b >= AirShowerWaitTicks)
                        PersonTransition3b(PersonPhase.CorridorExit);
                    break;

                case PersonPhase.CorridorExit:
                    _personCurrentZ3b = MoveTowardZ(_personCurrentZ3b, OuterFrontZ, _walkSpeed3b);
                    if (_personCurrentZ3b < OuterFrontZ)
                        _personCurrentX3b = MoveTowardZ(_personCurrentX3b, _personStartX3b, _walkSpeed3b);
                    if (_personCurrentZ3b >= OuterFrontZ - 0.001)
                        PersonTransition3b(PersonPhase.CorridorExitWalk);
                    break;

                case PersonPhase.CorridorExitWalk:
                    _personCurrentZ3b = MoveTowardZ(_personCurrentZ3b, _personStartZ3b, _walkSpeed3b);
                    if (_personCurrentZ3b >= _personStartZ3b - 0.001)
                        PersonTransition3b(PersonPhase.CorridorWait);
                    break;
            }

            // Transform + Pose
            double offset3b = _personCurrentZ3b - PersonOriginZ;
            if (_personT3b != null) { _personT3b.OffsetZ = offset3b; _personT3b.OffsetX = _personCurrentX3b; }
            bool inWorkPose3b = _personPhase3b == PersonPhase.WorkingAtEquip;
            RebuildPersonPose(_person3b, _personAnimT3b, inWorkPose3b, _personCurrentX3b);

            // 작업자 색상 (에어샤워 중이면 트리거 색)
            bool airOn3b = _personPhase3b == PersonPhase.AirShowerEntry
                        || _personPhase3b == PersonPhase.AirShowerExit;
            if (_person3b != null) _person3b.Color = airOn3b ? PersonTriggerColor : PersonIdleColor3;
        }

        private void PersonTransition3b(PersonPhase next)
        {
            _personPhase3b      = next;
            _personPhaseCount3b = 0;
            switch (next)
            {
                case PersonPhase.WalkToEquip:
                    if (_waypointIdx3b == 0) InitWaypoints3b();
                    break;
                case PersonPhase.WorkingAtEquip:
                    _personAnimT3b   = 0;
                    _nextWorkTicks3b = 100;  // 5초 고정
                    break;
                case PersonPhase.CorridorWait:
                    _nextWaitTicks3b  = 80 + _personRng.Next(160);
                    _cr3bCountdown    = -3; // 이후 사이클은 일반 대기
                    if (_person3b != null) _person3b.Color = PersonIdleColor3;
                    if (_nameBillboard3b != null) _nameBillboard3b.Text = ""; // 이름 레이블 초기화
                    break;
                case PersonPhase.CorridorWalk:
                    // 첫 번째 작업자와 다른 섹터 선택
                    int newSector;
                    do { newSector = _personRng.Next(3); }
                    while (newSector == _personSectorIdx3);
                    _personSectorIdx3b = newSector;
                    (_personStartX3b, _personStartZ3b) = Sectors[_personSectorIdx3b];
                    _personCurrentX3b = _personStartX3b;
                    _personCurrentZ3b = _personStartZ3b;
                    // Transform 즉시 갱신 — 색이 보이기 전에 올바른 위치로 이동
                    if (_personT3b != null) { _personT3b.OffsetZ = _personCurrentZ3b - PersonOriginZ; _personT3b.OffsetX = _personCurrentX3b; }
                    RebuildPersonPose(_person3b, 0, false, _personCurrentX3b);
                    if (_person3b != null) _person3b.Color = PersonIdleColor3;
                    if (_nameBillboard3b != null) _nameBillboard3b.Text = "";
                    break;
                case PersonPhase.AuthWait:
                    // CR3가 이미 AuthWait 중이면 CR3가 먼저 처리될 때까지 잠시 대기
                    // (authRequest3은 UpdateCR3B에서 표시)
                    break;
                case PersonPhase.DoorOpening:
                    authRequest3.Visibility = Visibility.Collapsed;
                    btnAllow3.Visibility    = Visibility.Collapsed;
                    authPopup3.Visibility   = Visibility.Collapsed;
                    authInput3.Text         = string.Empty;
                    authError3.Visibility   = Visibility.Collapsed;
                    _doorOpenOffset3b = 0.0;
                    break;
                case PersonPhase.CorridorEntry:
                    // 문 서서히 닫힘 — 업데이트 루프에서 처리
                    break;
                case PersonPhase.AirShowerEntry:
                    SimulateAirShowerOn(_sensorService3);
                    airShowerBanner3.Visibility = Visibility.Visible;
                    break;
                case PersonPhase.WalkToRoom:
                    airShowerBanner3.Visibility = Visibility.Collapsed;
                    break;
                case PersonPhase.AirShowerExit:
                    SimulateAirShowerOff(_sensorService3);
                    break;
                case PersonPhase.CorridorExitWalk:
                    _personCurrentX3b = _personStartX3b;
                    CloseDoor3b();
                    break;
            }
        }

        private void ApplyDoorOffset3b(double offset)
        {
            int s = _personSectorIdx3b;
            if (_doorLeftT3[s]  != null) _doorLeftT3[s]!.OffsetX  = -offset;
            if (_doorRightT3[s] != null) _doorRightT3[s]!.OffsetX =  offset;
        }

        private void CloseDoor3b()
        {
            _doorOpenOffset3b = 0.0;
            ApplyDoorOffset3b(0.0);
        }

        private void InitWaypoints3b()
        {
            _equipWaypoints3b = Enumerable.Range(0, AllEquips.Length)
                .OrderBy(_ => _personRng.Next()).Take(3)
                .Select(i => AllEquips[i]).ToArray();
        }

        private void ShowWaferSuccess3b()
        {
            string visited = string.Join(" → ", _equipWaypoints3b.Select(w => w.label));
            AddWaferBanner("CR3-B", visited);
        }

        // ── CR3 장비엔지니어 ───────────────────────────────────────
        private void UpdateEngineer3()
        {
            if (_paused3) return;

            // ── 진동 센서 2회 연속 위험 감지 시 엔지니어 투입 ──
            // _engineerTriggerPending: DataUpdated 이벤트에서 설정, 여기서 소비
            if (!_engineerActive)
            {
                if (_engineerTriggerPending)
                {
                    _engineerTriggerPending = false;
                    _engineerActive         = true;
                    // _engineerTarget는 DataUpdated에서 문제 장비로 이미 설정됨
                    _engineerWorkTicks      = 100 + _personRng.Next(100);
                    if (_personEng != null) _personEng.Color = EngineerColor;
                    EngineerTransition3(PersonPhase.CorridorWalk);
                }
                return;
            }

            // ── 엔지니어 활성 상태 머신 ──
            _engineerAnimT += 0.05;
            _engineerPhaseCount++;

            switch (_engineerPhase)
            {
                case PersonPhase.CorridorWalk:
                    _engineerCurrentZ = MoveTowardZ(_engineerCurrentZ, AuthTriggerZ, _walkSpeedEng);
                    if (_engineerCurrentZ <= AuthTriggerZ + 0.001)
                        EngineerTransition3(PersonPhase.AuthWait);
                    break;

                case PersonPhase.AuthWait:
                    // CR3, 3b 모두 AuthWait 아닐 때만 요청 배너 표시
                    if (_personPhase3  != PersonPhase.AuthWait
                     && _personPhase3b != PersonPhase.AuthWait
                     && authRequest3.Visibility != Visibility.Visible)
                        ShowAuthRequest3(isEngineer: true);
                    break;

                case PersonPhase.DoorOpening:
                    _doorOpenOffsetEng = Math.Min(_doorOpenOffsetEng + 0.025, AutoDoorOpenX);
                    ApplyDoorOffsetEng3(_doorOpenOffsetEng);
                    if (_doorOpenOffsetEng >= AutoDoorOpenX)
                        EngineerTransition3(PersonPhase.BoxWait);  // 박스 진입 → 즉시 정지
                    break;

                case PersonPhase.BoxWait:
                    // 박스 라인(OuterFrontZ)까지 이동 후 정지 — 📡거리 버튼 클릭 시 진행
                    if (_engineerCurrentZ > OuterFrontZ + 0.001)
                        _engineerCurrentZ = MoveTowardZ(_engineerCurrentZ, OuterFrontZ, _walkSpeedEng);
                    // else: 대기 (TglDist3_Click에서 CorridorEntry로 전환)
                    break;

                case PersonPhase.CorridorEntry:
                    _doorOpenOffsetEng = Math.Max(0.0, _doorOpenOffsetEng - 0.015);
                    ApplyDoorOffsetEng3(_doorOpenOffsetEng);
                    _engineerCurrentZ = MoveTowardZ(_engineerCurrentZ, AirShowerMidZ, _walkSpeedEng);
                    if (_engineerCurrentZ <= OuterFrontZ)
                        _engineerCurrentX = MoveTowardZ(_engineerCurrentX, 0.0, _walkSpeedEng);
                    if (_engineerCurrentZ <= AirShowerMidZ + 0.001 && Math.Abs(_engineerCurrentX) < 0.05)
                        EngineerTransition3(PersonPhase.AirShowerEntry);
                    break;

                case PersonPhase.AirShowerEntry:
                    // ※ 에어샤워 압력 조건 주석 처리 (ADS 미연결 임시)
                    if (_engineerPhaseCount >= AirShowerWaitTicks)
                        EngineerTransition3(PersonPhase.WalkToEquip);
                    break;

                case PersonPhase.WalkToEquip:
                    // 문제 센서(장비) 위치로 이동
                    _engineerCurrentZ = MoveTowardZ(_engineerCurrentZ, _engineerTarget.z, _walkSpeedEng);
                    _engineerCurrentX = MoveTowardZ(_engineerCurrentX, _engineerTarget.x, _walkSpeedEng);
                    if (Math.Abs(_engineerCurrentZ - _engineerTarget.z) < 0.03
                     && Math.Abs(_engineerCurrentX - _engineerTarget.x) < 0.03)
                        EngineerTransition3(PersonPhase.WorkingAtEquip);
                    break;

                case PersonPhase.WorkingAtEquip:
                    // 엔지니어가 고장 장비에 완전 접근 후 5초(100틱) 뒤 정비 완료
                    if (_engineerPhaseCount >= CR3WorkerRepairTotal)
                    {
                        CompleteCR1WorkerRepair();
                        EngineerTransition3(PersonPhase.WalkToExitShower);
                    }
                    break;

                case PersonPhase.WalkToExitShower:
                    // 수리 완료 후 에어샤워 방향으로 복귀
                    if (_engineerPhaseCount == 1) RebuildPersonPose(_personEng, 0, false);
                    _engineerCurrentZ = MoveTowardZ(_engineerCurrentZ, AirShowerMidZ, _walkSpeedEng);
                    _engineerCurrentX = MoveTowardZ(_engineerCurrentX, 0.0, _walkSpeedEng);
                    if (_engineerCurrentZ >= AirShowerMidZ - 0.001 && Math.Abs(_engineerCurrentX) < 0.05)
                        EngineerTransition3(PersonPhase.AirShowerExit);
                    break;

                case PersonPhase.AirShowerExit:
                    if (_engineerPhaseCount >= AirShowerWaitTicks)
                        EngineerTransition3(PersonPhase.CorridorExit);
                    break;

                case PersonPhase.CorridorExit:
                    _engineerCurrentZ = MoveTowardZ(_engineerCurrentZ, OuterFrontZ, _walkSpeedEng);
                    if (_engineerCurrentZ < OuterFrontZ)
                        _engineerCurrentX = MoveTowardZ(_engineerCurrentX, _engineerStartX, _walkSpeedEng);
                    if (_engineerCurrentZ >= OuterFrontZ - 0.001)
                        EngineerTransition3(PersonPhase.CorridorExitWalk);
                    break;

                case PersonPhase.CorridorExitWalk:
                    _engineerCurrentZ = MoveTowardZ(_engineerCurrentZ, _engineerStartZ, _walkSpeedEng);
                    if (_engineerCurrentZ >= _engineerStartZ - 0.001)
                    {
                        // 안전망 — 미복원 항목 최종 정리 (정상 흐름에서는 이미 수리 즉시 복원됨)
                        foreach (int bidx in _brokenEquipIndices3)
                        {
                            var (frames, orig) = _equipReg3[bidx];
                            foreach (var frame in frames) frame.Color = orig;
                        }
                        _brokenEquipIndices3.Clear();
                        if (_ffuFailed3)       { RestoreFfuColor3();       _ffuFailed3       = false; }
                        if (_airShowerFailed3) { RestoreAirShowerColor3(); _airShowerFailed3 = false; }
                        // 엔지니어 비활성화
                        _engineerActive   = false;
                        _engineerPhase    = PersonPhase.CorridorWait;
                        if (_personEng  != null) { _personEng.Points.Clear(); _personEng.Color = Colors.Transparent; }
                        if (_nameBillboardEng != null) _nameBillboardEng.Text = ""; // 이름 레이블 초기화
                        return;
                    }
                    break;
            }

            // Transform + Pose
            double offsetEng = _engineerCurrentZ - PersonOriginZ;
            if (_personTEng != null) { _personTEng.OffsetZ = offsetEng; _personTEng.OffsetX = _engineerCurrentX; }
            bool inWorkPoseEng = _engineerPhase == PersonPhase.WorkingAtEquip;
            RebuildPersonPose(_personEng, _engineerAnimT, inWorkPoseEng, _engineerCurrentX);

            // 에어샤워 중 색상
            bool airOnEng = _engineerPhase == PersonPhase.AirShowerEntry
                         || _engineerPhase == PersonPhase.AirShowerExit;
            if (_personEng != null)
                _personEng.Color = airOnEng ? PersonTriggerColor : EngineerColor;
        }

        private void EngineerTransition3(PersonPhase next)
        {
            _engineerPhase      = next;
            _engineerPhaseCount = 0;
            switch (next)
            {
                case PersonPhase.CorridorWalk:
                    // 매 출동마다 시작 위치 초기화 + Transform 즉시 갱신
                    _engineerCurrentX = _engineerStartX;
                    _engineerCurrentZ = _engineerStartZ;
                    if (_personTEng != null) { _personTEng.OffsetZ = _engineerCurrentZ - PersonOriginZ; _personTEng.OffsetX = _engineerCurrentX; }
                    RebuildPersonPose(_personEng, 0, false, _engineerCurrentX);
                    if (_personEng != null) _personEng.Color = EngineerColor;
                    if (_nameBillboardEng != null) _nameBillboardEng.Text = "";
                    break;
                case PersonPhase.DoorOpening:
                    authRequest3.Visibility = Visibility.Collapsed;
                    btnAllow3.Visibility    = Visibility.Collapsed;
                    authPopup3.Visibility   = Visibility.Collapsed;
                    authInput3.Text         = string.Empty;
                    authError3.Visibility   = Visibility.Collapsed;
                    _doorOpenOffsetEng = 0.0;
                    break;
                case PersonPhase.CorridorEntry:
                    // 문 서서히 닫힘 — 업데이트 루프에서 처리
                    break;
                case PersonPhase.AirShowerEntry:
                    SimulateAirShowerOn(_sensorService3);
                    airShowerBanner3.Visibility = Visibility.Visible;
                    break;
                case PersonPhase.WalkToEquip:
                    airShowerBanner3.Visibility = Visibility.Collapsed;
                    break;
                case PersonPhase.AirShowerExit:
                    SimulateAirShowerOff(_sensorService3);
                    break;
                case PersonPhase.WorkingAtEquip:
                {
                    _engineerAnimT = 0;
                    string engLabel = _engineerTarget.label ?? "";
                    repairingBannerText3.Text = $"🔧  엔지니어 장비 점검 중  ·  {engLabel}";
                    repairingBanner3.Visibility = Visibility.Visible;
                    break;
                }
                case PersonPhase.CorridorExitWalk:
                    repairingBanner3.Visibility = Visibility.Collapsed;
                    _engineerCurrentX = _engineerStartX;
                    CloseDoorEng3();
                    break;
            }
        }

        private void ApplyDoorOffsetEng3(double offset)
        {
            int s = _engineerSectorIdx;
            if (_doorLeftT3[s]  != null) _doorLeftT3[s]!.OffsetX  = -offset;
            if (_doorRightT3[s] != null) _doorRightT3[s]!.OffsetX =  offset;
        }

        private void CloseDoorEng3()
        {
            _doorOpenOffsetEng = 0.0;
            ApplyDoorOffsetEng3(0.0);
        }

        // ── CR2 상태 전환 ──────────────────────────────────────────
        // CR2 페이즈 전환: 상태별 UI 초기화
        private void CR2Transition(CR2Phase next)
        {
            _cr2Phase     = next;
            _p2PhaseCount = 0;

            switch (next)
            {
                case CR2Phase.Hidden:
                    _p2Target = null;
                    _cr2WorkerLabel = "";
                    _doorOpenOffset2 = 0.0;
                    ApplyDoorOffset2(0.0);
                    if (_person2 != null) { _person2.Points.Clear(); _person2.Color = Colors.Transparent; }
                    authRequest2.Visibility = Visibility.Collapsed;
                    SetCR2Status("대기 중 (엔지니어 없음)");
                    btnCR2Enter.IsEnabled = true;
                    btnCR2Exit.IsEnabled  = false;
                    tglDist2.IsEnabled    = false;
                    tglDist2.IsChecked    = false;
                    _doorClosing2         = false;
                    if (_distBeam2 != null) _distBeam2.Points.Clear();
                    SetCR2EqButtonsEnabled(false);
                    break;

                case CR2Phase.CorridorWalk:
                    // 섹터 출발점에 스폰
                    _p2X = Sectors[_p2SectorIdx].x;
                    _p2Z = Sectors[_p2SectorIdx].z;
                    if (_personT2 != null) { _personT2.OffsetZ = _p2Z - PersonOriginZ; _personT2.OffsetX = _p2X; }
                    RebuildPersonPose(_person2, 0, false, _p2X);
                    if (_person2 != null) _person2.Color = PersonIdleColor2;
                    SetCR2Status("엔지니어 이동 중 — 문으로 접근...");
                    break;

                case CR2Phase.AuthWait:
                    // 문 앞 정지: 인증 배너 표시
                    authRequest2.Visibility = Visibility.Visible;
                    SetCR2Status("🔐  인증 대기 — 허용 버튼을 클릭하세요");
                    break;

                case CR2Phase.DoorOpening:
                    // 이동 중 — 상태바 유지
                    break;

                case CR2Phase.CorridorEntry:
                    // 엔지니어가 문을 통과했으므로 거리 추적 아이콘 활성화
                    tglDist2.IsEnabled = true;
                    SetCR2Status("📡  거리 아이콘을 클릭하면 문이 닫히고 추적이 시작됩니다");
                    break;

                case CR2Phase.AirShowerEntry:
                    SimulateAirShowerOn(_sensorService2);   // 시뮬레이션: 블로어 압력 상승
                    SetCR2Status("⏳  에어샤워 압력 대기 중…");
                    break;

                case CR2Phase.WalkToRoom:
                    // 이동 중 — 상태바 유지
                    break;

                case CR2Phase.BoxIdle:
                    // 거리 아이콘이 이미 켜져 있으면 바로 에어샤워로 이동
                    if (tglDist2.IsChecked == true)
                    {
                        CR2Transition(CR2Phase.AirShowerEntry);
                        break;
                    }
                    // 에어샤워 입구 정지: 거리 아이콘 클릭 안내
                    tglDist2.IsEnabled = true;
                    SetCR2Status(string.IsNullOrEmpty(_cr2WorkerLabel)
                        ? "📡  거리 아이콘을 클릭하여 에어샤워로 이동하세요"
                        : $"📡  [{_cr2WorkerLabel}]  거리 아이콘을 클릭하세요");
                    break;

                case CR2Phase.Idle:
                    SetCR2Status(string.IsNullOrEmpty(_cr2WorkerLabel) ? "대기 중 — 장비를 선택하세요" : $"대기 중 [{_cr2WorkerLabel}] — 장비를 선택하세요");
                    SetCR2EqButtonsEnabled(true);
                    btnCR2Exit.IsEnabled = true;
                    break;

                case CR2Phase.WalkToEquip:
                    SetCR2Status($"이동 중  →  {_p2Target?.label ?? "?"}");
                    break;

                case CR2Phase.AtEquip:
                    // 고장 장비에 도착했을 때만 수리 카운트다운 시작 (인덱스로 비교)
                    if (_cr2FaultFrames != null && _cr2RepairTicks < 0
                        && _cr2TargetIdx == _cr2FaultIdx)
                    {
                        _cr2RepairTicks = CR2RepairTotalTicks;
                        SetCR2Status($"🔧  {_cr2FaultEquipName}  정비 중");
                    }
                    else
                    {
                        SetCR2Status($"작업 중  —  {_p2Target?.label ?? "?"}");
                    }
                    break;

                case CR2Phase.WalkToExitShower:
                case CR2Phase.AirShowerExit:
                case CR2Phase.CorridorExit:
                case CR2Phase.CorridorExitWalk:
                    _p2Target     = null;
                    _doorClosing2 = false;
                    SetCR2EqButtonsEnabled(false);
                    btnCR2Exit.IsEnabled = false;
                    tglDist2.IsEnabled   = false;
                    tglDist2.IsChecked   = false;
                    if (_distBeam2 != null) _distBeam2.Points.Clear();
                    SetCR2Status("퇴실 중...");
                    break;
            }
        }

        // ── CR2 헬퍼 ──────────────────────────────────────────────
        // CR2 상태바 텍스트 업데이트
        private void SetCR2Status(string text) => lblCR2Status.Text = $"  —  {text}";

        // CR2 장비 버튼 8개 일괄 활성화/비활성화
        private void SetCR2EqButtonsEnabled(bool enabled)
        {
            cr2eq1.IsEnabled = enabled; cr2eq2.IsEnabled = enabled;
            cr2eq3.IsEnabled = enabled; cr2eq4.IsEnabled = enabled;
            cr2eq5.IsEnabled = enabled; cr2eq6.IsEnabled = enabled;
            cr2eq7.IsEnabled = enabled; cr2eq8.IsEnabled = enabled;
        }

        // ── CR2 관리자 지시 버튼 핸들러 ───────────────────────────
        // CR2 입실 버튼: Hidden 상태일 때 인증 후 Entering 전환
        // ShowDialog 동안 CR1/CR3 렌더링 루프도 계속 돌아 다른 룸 작업자가
        // AuthTriggerZ에 도달해 authRequest 배너가 뜨는 문제를 막기 위해
        // 다이얼로그 표시 전후로 모든 룸을 일시 정지한다.
        private void BtnCR2Enter_Click(object sender, RoutedEventArgs e)
        {
            if (_cr2Phase != CR2Phase.Hidden) return;
            btnCR2Enter.IsEnabled = false;
            tglDist2.IsEnabled    = false;
            tglDist2.IsChecked    = false;
            CR2Transition(CR2Phase.CorridorWalk);
        }

        // 문 앞 인증 배너 "허용" 클릭 → EngineerAuthDialog
        private void ShowAuthPopup2()
        {
            authRequest2.Visibility = Visibility.Collapsed;

            bool p1 = _paused1, p3 = _paused3;
            _paused1 = true; _paused3 = true;

            var dlg = new EngineerAuthDialog("천재엔지니어스") { Owner = this };
            bool ok = dlg.ShowDialog() == true;

            _paused1 = p1; _paused3 = p3;

            if (!ok)
            {
                // 인증 실패 — 배너 다시 표시
                authRequest2.Visibility = Visibility.Visible;
                return;
            }

            User? u = dlg.AuthenticatedUser;
            _cr2WorkerLabel = u != null ? $"{u.Role}:{u.FullName}" : "";

            CR2Transition(CR2Phase.DoorOpening);
        }

        // CR2 퇴실 버튼: Idle/WalkToEquip/AtEquip 상태일 때 Exiting 전환
        private void BtnCR2Exit_Click(object sender, RoutedEventArgs e)
        {
            if (_cr2Phase != CR2Phase.Idle
             && _cr2Phase != CR2Phase.WalkToEquip
             && _cr2Phase != CR2Phase.AtEquip) return;
            CR2Transition(CR2Phase.WalkToExitShower);
        }

        // CR2 장비 이동 지시: 지정 인덱스 장비로 WalkToEquip 전환
        private void CR2SendToEquip(int idx)
        {
            if (_cr2Phase != CR2Phase.Idle
             && _cr2Phase != CR2Phase.AtEquip
             && _cr2Phase != CR2Phase.WalkToEquip) return;
            _p2Target      = AllEquips[idx];
            _cr2TargetIdx  = idx;
            CR2Transition(CR2Phase.WalkToEquip);
        }

        private void BtnCR2Eq1_Click(object s, RoutedEventArgs e) => CR2SendToEquip(0);
        private void BtnCR2Eq2_Click(object s, RoutedEventArgs e) => CR2SendToEquip(1);
        private void BtnCR2Eq3_Click(object s, RoutedEventArgs e) => CR2SendToEquip(2);
        private void BtnCR2Eq4_Click(object s, RoutedEventArgs e) => CR2SendToEquip(3);
        private void BtnCR2Eq5_Click(object s, RoutedEventArgs e) => CR2SendToEquip(4);
        private void BtnCR2Eq6_Click(object s, RoutedEventArgs e) => CR2SendToEquip(5);
        private void BtnCR2Eq7_Click(object s, RoutedEventArgs e) => CR2SendToEquip(6);
        private void BtnCR2Eq8_Click(object s, RoutedEventArgs e) => CR2SendToEquip(7);

        // CR1 웨이퍼 생산 성공 배너 표시
        private void ShowWaferSuccess()
        {
            string visited = string.Join(" → ", _equipWaypoints.Select(w => w.label));
            AddWaferBanner("CR1", visited);
        }

        // 웨이퍼 성공 배너 — 공통 행 추가 (클린룸 태그 + 장비 경로)
        private void AddWaferBanner(string roomTag, string visited)
        {
            // 구분선: 두 번째 항목부터
            if (waferBannerList.Children.Count > 0)
            {
                waferBannerList.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0x22, 0xC5, 0x5E)),
                    Margin = new Thickness(16, 0, 16, 0)
                });
            }

            // 행 그리드
            var row = new Grid { Margin = new Thickness(16, 10, 16, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var emoji = new TextBlock
            {
                Text = "✅",
                FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var label = new TextBlock
            {
                Text = $"[{roomTag}] 웨이퍼 생산 성공  |  {visited}",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            var confirmBtn = new Button
            {
                Content = "확인",
                Width = 64, Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(12, 0, 0, 0)
            };
            confirmBtn.Template = (ControlTemplate)FindResource("WaferConfirmBtnTemplate");

            // 확인 클릭: 이 행(과 앞 구분선)만 제거
            confirmBtn.Click += (s, e) =>
            {
                int idx = waferBannerList.Children.IndexOf(row);
                // 앞에 구분선이 있으면 함께 제거
                if (idx > 0) waferBannerList.Children.RemoveAt(idx - 1);
                waferBannerList.Children.Remove(row);
                if (waferBannerList.Children.Count == 0)
                    waferBanner.Visibility = Visibility.Collapsed;
            };

            Grid.SetColumn(emoji, 0);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(confirmBtn, 2);
            row.Children.Add(emoji);
            row.Children.Add(label);
            row.Children.Add(confirmBtn);

            waferBannerList.Children.Add(row);
            waferBanner.Visibility = Visibility.Visible;
        }

        // ── 장비 고장 트리거 ──────────────────────────────────────────────────────
        private void TriggerEquipmentFault(string room)
        {
            if (room == "R2")
            {
                TriggerCR2EquipmentFault();
                return;
            }
            // R1 / R3: 기존 랜덤 로직
            var all = new List<(LinesVisual3D[] frames, Color orig)>(_equipReg1.Count + _equipReg3.Count);
            all.AddRange(_equipReg1);
            all.AddRange(_equipReg3);
            if (all.Count == 0) return;

            var (frames, orig) = all[_faultRng.Next(all.Count)];
            foreach (var f in frames) f.Color = FaultColor;
            _faults.Add((frames, orig, DateTime.Now.AddSeconds(FaultRecoverySec)));
        }

        // ── CR2 전용: 랜덤 장비 고장 + 5초 자동 수리 ──────────────────────────
        private void TriggerCR2EquipmentFault()
        {
            if (_equipReg2.Count == 0) return;
            if (_cr2FaultFrames != null) return;  // 이미 고장 중이면 중복 방지

            int idx        = _faultRng.Next(_equipReg2.Count);
            var (frames, orig) = _equipReg2[idx];
            foreach (var f in frames) f.Color = FaultColor;
            _faults.Add((frames, orig, DateTime.MaxValue)); // 수동 복구 전까지 유지

            _cr2FaultFrames    = frames;
            _cr2FaultOrigColor = orig;
            _cr2FaultIdx       = idx;
            // _cr2RepairTicks는 시작하지 않음 → 엔지니어가 AtEquip에 도착해야 시작

            string equipName = idx < _cr2EquipNames.Length
                ? _cr2EquipNames[idx]
                : $"CR2-장비{idx + 1:D2}";
            _cr2FaultEquipName = equipName;

            var record = new AlarmRecord
            {
                Timestamp         = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Room              = "R2",
                Sensor            = "장비 고장",
                Value             = 0,
                Unit              = "",
                Threshold         = 0,
                Status            = "위험",
                EquipmentName     = equipName,
                MaintenanceStatus = "정비 필요"
            };
            _cr2FaultRecord = record;
            EquipmentFaultOccurred?.Invoke(record);
            SetCR2Status($"⚠️  {equipName}  고장 발생!  — 엔지니어를 파견하세요");
        }

        // ── CR2 수리 카운트다운 (렌더루프에서 틱마다 호출) ──────────────────────
        private void TickCR2Repair()
        {
            // 정비 완료 배너 자동 숨김
            if (_cr2RepairDoneTicks > 0 && --_cr2RepairDoneTicks == 0)
                SetCR2Status("대기 중");

            if (_cr2RepairTicks <= 0) return;

            _cr2RepairTicks--;
            if (_cr2RepairTicks == 0)
                CompleteCR2Repair();
        }

        // ── CR2 수리 완료 처리 ────────────────────────────────────────────────────
        private void CompleteCR2Repair()
        {
            // "정비 중"에서 보여준 이름 그대로 "정비 완료"에도 사용
            string savedEquipName = _cr2FaultEquipName;

            if (_cr2FaultFrames != null)
            {
                // _faults 에서 제거 + 원래 색 복원
                for (int i = _faults.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_faults[i].frames, _cr2FaultFrames))
                    {
                        foreach (var fr in _cr2FaultFrames) fr.Color = _cr2FaultOrigColor;
                        _faults.RemoveAt(i);
                        break;
                    }
                }
                _cr2FaultFrames = null;
            }

            if (_cr2FaultRecord != null)
            {
                _cr2FaultRecord.MaintenanceStatus = "정상";
                _cr2FaultRecord.Status            = "정상";
                _cr2FaultRecord = null;
            }
            SetCR2Status($"✅  {savedEquipName}  정비 완료!");

            _cr2RepairTicks     = -1;   // idle 상태로 복귀 → 다음 고장 트리거 가능
            _cr2FaultEquipName  = "";
            _cr2FaultIdx        = -1;
            _cr2TargetIdx       = -1;
            _cr2DangerCount     = 0;    // 위험 카운터 초기화
            _cr2RepairDoneTicks = 80;   // 4초 후 "대기 중"으로 복귀
        }

        // ── CR3 현재 수리 대상 복원 ──────────────────────────────────────────────
        private void RepairCurrentTarget3(string label)
        {
            if (label.Contains("FFU") || label.Contains("ffu"))
            {
                RestoreFfuColor3();
                _ffuFailed3 = false;
            }
            else if (label.Contains("에어샤워"))
            {
                RestoreAirShowerColor3();
                _airShowerFailed3 = false;
            }
            else
            {
                // 장비 인덱스 복원
                foreach (int bidx in _brokenEquipIndices3)
                {
                    if (bidx < _equipReg3.Count)
                    {
                        var (frames, orig) = _equipReg3[bidx];
                        foreach (var f in frames) f.Color = orig;
                    }
                }
                _brokenEquipIndices3.Clear();
            }
        }

        // ── FFU 색상 복원 ─────────────────────────────────────────────────────
        private void RestoreFfuColor3()
        {
            if (_ffuFailedIndex3 < _ffuOrigColors3.Count)
            {
                var (line, orig) = _ffuOrigColors3[_ffuFailedIndex3];
                line.Color = orig;
            }
        }

        // ── 에어샤워 노즐 색상 복원 ───────────────────────────────────────────
        private void RestoreAirShowerColor3()
        {
            if (_airNozzle3 != null) _airNozzle3.Color = _nozzleIdleColor3;
        }

        // ── 고장 깜빡임 업데이트 (50ms 틱) ──────────────────────────────────
        private void UpdateFailBlink3()
        {
            _failBlinkTick++;
            if (_failBlinkTick >= FailBlinkToggleTicks)
            {
                _failBlinkTick = 0;
                _failBlinkOn   = !_failBlinkOn;
            }

            Color blinkColor = _failBlinkOn ? EquipFailColor : Colors.Transparent;

            foreach (int bidx in _brokenEquipIndices3)
            {
                if (bidx < _equipReg3.Count)
                {
                    var (frames, _) = _equipReg3[bidx];
                    foreach (var f in frames) f.Color = blinkColor;
                }
            }

            if (_ffuFailed3 && _ffuFailedIndex3 < _ffuOrigColors3.Count)
                _ffuOrigColors3[_ffuFailedIndex3].line.Color = blinkColor;

            if (_airShowerFailed3 && _airNozzle3 != null)
                _airNozzle3.Color = _failBlinkOn ? WarnColor : _nozzleIdleColor3;
        }

        // ── 수리 완료 배너 자동 숨김 카운트다운 ─────────────────────────────
        private void TickRepairBanner3()
        {
            if (_repairBannerTicks3 <= 0) return;
            _repairBannerTicks3--;
            if (_repairBannerTicks3 == 0)
                repairingBanner3.Visibility = Visibility.Collapsed;
        }

        // ── CR1 정비 완료 배너 자동 숨김 ─────────────────────────────────────
        private void TickCR1WorkerRepair()
        {
            if (_cr3WorkerDoneTicks > 0 && --_cr3WorkerDoneTicks == 0)
                maintenanceBanner3.Visibility = Visibility.Collapsed;
        }

        // ── CR1 작업자 수리 완료 ──────────────────────────────────────────────
        private void CompleteCR1WorkerRepair()
        {
            // _faults에서 해당 장비 제거 + 원래 색 복원
            for (int i = _faults.Count - 1; i >= 0; i--)
            {
                var (frames, orig, restoreAt) = _faults[i];
                if (restoreAt == DateTime.MaxValue)
                {
                    foreach (var f in frames) f.Color = orig;
                    _faults.RemoveAt(i);
                    break;
                }
            }

            string label = _cr3FaultLabel;
            _cr3FaultActive       = false;
            _cr3FaultLabel        = "";
            _cr3WorkerRepairTicks = -1;

            // "정비 완료!" 배너 3초 표시
            maintenanceBannerText3.Text = $"✅  {label}  정비 완료!";
            maintenanceBanner3.Visibility = Visibility.Visible;
            _cr3WorkerDoneTicks = 60;  // 60 × 50ms = 3초
        }

        // ── 고장 자동 복구 ────────────────────────────────────────────────────────
        private void RecoverFaults()
        {
            for (int i = _faults.Count - 1; i >= 0; i--)
            {
                var (frames, orig, restoreAt) = _faults[i];
                if (DateTime.Now >= restoreAt)
                {
                    foreach (var f in frames) f.Color = orig;
                    _faults.RemoveAt(i);
                }
            }
        }

        // ── 엔지니어 투입 트리거 ──────────────────────────────────────────────────
        // Pressure: 에어샤워 고장(황색) + 랜덤 장비 1개 + 랜덤 FFU 1개 고장
        private void TriggerEngineerDispatch(EngineerTriggerReason reason)
        {
            _engineerTriggerReason  = reason;
            _engineerTriggerPending = true;

            // 랜덤 장비 고장 (CR1 전용, 실제 값과 무관)
            // 자동복구 없음 — 작업자가 해당 장비 접근 후 5초 뒤 해결
            if (_equipReg3.Count > 0 && !_cr3FaultActive)
            {
                int fIdx = _faultRng.Next(_equipReg3.Count);
                var (frames, orig) = _equipReg3[fIdx];
                foreach (var f in frames) f.Color = FaultColor;
                _faults.Add((frames, orig, DateTime.MaxValue));
                _cr3FaultActive       = true;
                _cr3FaultLabel        = fIdx < AllEquips.Length ? AllEquips[fIdx].label : $"장비{fIdx + 1}";
                _cr3WorkerRepairTicks = -1;

                // 엔지니어는 고장 장비 위치로 직행 (센서 점검 없음)
                _engineerTarget = fIdx < AllEquips.Length
                    ? AllEquips[fIdx]
                    : (0.0, RZ * 0.5, _cr3FaultLabel);
            }
            else
            {
                // 고장 없으면 기존 센서 타겟 유지 (폴백)
                _engineerTarget = reason switch
                {
                    EngineerTriggerReason.Vibration    => (0.0, RZ * 0.52, "진동센서"),
                    EngineerTriggerReason.TempHumidity => ThSensorPos,
                    EngineerTriggerReason.Pressure     => PressSensorPos,
                    _                                  => ThSensorPos,
                };
            }
            _engineerRepairQueue.Clear();  // 추가 방문 큐 초기화
        }
    }
}
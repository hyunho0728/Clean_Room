using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace Clean_Room
{
    public partial class AlarmHistoryWindow : Window
    {
        // 라이브 알람 컬렉션 (SensorDashboard에서 전달)
        private readonly ObservableCollection<AlarmRecord>? _liveAlarms;
        // DB 조회 결과를 담는 컬렉션
        private ObservableCollection<AlarmRecord>? _dbAlarms;

        // 라이브 모드로 열기 (SensorDashboard → BtnHistory_Click)
        public AlarmHistoryWindow(ObservableCollection<AlarmRecord> liveAlarms)
        {
            InitializeComponent();
            _liveAlarms = liveAlarms;
            dpFrom.SelectedDate = DateTime.Today;
            dpTo.SelectedDate   = DateTime.Today;

            // 라이브 컬렉션을 바로 바인딩 (INotifyPropertyChanged로 실시간 반영)
            BindLive();
        }

        // 기본 생성자 (라이브 컬렉션 없이 DB 조회만)
        public AlarmHistoryWindow()
        {
            InitializeComponent();
            dpFrom.SelectedDate = DateTime.Today;
            dpTo.SelectedDate   = DateTime.Today;
            LoadAll();
        }

        // ── 라이브 바인딩 ──────────────────────────────────────────
        private void BindLive()
        {
            if (_liveAlarms == null) return;
            alarmList.ItemsSource  = _liveAlarms;
            txtResultCount.Text    = $"{_liveAlarms.Count}건 (라이브)";

            // 컬렉션 변경 시 카운트 갱신
            _liveAlarms.CollectionChanged += (_, _) =>
                txtResultCount.Text = $"{_liveAlarms.Count}건 (라이브)";
        }

        // ── 라이브 버튼 ───────────────────────────────────────────
        private void BtnLive_Click(object sender, RoutedEventArgs e)
        {
            if (_liveAlarms != null)
                BindLive();
        }

        // ── DB 전체 조회 ──────────────────────────────────────────
        private void LoadAll()
        {
            var list = DatabaseHelper.GetAlarms();
            _dbAlarms = new ObservableCollection<AlarmRecord>(list);
            alarmList.ItemsSource = _dbAlarms;
            txtResultCount.Text   = $"{_dbAlarms.Count}건";
        }

        // ── DB 기간 조회 ──────────────────────────────────────────
        private void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            if (dpFrom.SelectedDate == null || dpTo.SelectedDate == null)
            {
                LoadAll();
                return;
            }

            var from = dpFrom.SelectedDate.Value;
            var to   = dpTo.SelectedDate.Value.AddDays(1).AddSeconds(-1);
            var list = DatabaseHelper.GetAlarms(from, to);
            _dbAlarms = new ObservableCollection<AlarmRecord>(list);
            alarmList.ItemsSource = _dbAlarms;
            txtResultCount.Text   = $"{_dbAlarms.Count}건";
        }

        // ── CSV 내보내기 ──────────────────────────────────────────
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            // 현재 표시 중인 소스 (라이브 or DB)
            var source = alarmList.ItemsSource as System.Collections.IEnumerable;
            var list   = source?.Cast<AlarmRecord>().ToList();

            if (list == null || list.Count == 0)
            {
                MessageBox.Show("내보낼 데이터가 없습니다.", "알림",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName   = $"AlarmLog_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".csv",
                Filter     = "CSV 파일|*.csv"
            };

            if (dlg.ShowDialog() != true) return;

            var sb = new StringBuilder();
            sb.AppendLine("시간,구역,센서,측정값,단위,상태,고장장비,정비상태");
            foreach (var r in list)
                sb.AppendLine(
                    $"{r.Timestamp},{r.Room},{r.Sensor},{r.Value:F3},{r.Unit}," +
                    $"{r.Status},{r.EquipmentName},{r.MaintenanceStatus}");

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"저장 완료: {dlg.FileName}", "내보내기",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}

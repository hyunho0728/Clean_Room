using System.ComponentModel;

namespace Clean_Room
{
    public class AlarmRecord : INotifyPropertyChanged
    {
        public string Timestamp { get; set; } = "";
        public string Room      { get; set; } = "R1";
        public string Sensor    { get; set; } = "";
        public double Value     { get; set; }
        public string Unit      { get; set; } = "";
        public double Threshold { get; set; }

        // ── 상태: "위험" or "정상" ─────────────────────────────────
        private string _status = "위험";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropChanged(nameof(Status)); }
        }

        // ── 고장 장비명 (장비 고장 기록 시에만 사용) ─────────────────
        public string EquipmentName { get; set; } = "";
        public bool   IsEquipmentFault => !string.IsNullOrEmpty(EquipmentName);

        // ── 정비 상태: "" → "정비 중" → "정비 완료" ──────────────────
        private string _maintenanceStatus = "";
        public string MaintenanceStatus
        {
            get => _maintenanceStatus;
            set { _maintenanceStatus = value; OnPropChanged(nameof(MaintenanceStatus)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string Display =>
            $"[{Timestamp}]  {Sensor}  {(Value > 0 ? Value.ToString("F2") + " " + Unit : "")}  ({Status})";
    }
}

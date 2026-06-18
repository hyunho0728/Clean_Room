using System.Windows;
using System.Windows.Input;

namespace Clean_Room
{
    /// <summary>
    /// 작업자/장비 엔지니어 2단계 출입 인증 다이얼로그.
    /// 1차: ID + 비밀번호 (DB 검증, requiredRole 일치 확인)
    /// 2차: 역할별 텍스트 입력 (secondFactor)
    /// </summary>
    public partial class WorkerAuthDialog : Window
    {
        private readonly string _requiredRole;
        private readonly string _secondFactor;

        /// <summary>1차 인증 성공 시 설정되는 사용자 (이름 표시 등에 활용)</summary>
        public User? AuthenticatedUser { get; private set; }

        /// <param name="requiredRole">허용할 직무 (예: "작업자", "장비 엔지니어")</param>
        /// <param name="secondFactor">2차 인증 텍스트 (예: "A+클린룸", "천재엔지니어스")</param>
        public WorkerAuthDialog(string requiredRole, string secondFactor)
        {
            InitializeComponent();
            _requiredRole = requiredRole;
            _secondFactor = secondFactor;

            // UI 초기 설정
            txtTitle.Text    = $"🔐  {requiredRole} 출입 인증";
            txtStep.Text     = "1단계 · 아이디 / 비밀번호 확인";
            txt2ndLabel.Text = $"클린룸 출입 2차 인증 코드를 입력하세요\n({requiredRole} 전용)";

            Loaded += (_, _) => txtId.Focus();
        }

        // ── 1단계 ──────────────────────────────────────────────────

        private void TxtId_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return) txtPw.Focus();
        }

        private void TxtPw_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return) TryStep1();
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e) => TryStep1();

        private void TryStep1()
        {
            step1Error.Visibility = Visibility.Collapsed;

            string id = txtId.Text.Trim();
            string pw = txtPw.Password;

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw))
            {
                step1Error.Visibility = Visibility.Visible;
                return;
            }

            // DB 인증: ID + PW 확인 후 직무 일치 검사
            User? user = DatabaseHelper.AuthenticateUser(id, pw);
            if (user == null || user.Role != _requiredRole)
            {
                step1Error.Visibility = Visibility.Visible;
                txtPw.Clear();
                txtPw.Focus();
                return;
            }

            // 1차 성공 → 사용자 저장 후 2단계로 전환
            AuthenticatedUser     = user;
            step1Panel.Visibility = Visibility.Collapsed;
            step2Panel.Visibility = Visibility.Visible;
            txtStep.Text          = "2단계 · 2차 인증 코드 확인";
            txt2nd.Focus();
        }

        // ── 2단계 ──────────────────────────────────────────────────

        private void Txt2nd_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return) TryStep2();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e) => TryStep2();

        private void TryStep2()
        {
            if (txt2nd.Text == _secondFactor)
            {
                DialogResult = true;
            }
            else
            {
                step2Error.Visibility = Visibility.Visible;
                txt2nd.Clear();
                txt2nd.Focus();
            }
        }

        // ── 취소 ───────────────────────────────────────────────────

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}

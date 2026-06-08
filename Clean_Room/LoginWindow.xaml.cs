using System.Windows;

namespace Clean_Room
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            // 앱 실행 시 데이터베이스 초기화 유도
            DatabaseHelper.InitializeDatabase();
        }

        // 로그인 버튼 클릭 이벤트
        private void btnLogin_Click(object sender, RoutedEventArgs e)
        {
            string userID = txtUserId.Text.Trim();
            string password = txtPassword.Password;

            if (DatabaseHelper.AuthenticateUser(userID, password))
            {
                MessageBox.Show("로그인 성공!");

                // 새로운 창 이름으로 인스턴스 생성
                IOMonitorWindow monitorWin = new IOMonitorWindow();
                monitorWin.Show(); // 관제 화면 열기

                this.Close(); // 로그인 창 닫기
            }
            else
            {
                MessageBox.Show("아이디 또는 비밀번호가 틀렸습니다.");
            }
        }

        // 회원가입 화면으로 이동
        private void btnGoToRegister_Click(object sender, RoutedEventArgs e)
        {
            RegisterWindow registerWindow = new RegisterWindow();
            registerWindow.Show();
            this.Close();
        }

        private void txtUserId_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {

        }
    }
}
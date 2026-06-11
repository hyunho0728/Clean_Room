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
            string userID   = txtUserId.Text.Trim();
            string password = txtPassword.Password;

            User user = DatabaseHelper.AuthenticateUser(userID, password);

            if (user != null)
            {
                Window nextWindow;

                switch (user.Role)
                {
                    case "관리자":
                        nextWindow = new AdminWindow(user);
                        break;
                    case "센서 엔지니어":
                    case "장비 엔지니어":
                        nextWindow = new IOMonitorWindow();
                        break;
                    default:
                        nextWindow = new IOMonitorWindow();
                        break;
                }

                nextWindow.Show();
                this.Close();
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
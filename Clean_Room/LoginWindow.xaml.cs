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
            string username = txtUsername.Text.Trim();
            string password = txtPassword.Password;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("아이디와 비밀번호를 모두 입력해주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 모듈화된 DB Helper 호출
            if (DatabaseHelper.AuthenticateUser(username, password))
            {
                MessageBox.Show("인증에 성공했습니다.", "성공", MessageBoxButton.OK, MessageBoxImage.Information);

                // 메인 화면 실행 (MainWindow가 프로젝트에 정의되어 있어야 합니다)
                // MainWindow mainWin = new MainWindow();
                // mainWin.Show();

                this.Close();
            }
            else
            {
                MessageBox.Show("아이디 또는 비밀번호가 올바르지 않습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 회원가입 화면으로 이동
        private void btnGoToRegister_Click(object sender, RoutedEventArgs e)
        {
            RegisterWindow registerWindow = new RegisterWindow();
            registerWindow.Show();
            this.Close();
        }
    }
}
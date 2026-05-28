using System.Windows;

namespace Clean_Room
{
    public partial class RegisterWindow : Window
    {
        public RegisterWindow()
        {
            InitializeComponent();
        }

        // 회원가입 버튼 클릭
        private void btnRegister_Click(object sender, RoutedEventArgs e)
        {
            string username = txtRegUsername.Text.Trim();
            string fullName = txtRegFullName.Text.Trim();
            string password = txtRegPassword.Password;

            // 유효성 검사
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("모든 필드를 입력해 주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 모델 객체 생성
            User newUser = new User
            {
                Username = username,
                FullName = fullName,
                Password = password
            };

            // DB 모듈에 데이터 생성 요청
            bool isSuccess = DatabaseHelper.RegisterUser(newUser);

            if (isSuccess)
            {
                MessageBox.Show("회원가입이 완료되었습니다. 로그인 해주세요.", "성공", MessageBoxButton.OK, MessageBoxImage.Information);

                // 로그인 창으로 복귀
                LoginWindow loginWindow = new LoginWindow();
                loginWindow.Show();
                this.Close();
            }
            else
            {
                MessageBox.Show("이미 존재하는 사용자 ID입니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 로그인 창으로 복귀
        private void btnBack_Click(object sender, RoutedEventArgs e)
        {
            LoginWindow loginWindow = new LoginWindow();
            loginWindow.Show();
            this.Close();
        }
    }
}
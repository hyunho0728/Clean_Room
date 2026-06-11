using System.Linq;
using System.Windows;
using System.Windows.Controls;

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
            string userID          = txtRegUserID.Text.Trim();
            string password        = txtRegPassword.Password;
            string passwordConfirm = txtRegPasswordConfirm.Password;
            string fullName        = txtRegFullName.Text.Trim();
            string phone    = txtRegPhone.Text.Trim();
            string role   = pnlRegRole.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Content?.ToString();
            string gender = pnlRegGender.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Content?.ToString();
            string email    = txtRegEmail.Text.Trim();

            // 유효성 검사
            if (string.IsNullOrEmpty(userID) ||
                string.IsNullOrEmpty(password) ||
                string.IsNullOrEmpty(passwordConfirm) ||
                string.IsNullOrEmpty(fullName) ||
                string.IsNullOrEmpty(phone) ||
                string.IsNullOrEmpty(role) ||
                string.IsNullOrEmpty(gender) ||
                string.IsNullOrEmpty(email))
            {
                MessageBox.Show("모든 필드를 입력해 주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (password != passwordConfirm)
            {
                MessageBox.Show("비밀번호가 일치하지 않습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 모델 객체 생성
            User newUser = new User
            {
                UserID = userID,
                Password = password,
                FullName = fullName,
                Phone    = phone,
                Role     = role,
                Gender   = gender,
                Email    = email
            };

            // DB에 저장
            bool isSuccess = DatabaseHelper.RegisterUser(newUser);

            if (isSuccess)
            {
                MessageBox.Show("회원가입이 완료되었습니다. 로그인 해주세요.", "성공", MessageBoxButton.OK, MessageBoxImage.Information);

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

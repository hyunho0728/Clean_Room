using System.Windows;
using System.Windows.Input;

namespace Clean_Room
{
    public partial class AuthDialog : Window
    {
        private const string Password = "A+클린룸";

        public AuthDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => authInput.Focus();
        }

        private void AuthInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return) TryAuth();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e) => TryAuth();

        private void TryAuth()
        {
            if (authInput.Text == Password)
            {
                DialogResult = true;
            }
            else
            {
                authError.Visibility = Visibility.Visible;
                authInput.Clear();
                authInput.Focus();
            }
        }
    }
}

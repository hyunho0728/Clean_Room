using System.Windows;

namespace Clean_Room
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 프로그램 실행 시 데이터베이스를 먼저 초기화 (선택 사항)
            // LoginWindow 생성자 내부에 두어도 되지만 여기서 일괄 처리하면 관리하기 좋습니다.
            DatabaseHelper.InitializeDatabase();

            // 2. 로그인 창 객체 생성 및 출력
            LoginWindow loginWindow = new LoginWindow();
            loginWindow.Show();
        }
    }
}
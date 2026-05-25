using System.Windows;
using System.Windows.Input;

namespace MasterAdmin
{
    public partial class LoginWindow : Window
    {
        // 계정 정보 — 나중에 Flask API 인증으로 교체 가능
        private static readonly (string id, string pw, string name)[] _accounts =
        {
            ("admin",   "admin1234", "관리자"),
            ("manager", "mgr1234",   "매니저"),
        };

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => TxtId.Focus();
        }

        // 드래그로 창 이동
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        // X 버튼
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        // Enter 키 로그인
        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                TryLogin();
        }

        // 로그인 버튼
        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            TryLogin();
        }

        private void TryLogin()
        {
            string id = TxtId.Text.Trim();
            string pw = TxtPw.Password;

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw))
            {
                ShowError("아이디와 비밀번호를 입력해주세요.");
                return;
            }

            foreach (var (accId, accPw, accName) in _accounts)
            {
                if (id == accId && pw == accPw)
                {
                    // 로그인 성공 → 메인 창 열기
                    var main = new MainWindow();
                    main.Show();
                    Close();
                    return;
                }
            }

            ShowError("아이디 또는 비밀번호가 올바르지 않습니다.");
            TxtPw.Clear();
            TxtPw.Focus();
        }

        private void ShowError(string msg)
        {
            TxtError.Text = msg;
            TxtError.Visibility = Visibility.Visible;
        }
    }
}

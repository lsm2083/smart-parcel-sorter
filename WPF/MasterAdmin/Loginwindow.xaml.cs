using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;

namespace MasterAdmin
{
    public partial class LoginWindow : Window
    {
        // ── Flask 서버 주소 (MainViewModel과 동일하게 맞추기) ──────────────
        private const string SERVER = "http://192.168.0.21:5000";

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

        private async void TryLogin()
        {
            string id = TxtId.Text.Trim();
            string pw = TxtPw.Password;

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw))
            {
                ShowError("아이디와 비밀번호를 입력해주세요.");
                return;
            }

            BtnLogin.IsEnabled = false;
            ShowError("");

            try
            {
                using var http = new HttpClient();
                http.BaseAddress = new System.Uri(SERVER.TrimEnd('/') + "/");
                http.Timeout = System.TimeSpan.FromSeconds(5);

                // Flask POST /api/auth/login 호출
                var body = new StringContent(
                    JsonConvert.SerializeObject(new { user_id = id, password = pw }),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await http.PostAsync("api/auth/login", body);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var result = JsonConvert.DeserializeObject<LoginResponse>(json);

                    if (result?.Success == true)
                    {
                        // 로그인 성공 → 메인 창 열기
                        var main = new MainWindow();
                        main.Show();
                        Close();
                        return;
                    }

                    ShowError(result?.Message ?? "아이디 또는 비밀번호가 올바르지 않습니다.");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    ShowError("아이디 또는 비밀번호가 올바르지 않습니다.");
                }
                else
                {
                    ShowError($"서버 오류 ({(int)response.StatusCode})");
                }
            }
            catch (HttpRequestException)
            {
                ShowError("서버에 연결할 수 없습니다. 네트워크를 확인하세요.");
            }
            catch (System.Exception ex)
            {
                ShowError($"오류: {ex.Message}");
            }
            finally
            {
                BtnLogin.IsEnabled = true;
                TxtPw.Clear();
                TxtPw.Focus();
            }
        }

        private void ShowError(string msg)
        {
            TxtError.Text = msg;
            TxtError.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Flask 응답 모델
        private class LoginResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("message")]
            public string? Message { get; set; }

            [JsonProperty("name")]
            public string? Name { get; set; }

            [JsonProperty("role")]
            public string? Role { get; set; }
        }
    }
}
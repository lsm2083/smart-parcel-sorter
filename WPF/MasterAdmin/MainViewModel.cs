using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;          // ← 추가
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows;

namespace MasterAdmin
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;
        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
        public void Execute(object? p) => _execute(p);
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly DispatcherTimer _timer;
        // ── [변경] SimulateLiveData 관련 필드 제거, ApiService 추가
        private readonly ApiService _api;
        private int _tick = 0;  // 시계 틱용으로만 사용

        // ── Navigation
        private string _currentPage = "현장";
        public string CurrentPage
        {
            get => _currentPage;
            set { _currentPage = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsFieldPage)); OnPropertyChanged(nameof(IsOverviewPage)); }
        }
        public bool IsFieldPage   => _currentPage == "현장";
        public bool IsOverviewPage => _currentPage == "총괄";

        // ── Clock
        private string _currentTime = "";
        public string CurrentTime
        {
            get => _currentTime;
            set { _currentTime = value; OnPropertyChanged(); }
        }

        // ── Emergency Stop
        private bool _isEmergencyStop = false;
        public bool IsEmergencyStop
        {
            get => _isEmergencyStop;
            set { _isEmergencyStop = value; OnPropertyChanged(); DeviceStatus.EmergencyStop = value; OnPropertyChanged(nameof(DeviceStatus)); }
        }

        public DeviceStatus DeviceStatus { get; set; } = new();

        // ApiService에서 DeviceStatus 변경 후 UI에 알릴 때 사용
        public void RefreshDeviceStatus() => OnPropertyChanged(nameof(DeviceStatus));
        // 추가
        public Action<string>? TriggerRecording { get; set; }
        public ObservableCollection<SortingLog>   SortingLogs   { get; } = new();
        public ObservableCollection<ShippingLog>  ShippingLogs  { get; } = new();
        public ObservableCollection<BlackboxEvent> BlackboxEvents { get; } = new();
        public ObservableCollection<LoginRecord>  LoginRecords  { get; } = new();

        public ICommand NavigateToFieldCommand  { get; }
        public ICommand NavigateToOverviewCommand { get; }
        public ICommand ToggleEmergencyCommand  { get; }
        public ICommand ToggleThemeCommand      { get; }
        public ICommand ClearLogsCommand          { get; }
        public ICommand ConveyorStartCommand      { get; }
        public ICommand ConveyorStopCommand       { get; }
        public ICommand ConveyorResumeCommand     { get; }

        // ── Theme
        private bool _isDark = true;
        public bool IsDark
        {
            get => _isDark;
            set { _isDark = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThemeIcon)); OnPropertyChanged(nameof(ThemeLabel)); }
        }
        public string ThemeIcon  => IsDark ? "☀" : "🌙";
        public string ThemeLabel => IsDark ? "라이트 모드" : "다크 모드";

        public MainViewModel()
        {
            NavigateToFieldCommand    = new RelayCommand(_ => CurrentPage = "현장");
            NavigateToOverviewCommand = new RelayCommand(_ => CurrentPage = "총괄");

            // ── [변경] 비상정지: 로컬 토글 → Flask API 호출
            ToggleEmergencyCommand = new RelayCommand(async _ => await ToggleEmergencyAsync());

            ToggleThemeCommand = new RelayCommand(_ =>
            {
                IsDark = !IsDark;
                App.ToggleTheme();
            });
            ClearLogsCommand = new RelayCommand(p =>
            {
                switch (p?.ToString())
                {
                    case "sorting":  SortingLogs.Clear();   break;
                    case "shipping": ShippingLogs.Clear();  break;
                    case "blackbox": BlackboxEvents.Clear(); break;
                    case "login":    LoginRecords.Clear();  break;
                }
            });

            ConveyorStartCommand  = new RelayCommand(_ => _ = ConveyorStartAsync());
            ConveyorStopCommand   = new RelayCommand(_ => _ = ConveyorStopAsync());
            ConveyorResumeCommand = new RelayCommand(_ => _ = ConveyorResumeAsync());

            // ── [변경] ApiService 초기화 (서버 IP 수정 필요)
            _api = new ApiService("http://192.168.0.24:5000");

            // ── [변경] LoadSampleData() → Flask REST 초기 로딩
            _ = InitAsync();

            // ── 시계 타이머 (기존 유지, SimulateLiveData 호출만 제거)
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _tick++;
                // SimulateLiveData() 제거 — WebSocket 이벤트로 대체됨
            };
            _timer.Start();
        }

        // ── [추가] 초기 데이터 로딩 + WebSocket 구독
        private async Task InitAsync()
        {
            await _api.LoadInitialDataAsync(this);  // REST API로 초기 데이터
            _api.StartRealtimeEvents(this);          // WebSocket 이벤트 구독 시작
        }

        // ── [추가] 비상정지 토글 → API 호출
        private async Task ToggleEmergencyAsync()
        {
            if (IsEmergencyStop)
            {
                IsEmergencyStop = false;
                await _api.EmergencyResetAsync();
            }
            else
            {
                IsEmergencyStop = true;
                await _api.EmergencyStopAsync();
            }
        }

        private async Task ConveyorStartAsync()
        {
            DeviceStatus.ConveyorStatus = "시작중...";
            RefreshDeviceStatus();
            bool ok = await _api.ConveyorStartAsync();
            if (!ok) { DeviceStatus.ConveyorStatus = "오류"; RefreshDeviceStatus(); }
        }

        private async Task ConveyorStopAsync()
        {
            DeviceStatus.ConveyorStatus = "정지중...";
            RefreshDeviceStatus();
            bool ok = await _api.ConveyorStopAsync();
            if (!ok) { DeviceStatus.ConveyorStatus = "오류"; RefreshDeviceStatus(); }
        }

        private async Task ConveyorResumeAsync()
        {
            DeviceStatus.ConveyorStatus = "재개중...";
            RefreshDeviceStatus();
            bool ok = await _api.ConveyorResumeAsync();
            if (!ok) { DeviceStatus.ConveyorStatus = "오류"; RefreshDeviceStatus(); }
        }

        public async Task CleanupAsync()
        {
            _timer.Stop();
            await _api.DisconnectAsync();
        }

        // ─────────────────────────────────────────────────────────────────
        // 블랙박스 이벤트 생성 헬퍼 (기존 유지 — 필요 시 삭제 가능)
        // ─────────────────────────────────────────────────────────────────
        private const string BlackboxRoot = @"blackbox\";
        private int _localId = 1;

        private BlackboxEvent CreateBlackboxEvent(
            string eventType, string description, string severity, string trackingNumber = "")
        {
            string subFolder = eventType switch
            {
                BlackboxEventType.OcrFail  => @"ocr_fail\",
                BlackboxEventType.SortFail => @"sort_fail\",
                BlackboxEventType.Jam      => @"jam\",
                _                          => @"etc\"
            };
            string saveFolder = BlackboxRoot + subFolder;
            string fileName   = $"{eventType}_{DateTime.Now:yyyyMMdd_HHmmss}_{_localId}.jpg";
            return new BlackboxEvent
            {
                Id             = _localId++,
                Timestamp      = DateTime.Now,
                EventType      = eventType,
                Description    = description,
                ImagePath      = saveFolder + fileName,
                SaveFolder     = saveFolder,
                Severity       = severity,
                TrackingNumber = trackingNumber
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // 제거된 메서드 (주석으로 보존 — 필요 없으면 삭제)
        // ─────────────────────────────────────────────────────────────────
        // private void LoadSampleData() { ... }      → InitAsync()로 대체
        // private void SimulateLiveData() { ... }    → WebSocket 이벤트로 대체

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

}

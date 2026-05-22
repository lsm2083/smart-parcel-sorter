using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace MasterAdmin
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;

        public void Execute(object? p) => _execute(p);
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly DispatcherTimer _timer;
        private readonly ApiService _api;
        private int _tick = 0;
        private bool _statusPolling = false;

        private string _currentPage = "현장";
        public string CurrentPage
        {
            get => _currentPage;
            set
            {
                _currentPage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsFieldPage));
                OnPropertyChanged(nameof(IsOverviewPage));
            }
        }

        public bool IsFieldPage => _currentPage == "현장";
        public bool IsOverviewPage => _currentPage == "총괄";

        private string _currentTime = "";
        public string CurrentTime
        {
            get => _currentTime;
            set
            {
                _currentTime = value;
                OnPropertyChanged();
            }
        }

        private bool _isEmergencyStop = false;
        public bool IsEmergencyStop
        {
            get => _isEmergencyStop;
            set
            {
                _isEmergencyStop = value;
                OnPropertyChanged();
                DeviceStatus.EmergencyStop = value;
                OnPropertyChanged(nameof(DeviceStatus));
            }
        }

        public DeviceStatus DeviceStatus { get; set; } = new();

        public void RefreshDeviceStatus()
        {
            OnPropertyChanged(nameof(DeviceStatus));
        }

        public Action<string>? TriggerRecording { get; set; }

        public ObservableCollection<SortingLog> SortingLogs { get; } = new();
        public ObservableCollection<ShippingLog> ShippingLogs { get; } = new();
        public ObservableCollection<BlackboxEvent> BlackboxEvents { get; } = new();
        public ObservableCollection<LoginRecord> LoginRecords { get; } = new();

        public ICommand NavigateToFieldCommand { get; }
        public ICommand NavigateToOverviewCommand { get; }
        public ICommand ToggleEmergencyCommand { get; }
        public ICommand ToggleThemeCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand ConveyorStartCommand { get; }
        public ICommand ConveyorStopCommand { get; }
        public ICommand ConveyorResumeCommand { get; }

        private bool _isDark = true;
        public bool IsDark
        {
            get => _isDark;
            set
            {
                _isDark = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThemeIcon));
                OnPropertyChanged(nameof(ThemeLabel));
            }
        }

        public string ThemeIcon => IsDark ? "☀" : "🌙";
        public string ThemeLabel => IsDark ? "라이트 모드" : "다크 모드";

        public MainViewModel()
        {
            NavigateToFieldCommand = new RelayCommand(_ => CurrentPage = "현장");
            NavigateToOverviewCommand = new RelayCommand(_ => CurrentPage = "총괄");

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
                    case "sorting":
                        SortingLogs.Clear();
                        break;

                    case "shipping":
                        ShippingLogs.Clear();
                        break;

                    case "blackbox":
                        BlackboxEvents.Clear();
                        break;

                    case "login":
                        LoginRecords.Clear();
                        break;
                }
            });

            ConveyorStartCommand = new RelayCommand(_ => _ = ConveyorStartAsync());
            ConveyorStopCommand = new RelayCommand(_ => _ = ConveyorStopAsync());
            ConveyorResumeCommand = new RelayCommand(_ => _ = ConveyorResumeAsync());

            _api = new ApiService("http://192.168.0.21:5000");

            _ = InitAsync();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (_, _) =>
            {
                CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _tick++;

                if (!_statusPolling)
                {
                    _statusPolling = true;

                    try
                    {
                        await _api.RefreshStatusAsync(this);
                    }
                    finally
                    {
                        _statusPolling = false;
                    }
                }
            };

            _timer.Start();
        }

        private async Task InitAsync()
        {
            await _api.LoadInitialDataAsync(this);
            _api.StartRealtimeEvents(this);
        }

        private async Task ToggleEmergencyAsync()
        {
            if (IsEmergencyStop)
            {
                IsEmergencyStop = false;

                DeviceStatus.ConveyorStatus = "재개중...";
                DeviceStatus.ConveyorSpeed = 180;
                RefreshDeviceStatus();

                await _api.EmergencyResetAsync();
                await _api.RefreshStatusAsync(this);
            }
            else
            {
                IsEmergencyStop = true;

                DeviceStatus.ConveyorStatus = "비상정지";
                DeviceStatus.ConveyorSpeed = 0;
                RefreshDeviceStatus();

                await _api.EmergencyStopAsync();
                await _api.RefreshStatusAsync(this);
            }
        }

        private async Task ConveyorStartAsync()
        {
            if (DeviceStatus.ConveyorStatus == "연결전" || DeviceStatus.ConveyorStatus == "연결안됨")
            {
                DeviceStatus.ConveyorStatus = "연결안됨";
                RefreshDeviceStatus();
                await Task.Delay(2000);
                DeviceStatus.ConveyorStatus = "연결전";
                RefreshDeviceStatus();
                return;
            }

            const int speed = 180;

            DeviceStatus.ConveyorStatus = "시작중...";
            DeviceStatus.ConveyorSpeed = speed;
            RefreshDeviceStatus();

            bool ok = await _api.ConveyorStartAsync(speed);

            if (ok)
            {
                DeviceStatus.ConveyorStatus = "동작중";
                DeviceStatus.ConveyorSpeed = speed;
                RefreshDeviceStatus();
            }
            else
            {
                DeviceStatus.ConveyorStatus = "오류";
                RefreshDeviceStatus();
            }

            await _api.RefreshStatusAsync(this);
        }

        private async Task ConveyorStopAsync()
        {
            if (DeviceStatus.ConveyorStatus == "연결전" || DeviceStatus.ConveyorStatus == "연결안됨")
            {
                DeviceStatus.ConveyorStatus = "연결안됨";
                RefreshDeviceStatus();
                await Task.Delay(2000);
                DeviceStatus.ConveyorStatus = "연결전";
                RefreshDeviceStatus();
                return;
            }

            DeviceStatus.ConveyorStatus = "정지중...";
            DeviceStatus.ConveyorSpeed = 0;
            RefreshDeviceStatus();

            bool ok = await _api.ConveyorStopAsync();

            if (ok)
            {
                DeviceStatus.ConveyorStatus = "정지중";
                DeviceStatus.ConveyorSpeed = 0;
                RefreshDeviceStatus();
            }
            else
            {
                DeviceStatus.ConveyorStatus = "오류";
                RefreshDeviceStatus();
            }

            await _api.RefreshStatusAsync(this);
        }

        private async Task ConveyorResumeAsync()
        {
            if (DeviceStatus.ConveyorStatus == "연결전" || DeviceStatus.ConveyorStatus == "연결안됨")
            {
                DeviceStatus.ConveyorStatus = "연결안됨";
                RefreshDeviceStatus();
                await Task.Delay(2000);
                DeviceStatus.ConveyorStatus = "연결전";
                RefreshDeviceStatus();
                return;
            }

            const int speed = 180;

            DeviceStatus.ConveyorStatus = "재개중";
            DeviceStatus.ConveyorSpeed = speed;
            RefreshDeviceStatus();

            bool ok = await _api.ConveyorResumeAsync();

            if (ok)
            {
                DeviceStatus.ConveyorStatus = "동작중";
                DeviceStatus.ConveyorSpeed = speed;
                RefreshDeviceStatus();
            }
            else
            {
                DeviceStatus.ConveyorStatus = "오류";
                RefreshDeviceStatus();
            }

            await _api.RefreshStatusAsync(this);
        }

        public async Task CleanupAsync()
        {
            _timer.Stop();
            await _api.DisconnectAsync();
        }

        private const string BlackboxRoot = @"blackbox\";
        private int _localId = 1;

        private BlackboxEvent CreateBlackboxEvent(
            string eventType, string description, string severity, string trackingNumber = "")
        {
            string subFolder = eventType switch
            {
                BlackboxEventType.OcrFail => @"ocr_fail\",
                BlackboxEventType.SortFail => @"sort_fail\",
                BlackboxEventType.Jam => @"jam\",
                _ => @"etc\"
            };

            string saveFolder = BlackboxRoot + subFolder;
            string fileName = $"{eventType}_{DateTime.Now:yyyyMMdd_HHmmss}_{_localId}.jpg";

            return new BlackboxEvent
            {
                Id = _localId++,
                Timestamp = DateTime.Now,
                EventType = eventType,
                Description = description,
                ImagePath = saveFolder + fileName,
                SaveFolder = saveFolder,
                Severity = severity,
                TrackingNumber = trackingNumber
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
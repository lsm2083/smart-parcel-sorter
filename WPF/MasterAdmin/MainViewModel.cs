using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        private readonly MqttService _mqtt;

        private int _tick = 0;
        private bool _statusPolling = false;
        private bool _robotMqttEventsRegistered = false;
        private bool _robotMqttConnecting = false;

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

        private bool _isRobotMqttConnected = false;
        public bool IsRobotMqttConnected
        {
            get => _isRobotMqttConnected;
            set
            {
                _isRobotMqttConnected = value;
                OnPropertyChanged();
            }
        }

        private bool _isRobotBusy = false;
        public bool IsRobotBusy
        {
            get => _isRobotBusy;
            set
            {
                _isRobotBusy = value;
                OnPropertyChanged();
            }
        }

        private string _robotLastMessage = "로봇 MQTT 연결 대기";
        public string RobotLastMessage
        {
            get => _robotLastMessage;
            set
            {
                _robotLastMessage = value;
                OnPropertyChanged();
            }
        }

        private string _robotCameraUrl = "";
        public string RobotCameraUrl
        {
            get => _robotCameraUrl;
            set
            {
                _robotCameraUrl = value;
                OnPropertyChanged();
            }
        }

        private string _robotAnglesText = "-";
        public string RobotAnglesText
        {
            get => _robotAnglesText;
            set
            {
                _robotAnglesText = value;
                OnPropertyChanged();
            }
        }

        private string _robotCoordsText = "-";
        public string RobotCoordsText
        {
            get => _robotCoordsText;
            set
            {
                _robotCoordsText = value;
                OnPropertyChanged();
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

        public ICommand RobotHomeCommand { get; }
        public ICommand RobotStatusCommand { get; }
        public ICommand RobotGetPoseCommand { get; }
        public ICommand RobotOpenGripperCommand { get; }
        public ICommand RobotCloseGripperCommand { get; }
        public ICommand RobotPickReadyCommand { get; }
        public ICommand RobotStopCommand { get; }
        public ICommand RobotEmergencyStopCommand { get; }
        public ICommand RobotResetEmergencyCommand { get; }
        public ICommand RobotCameraStatusCommand { get; }
        public ICommand RobotStartCameraCommand { get; }
        public ICommand RobotPlaceTestCommand { get; }
        public ICommand RobotSortTestCommand { get; }

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

            RobotHomeCommand = new RelayCommand(_ => _ = RobotHomeAsync());
            RobotStatusCommand = new RelayCommand(_ => _ = RobotStatusAsync());
            RobotGetPoseCommand = new RelayCommand(_ => _ = RobotGetPoseAsync());
            RobotOpenGripperCommand = new RelayCommand(_ => _ = RobotOpenGripperAsync());
            RobotCloseGripperCommand = new RelayCommand(_ => _ = RobotCloseGripperAsync());
            RobotPickReadyCommand = new RelayCommand(_ => _ = RobotPickReadyAsync());
            RobotStopCommand = new RelayCommand(_ => _ = RobotStopAsync());
            RobotEmergencyStopCommand = new RelayCommand(_ => _ = RobotEmergencyStopAsync());
            RobotResetEmergencyCommand = new RelayCommand(_ => _ = RobotResetEmergencyAsync());
            RobotCameraStatusCommand = new RelayCommand(_ => _ = RobotCameraStatusAsync());
            RobotStartCameraCommand = new RelayCommand(_ => _ = RobotStartCameraAsync());
            RobotPlaceTestCommand = new RelayCommand(p => _ = RobotPlaceTestAsync(p));
            RobotSortTestCommand = new RelayCommand(p => _ = RobotSortTestAsync(p));

            _api = new ApiService("http://192.168.0.21:5000");
            _mqtt = new MqttService("192.168.0.21");

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

                if (!IsRobotMqttConnected && !_robotMqttConnecting)
                {
                    _ = ConnectRobotMqttAsync();
                }
            };

            _timer.Start();
        }

        private async Task InitAsync()
        {
            await _api.LoadInitialDataAsync(this);
            _api.StartRealtimeEvents(this);

            await ConnectRobotMqttAsync();
        }

        private async Task ConnectRobotMqttAsync()
        {
            if (_robotMqttConnecting)
                return;

            if (_mqtt.IsConnected)
            {
                IsRobotMqttConnected = true;
                return;
            }

            _robotMqttConnecting = true;

            try
            {
                RegisterRobotMqttEvents();

                DeviceStatus.RobotArmStatus = "연결중";
                RobotLastMessage = "로봇 MQTT 연결 중...";
                RefreshDeviceStatus();

                await _mqtt.ConnectAsync();

                IsRobotMqttConnected = _mqtt.IsConnected;

                if (IsRobotMqttConnected)
                {
                    DeviceStatus.RobotArmStatus = "연결됨";
                    RobotLastMessage = "로봇 MQTT 연결 완료";
                    RefreshDeviceStatus();

                    await _mqtt.RobotStatusAsync();
                }
                else
                {
                    DeviceStatus.RobotArmStatus = "연결전";
                    RobotLastMessage = "로봇 MQTT 연결 실패";
                    RefreshDeviceStatus();
                }
            }
            catch (Exception ex)
            {
                IsRobotMqttConnected = false;
                DeviceStatus.RobotArmStatus = "연결전";
                RobotLastMessage = "로봇 MQTT 오류: " + ex.Message;
                RefreshDeviceStatus();
            }
            finally
            {
                _robotMqttConnecting = false;
            }
        }

        private void RegisterRobotMqttEvents()
        {
            if (_robotMqttEventsRegistered)
                return;

            _robotMqttEventsRegistered = true;

            _mqtt.RobotHeartbeatReceived += data =>
            {
                ApplyRobotPayload(data, "heartbeat");
            };

            _mqtt.RobotStatusReceived += data =>
            {
                ApplyRobotPayload(data, "status");
            };

            _mqtt.RobotResultReceived += data =>
            {
                ApplyRobotPayload(data, "result");
            };

            _mqtt.RobotMessageReceived += (topic, data) =>
            {
                System.Diagnostics.Debug.WriteLine("[ROBOT MQTT] " + topic + " / " + data.ToString(Formatting.None));
            };

            _mqtt.LogReceived += msg =>
            {
                System.Diagnostics.Debug.WriteLine(msg);
            };
        }

        private void ApplyRobotPayload(JObject data, string source)
        {
            Dispatch(() =>
            {
                IsRobotMqttConnected = true;

                string status = GetString(data,
                    "status",
                    "state",
                    "robot_state",
                    "result");

                string message = GetString(data,
                    "message",
                    "result",
                    "status",
                    "state");

                bool busy = GetBool(data, false,
                    "busy",
                    "is_busy",
                    "robot_busy");

                bool emergency = GetBool(data, false,
                    "emergency",
                    "emergency_stop",
                    "isEmergency",
                    "is_emergency");

                IsRobotBusy = busy;

                if (emergency)
                    IsEmergencyStop = true;

                string robotStatus = NormalizeRobotStatus(status, busy, emergency);

                if (!string.IsNullOrWhiteSpace(robotStatus))
                    DeviceStatus.RobotArmStatus = robotStatus;

                string cameraUrl = GetString(data, "camera_url", "cameraUrl");

                if (!string.IsNullOrWhiteSpace(cameraUrl))
                    RobotCameraUrl = cameraUrl;

                JToken? angles = data["angles"] ?? data["current_angles"] ?? data["robot_angles"];

                if (angles != null)
                    RobotAnglesText = angles.ToString(Formatting.None);

                JToken? coords = data["coords"] ?? data["current_coords"] ?? data["robot_coords"];

                if (coords != null)
                    RobotCoordsText = coords.ToString(Formatting.None);

                if (!string.IsNullOrWhiteSpace(message))
                    RobotLastMessage = $"[{source}] {message}";
                else if (!string.IsNullOrWhiteSpace(status))
                    RobotLastMessage = $"[{source}] {status}";
                else
                    RobotLastMessage = $"[{source}] 로봇 상태 수신";

                RefreshDeviceStatus();
            });
        }

        private string NormalizeRobotStatus(string status, bool busy, bool emergency)
        {
            string s = (status ?? "").Trim().ToUpperInvariant();

            if (emergency || s.Contains("EMERGENCY"))
                return "비상정지";

            if (s.Contains("ERROR") || s.Contains("FAIL"))
                return "오류";

            if (s.Contains("STOPPED"))
                return "정지중";

            if (s.Contains("CONNECTED"))
                return "연결됨";

            if (s.Contains("READY") || s == "READY" || s.Contains("HEARTBEAT"))
            {
                if (busy)
                    return "동작중";

                return "대기";
            }

            if (s.Contains("MOVING") ||
                s.Contains("SORT") ||
                s.Contains("PICK") ||
                s.Contains("PLACE") ||
                s.Contains("CARRY") ||
                s.Contains("RETURN") ||
                s.Contains("HOME"))
            {
                return "동작중";
            }

            if (busy)
                return "동작중";

            if (string.IsNullOrWhiteSpace(s))
                return DeviceStatus.RobotArmStatus;

            return "대기";
        }

        private async Task EnsureRobotMqttConnectedAsync()
        {
            if (_mqtt.IsConnected)
            {
                IsRobotMqttConnected = true;
                return;
            }

            await ConnectRobotMqttAsync();
        }

        private async Task RobotHomeAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            DeviceStatus.RobotArmStatus = "이동중";
            RobotLastMessage = "HOME 명령 전송";
            RefreshDeviceStatus();

            bool ok = await _mqtt.RobotHomeAsync();

            if (!ok)
                SetRobotCommandFail("HOME 명령 실패");
        }

        private async Task RobotStatusAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            RobotLastMessage = "STATUS 명령 전송";
            bool ok = await _mqtt.RobotStatusAsync();

            if (!ok)
                SetRobotCommandFail("STATUS 명령 실패");
        }

        private async Task RobotGetPoseAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            RobotLastMessage = "현재 좌표 확인 명령 전송";
            bool ok = await _mqtt.RobotGetPoseAsync();

            if (!ok)
                SetRobotCommandFail("좌표 확인 명령 실패");
        }

        private async Task RobotOpenGripperAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            DeviceStatus.RobotArmStatus = "동작중";
            RobotLastMessage = "그리퍼 열기 명령 전송";
            RefreshDeviceStatus();

            bool ok = await _mqtt.RobotOpenGripperAsync();

            if (!ok)
                SetRobotCommandFail("그리퍼 열기 실패");
        }

        private async Task RobotCloseGripperAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            DeviceStatus.RobotArmStatus = "동작중";
            RobotLastMessage = "그리퍼 닫기 명령 전송";
            RefreshDeviceStatus();

            bool ok = await _mqtt.RobotCloseGripperAsync();

            if (!ok)
                SetRobotCommandFail("그리퍼 닫기 실패");
        }

        private async Task RobotPickReadyAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            DeviceStatus.RobotArmStatus = "이동중";
            RobotLastMessage = "집기 준비 위치 이동 명령 전송";
            RefreshDeviceStatus();

            bool ok = await _mqtt.RobotPickReadyAsync();

            if (!ok)
                SetRobotCommandFail("집기 준비 위치 이동 실패");
        }

        private async Task RobotStopAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            DeviceStatus.RobotArmStatus = "정지중";
            RobotLastMessage = "로봇 STOP 명령 전송";
            RefreshDeviceStatus();

            bool ok = await _mqtt.RobotStopAsync();

            if (!ok)
                SetRobotCommandFail("STOP 명령 실패");
        }

        private async Task RobotEmergencyStopAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            IsEmergencyStop = true;
            DeviceStatus.RobotArmStatus = "비상정지";
            RobotLastMessage = "로봇 비상정지 명령 전송";
            RefreshDeviceStatus();

            bool ok = await _mqtt.RobotEmergencyStopAsync();

            if (!ok)
                SetRobotCommandFail("로봇 비상정지 명령 실패");
        }

        private async Task RobotResetEmergencyAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            IsEmergencyStop = false;
            DeviceStatus.RobotArmStatus = "재개중";
            RobotLastMessage = "로봇 비상정지 해제 명령 전송";
            RefreshDeviceStatus();

            bool ok = await _mqtt.RobotResetEmergencyAsync();

            if (!ok)
                SetRobotCommandFail("로봇 비상정지 해제 실패");
        }

        private async Task RobotCameraStatusAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            RobotLastMessage = "로봇 카메라 상태 확인 명령 전송";
            bool ok = await _mqtt.RobotCameraStatusAsync();

            if (!ok)
                SetRobotCommandFail("로봇 카메라 상태 확인 실패");
        }

        private async Task RobotStartCameraAsync()
        {
            await EnsureRobotMqttConnectedAsync();

            RobotLastMessage = "로봇 카메라 시작 명령 전송";
            bool ok = await _mqtt.RobotStartCameraAsync();

            if (!ok)
                SetRobotCommandFail("로봇 카메라 시작 실패");
        }

        private async Task RobotPlaceTestAsync(object? parameter)
        {
            int slot = ParseSlot(parameter);

            await EnsureRobotMqttConnectedAsync();

            DeviceStatus.RobotArmStatus = "이동중";
            RobotLastMessage = $"{slot}번 슬롯 위치 테스트 명령 전송";
            RefreshDeviceStatus();

            bool ok = await _mqtt.RobotPlaceTestAsync(slot);

            if (!ok)
                SetRobotCommandFail($"{slot}번 슬롯 위치 테스트 실패");
        }

        private async Task RobotSortTestAsync(object? parameter)
        {
            int slot = ParseSlot(parameter);

            await EnsureRobotMqttConnectedAsync();

            DeviceStatus.RobotArmStatus = "동작중";
            RobotLastMessage = $"{slot}번 슬롯 SORT 테스트 명령 전송";
            RefreshDeviceStatus();

            string packageId = "WPF_TEST_" + DateTime.Now.ToString("HHmmss");
            string sortCode = SlotToSortCode(slot);

            bool ok = await _mqtt.RobotSortAsync(
                slot,
                packageId,
                sortCode,
                0,
                0,
                0
            );

            if (!ok)
                SetRobotCommandFail($"{slot}번 슬롯 SORT 테스트 실패");
        }

        private int ParseSlot(object? parameter)
        {
            if (parameter == null)
                return 1;

            if (int.TryParse(parameter.ToString(), out int slot))
            {
                if (slot < 1) return 1;
                if (slot > 9) return 9;
                return slot;
            }

            return 1;
        }

        private string SlotToSortCode(int slot)
        {
            return slot switch
            {
                1 => "SEOUL_BOX",
                2 => "SEOUL_VINYL",
                3 => "GYEONGGI_BOX",
                4 => "GYEONGGI_VINYL",
                5 => "BUSAN_BOX",
                6 => "BUSAN_VINYL",
                7 => "DAEGU_BOX",
                8 => "DAEGU_VINYL",
                9 => "ETC_BOX",
                _ => "UNKNOWN_BOX"
            };
        }

        private void SetRobotCommandFail(string message)
        {
            DeviceStatus.RobotArmStatus = "오류";
            RobotLastMessage = message;
            RefreshDeviceStatus();
        }

        private async Task ToggleEmergencyAsync()
        {
            if (IsEmergencyStop)
            {
                IsEmergencyStop = false;

                DeviceStatus.ConveyorStatus = "재개중...";
                DeviceStatus.ConveyorSpeed = 180;
                DeviceStatus.RobotArmStatus = "재개중";
                RefreshDeviceStatus();

                await _api.EmergencyResetAsync();

                try
                {
                    await RobotResetEmergencyAsync();
                }
                catch
                {
                }

                await _api.RefreshStatusAsync(this);
            }
            else
            {
                IsEmergencyStop = true;

                DeviceStatus.ConveyorStatus = "비상정지";
                DeviceStatus.ConveyorSpeed = 0;
                DeviceStatus.RobotArmStatus = "비상정지";
                RefreshDeviceStatus();

                await _api.EmergencyStopAsync();

                try
                {
                    await RobotEmergencyStopAsync();
                }
                catch
                {
                }

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

            try
            {
                _mqtt.Dispose();
            }
            catch
            {
            }

            await _api.DisconnectAsync();
        }

        private const string BlackboxRoot = @"blackbox\";
        private int _localId = 1;

        private BlackboxEvent CreateBlackboxEvent(
            string eventType,
            string description,
            string severity,
            string trackingNumber = "")
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

        private static string GetString(JObject obj, params string[] names)
        {
            foreach (string name in names)
            {
                JToken? token = obj[name];

                if (token == null)
                    continue;

                string? value = token.ToString();

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static bool GetBool(JObject obj, bool defaultValue, params string[] names)
        {
            foreach (string name in names)
            {
                JToken? token = obj[name];

                if (token == null)
                    continue;

                if (token.Type == JTokenType.Boolean)
                    return token.Value<bool>();

                string text = token.ToString().Trim().ToLowerInvariant();

                if (text == "true" || text == "1" || text == "yes" || text == "on")
                    return true;

                if (text == "false" || text == "0" || text == "no" || text == "off")
                    return false;
            }

            return defaultValue;
        }

        private static void Dispatch(Action action)
        {
            var app = Application.Current;

            if (app == null || app.Dispatcher == null || app.Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            app.Dispatcher.Invoke(action);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
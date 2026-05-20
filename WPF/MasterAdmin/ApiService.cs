// ApiService.cs
// NuGet 패키지 2개 추가:
//   Install-Package SocketIOClient
//   Install-Package Newtonsoft.Json

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SocketIOClient;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace MasterAdmin
{
    public class ApiService
    {
        private readonly HttpClient _http;
        private readonly SocketIO _socket;

        public ApiService(string serverUrl)
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };

            _socket = new SocketIO(serverUrl, new SocketIOOptions
            {
                Reconnection = true,
                ReconnectionAttempts = int.MaxValue,
                ReconnectionDelay = 1000,
            });
        }

        // ── REST: 초기 데이터 로딩 ────────────────────────────────────────

        public async Task LoadInitialDataAsync(MainViewModel vm)
        {
            await Task.WhenAll(
                LoadStatusAsync(vm),
                LoadSortLogsAsync(vm),
                LoadShippingLogsAsync(vm),
                LoadBlackboxEventsAsync(vm),
                LoadLoginRecordsAsync(vm)
            );
        }

        private async Task LoadStatusAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/status");
                var d = JObject.Parse(json);
                Dispatch(() =>
                {
                    // DeviceStatus는 vm의 객체 프로퍼티이므로 직접 필드 대입
                    vm.DeviceStatus.ConveyorStatus = d["conveyorStatus"]?.ToString() ?? "-";
                    vm.DeviceStatus.ConveyorSpeed = d["conveyorSpeed"]?.Value<double>() ?? 0;
                    vm.DeviceStatus.RobotArmStatus = d["robotArmStatus"]?.ToString() ?? "-";
                    vm.DeviceStatus.OcrCamStatus = d["ocrCamStatus"]?.ToString() ?? "-";
                    vm.DeviceStatus.QrCamStatus = d["qrCamStatus"]?.ToString() ?? "-";
                    vm.DeviceStatus.EmergencyStop = d["emergencyStop"]?.Value<bool>() ?? false;
                    vm.DeviceStatus.InputUnitStatus = d["inputUnitStatus"]?.ToString() ?? "-";
                    vm.DeviceStatus.TodaySortedCount = d["todaySortedCount"]?.Value<int>() ?? 0;
                    vm.DeviceStatus.TodayErrorCount = d["todayErrorCount"]?.Value<int>() ?? 0;
                    vm.DeviceStatus.SuccessRate = d["successRate"]?.Value<double>() ?? 0;
                    vm.RefreshDeviceStatus();
                });
            }
            catch (Exception ex) { Log("LoadStatus", ex); }
        }

        private async Task LoadSortLogsAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/logs/sort");
                // Newtonsoft.Json 기본 설정으로 camelCase → PascalCase 자동 매핑
                var logs = Deserialize<List<SortingLog>>(json) ?? new();
                Dispatch(() =>
                {
                    vm.SortingLogs.Clear();
                    foreach (var l in logs) vm.SortingLogs.Add(l);
                });
            }
            catch (Exception ex) { Log("LoadSortLogs", ex); }
        }

        private async Task LoadShippingLogsAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/logs/shipping");
                var logs = Deserialize<List<ShippingLog>>(json) ?? new();
                Dispatch(() =>
                {
                    vm.ShippingLogs.Clear();
                    foreach (var l in logs) vm.ShippingLogs.Add(l);
                });
            }
            catch (Exception ex) { Log("LoadShippingLogs", ex); }
        }

        private async Task LoadBlackboxEventsAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/blackbox/events");
                var events = Deserialize<List<BlackboxEvent>>(json) ?? new();
                Dispatch(() =>
                {
                    vm.BlackboxEvents.Clear();
                    foreach (var e in events) vm.BlackboxEvents.Add(e);
                });
            }
            catch (Exception ex) { Log("LoadBlackboxEvents", ex); }
        }

        private async Task LoadLoginRecordsAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/logs/login");
                var records = Deserialize<List<LoginRecord>>(json) ?? new();
                Dispatch(() =>
                {
                    vm.LoginRecords.Clear();
                    foreach (var r in records) vm.LoginRecords.Add(r);
                });
            }
            catch (Exception ex) { Log("LoadLoginRecords", ex); }
        }

        // ── REST: 컨베이어 제어

        public async Task<bool> ConveyorStartAsync(int speed = 180)
        {
            try
            {
                string json = "{\"command\":\"CONVEYOR_START\",\"speed\":" + speed + "}";
                var body    = new StringContent(json, Encoding.UTF8, "application/json");
                var res     = await _http.PostAsync("api/conveyor/command", body);
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex) { Log("ConveyorStart", ex); return false; }
        }

        public async Task<bool> ConveyorStopAsync()
        {
            try
            {
                string json = "{\"command\":\"CONVEYOR_STOP\"}";
                var body    = new StringContent(json, Encoding.UTF8, "application/json");
                var res     = await _http.PostAsync("api/conveyor/command", body);
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex) { Log("ConveyorStop", ex); return false; }
        }

        public async Task<bool> ConveyorResumeAsync()
        {
            try
            {
                string json = "{\"command\":\"CONVEYOR_START\",\"speed\":180}";
                var body    = new StringContent(json, Encoding.UTF8, "application/json");
                var res     = await _http.PostAsync("api/conveyor/command", body);
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex) { Log("ConveyorResume", ex); return false; }
        }

        // ── REST: 비상정지 ────────────────────────────────────────────────

        public async Task<bool> EmergencyStopAsync()
        {
            try
            {
                var body = new StringContent("{\"source\":\"WPF\"}", Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/emergency/stop", body);
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex) { Log("EmergencyStop", ex); return false; }
        }

        public async Task<bool> EmergencyResetAsync()
        {
            try
            {
                var body = new StringContent("{}", Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/emergency/reset", body);
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex) { Log("EmergencyReset", ex); return false; }
        }

        // ── WebSocket: 실시간 이벤트 구독 ────────────────────────────────

        public void StartRealtimeEvents(MainViewModel vm)
        {
            // 장치 상태 전체 갱신
            _socket.On("device_status", resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>();
                    Dispatch(() =>
                    {
                        if (d["conveyorStatus"] != null) vm.DeviceStatus.ConveyorStatus = d["conveyorStatus"]!.ToString();
                        if (d["conveyorSpeed"] != null) vm.DeviceStatus.ConveyorSpeed = d["conveyorSpeed"]!.Value<double>();
                        if (d["robotArmStatus"] != null) vm.DeviceStatus.RobotArmStatus = d["robotArmStatus"]!.ToString();
                        if (d["ocrCamStatus"] != null) vm.DeviceStatus.OcrCamStatus = d["ocrCamStatus"]!.ToString();
                        if (d["qrCamStatus"] != null) vm.DeviceStatus.QrCamStatus = d["qrCamStatus"]!.ToString();
                        if (d["todaySortedCount"] != null) vm.DeviceStatus.TodaySortedCount = d["todaySortedCount"]!.Value<int>();
                        if (d["todayErrorCount"] != null) vm.DeviceStatus.TodayErrorCount = d["todayErrorCount"]!.Value<int>();
                        if (d["successRate"] != null) vm.DeviceStatus.SuccessRate = d["successRate"]!.Value<double>();
                        vm.RefreshDeviceStatus();
                    });
                }
                catch (Exception ex) { Log("device_status", ex); }
            });

            // 분류 로그 실시간 추가 + 실패 시 녹화 트리거
            _socket.On("sorting_log_added", resp =>
            {
                try
                {
                    var log = resp.GetValue<SortingLog>(0);
                    Dispatch(() =>
                    {
                        vm.SortingLogs.Insert(0, log);
                        if (vm.SortingLogs.Count > 60)
                            vm.SortingLogs.RemoveAt(vm.SortingLogs.Count - 1);

                        // 실패 시 녹화 트리거
                        if (log.Status == "불량")
                            vm.TriggerRecording?.Invoke(log.ErrorType ?? "인식실패");
                    });
                }
                catch (Exception ex) { Log("sorting_log_added", ex); }
            });

            // 출고 로그 실시간 추가
            _socket.On("shipping_log_added", resp =>
            {
                try
                {
                    var log = resp.GetValue<ShippingLog>();
                    Dispatch(() =>
                    {
                        vm.ShippingLogs.Insert(0, log);
                        if (vm.ShippingLogs.Count > 40) vm.ShippingLogs.RemoveAt(vm.ShippingLogs.Count - 1);
                    });
                }
                catch (Exception ex) { Log("shipping_log_added", ex); }
            });

            // 블랙박스 이벤트 실시간 추가
            _socket.On("blackbox_event_added", resp =>
            {
                try
                {
                    var ev = resp.GetValue<BlackboxEvent>();
                    Dispatch(() =>
                    {
                        vm.BlackboxEvents.Insert(0, ev);
                        if (vm.BlackboxEvents.Count > 100) vm.BlackboxEvents.RemoveAt(vm.BlackboxEvents.Count - 1);
                    });
                }
                catch (Exception ex) { Log("blackbox_event_added", ex); }
            });

            // 비상정지 상태 갱신
            _socket.On("emergency_stop", resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>();
                    bool isEmergency = d["isEmergency"]?.Value<bool>() ?? false;
                    Dispatch(() => vm.IsEmergencyStop = isEmergency);
                }
                catch (Exception ex) { Log("emergency_stop", ex); }
            });

            // 장치 연결 / 해제 → 상태 라벨 업데이트
            _socket.On("device_connected", resp => HandleDeviceChange(vm, resp, "작동중"));
            _socket.On("device_disconnected", resp => HandleDeviceChange(vm, resp, "오프라인"));

            // 물리 비상정지 버튼 눌림 (민지)
            _socket.On("physical_estop", resp =>
            {
                try { Dispatch(() => { vm.IsEmergencyStop = true; vm.DeviceStatus.ConveyorStatus = "비상정지"; vm.RefreshDeviceStatus(); }); }
                catch (Exception ex) { Log("physical_estop", ex); }
            });

            // 물리 비상정지 버튼 풀림 (민지)
            _socket.On("estop_released", resp =>
            {
                try { Dispatch(() => { vm.IsEmergencyStop = false; vm.DeviceStatus.ConveyorStatus = "대기"; vm.RefreshDeviceStatus(); }); }
                catch (Exception ex) { Log("estop_released", ex); }
            });

            _ = _socket.ConnectAsync();
        }

        private static void HandleDeviceChange(MainViewModel vm, SocketIOResponse resp, string label)
        {
            try
            {
                var d = resp.GetValue<JObject>();
                string deviceId = d["device_id"]?.ToString() ?? "";
                Dispatch(() =>
                {
                    switch (deviceId)
                    {
                        case "conveyor_agent_01":
                            vm.DeviceStatus.ConveyorStatus = label; break;
                        case "robot_agent_01":
                            vm.DeviceStatus.RobotArmStatus = label; break;
                        case "vision_agent_01":
                            vm.DeviceStatus.OcrCamStatus = label;
                            vm.DeviceStatus.QrCamStatus = label; break;
                    }
                    vm.RefreshDeviceStatus();
                });
            }
            catch (Exception ex) { Log("device change", ex); }
        }

        // ── 유틸 ─────────────────────────────────────────────────────────

        // Newtonsoft.Json: camelCase(JSON) → PascalCase(C#) 자동 매핑
        private static readonly JsonSerializerSettings _jsonSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTime,
        };

        private static T? Deserialize<T>(string json) =>
            JsonConvert.DeserializeObject<T>(json, _jsonSettings);

        private static void Dispatch(Action action) =>
            Application.Current?.Dispatcher.Invoke(action);

        private static void Log(string tag, Exception ex) =>
            Console.WriteLine($"[ApiService:{tag}] {ex.Message}");

        public async Task DisconnectAsync()
        {
            await _socket.DisconnectAsync();
            _http.Dispose();
        }
    }
}

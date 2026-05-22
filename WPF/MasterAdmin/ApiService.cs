using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SocketIOClient;
//using SocketIO.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace MasterAdmin
{
    public class ApiService
    {
        private readonly HttpClient _http;
        private readonly SocketIO _socket;
        private bool _statusPollingStarted = false;
        private bool _statusPollingBusy = false;

        public ApiService(string serverUrl)
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(30)
            };

            _socket = new SocketIO(serverUrl);


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
            await RefreshStatusAsync(vm);
        }

        public async Task RefreshStatusAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/status");
                var d = JObject.Parse(json);

                Dispatch(() =>
                {
                    ApplyDeviceStatus(vm, d, "api/status");
                });
            }
            catch (Exception ex)
            {
                Log("RefreshStatus", ex);
                Dispatch(() =>
                {
                    vm.DeviceStatus.ConveyorStatus = "연결전";
                    vm.RefreshDeviceStatus();
                });
            }
        }

        private async Task LoadSortLogsAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/logs/sort");
                var logs = Deserialize<List<SortingLog>>(json) ?? new();

                Dispatch(() =>
                {
                    vm.SortingLogs.Clear();

                    foreach (var l in logs)
                        vm.SortingLogs.Add(l);
                });
            }
            catch (Exception ex)
            {
                Log("LoadSortLogs", ex);
            }
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

                    foreach (var l in logs)
                        vm.ShippingLogs.Add(l);
                });
            }
            catch (Exception ex)
            {
                Log("LoadShippingLogs", ex);
            }
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

                    foreach (var e in events)
                        vm.BlackboxEvents.Add(e);
                });
            }
            catch (Exception ex)
            {
                Log("LoadBlackboxEvents", ex);
            }
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

                    foreach (var r in records)
                        vm.LoginRecords.Add(r);
                });
            }
            catch (Exception ex)
            {
                Log("LoadLoginRecords", ex);
            }
        }

        // ── REST: 컨베이어 제어 ───────────────────────────────────────────

        public async Task<bool> ConveyorStartAsync(int speed = 180)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[컨베이어] 시작 명령 전송...");

                string json = "{\"command\":\"CONVEYOR_START\",\"speed\":" + speed + "}";
                var body = new StringContent(json, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/conveyor/command", body);

                System.Diagnostics.Debug.WriteLine("[컨베이어] 응답: " + res.StatusCode);

                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("ConveyorStart", ex);
                return false;
            }
        }

        public async Task<bool> ConveyorStopAsync()
        {
            try
            {
                string json = "{\"command\":\"CONVEYOR_STOP\"}";
                var body = new StringContent(json, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/conveyor/command", body);

                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("ConveyorStop", ex);
                return false;
            }
        }

        public async Task<bool> ConveyorResumeAsync()
        {
            try
            {
                string json = "{\"command\":\"CONVEYOR_START\",\"speed\":180}";
                var body = new StringContent(json, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/conveyor/command", body);

                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("ConveyorResume", ex);
                return false;
            }
        }

        // ── REST: 비상정지 ────────────────────────────────────────────────

        public async Task<bool> EmergencyStopAsync()
        {
            try
            {
                string json = "{\"command\":\"EMERGENCY_STOP\"}";
                var body = new StringContent(json, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/conveyor/command", body);

                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("EmergencyStop", ex);
                return false;
            }
        }

        public async Task<bool> EmergencyResetAsync()
        {
            try
            {
                string json = "{\"command\":\"CONVEYOR_START\",\"speed\":180}";
                var body = new StringContent(json, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/conveyor/command", body);

                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("EmergencyReset", ex);
                return false;
            }
        }

        // ── WebSocket: 실시간 이벤트 구독 ────────────────────────────────

        public void StartRealtimeEvents(MainViewModel vm)
        {
            _socket.OnConnected += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[Flask] WebSocket 연결 성공!");

                Dispatch(() =>
                {
                    DebugWindow.Instance.SetConnected(true);
                    vm.DeviceStatus.ConveyorStatus = "연결됨";
                    vm.RefreshDeviceStatus();
                });
            };

            _socket.OnDisconnected += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[Flask] WebSocket 연결 끊김");

                Dispatch(() =>
                {
                    DebugWindow.Instance.SetConnected(false);
                    vm.DeviceStatus.ConveyorStatus = "연결전";
                    vm.DeviceStatus.ConveyorSpeed = 0;
                    vm.RefreshDeviceStatus();
                });
            };

            RegisterDeviceStatusEvent(vm, "device_status");
            RegisterDeviceStatusEvent(vm, "conveyor_status");
            RegisterDeviceStatusEvent(vm, "conveyor_speed");
            RegisterDeviceStatusEvent(vm, "status_update");
            RegisterDeviceStatusEvent(vm, "command_log_added");
            RegisterDeviceStatusEvent(vm, "command_added");
            RegisterDeviceStatusEvent(vm, "conveyor_command");
            RegisterDeviceStatusEvent(vm, "conveyor_command_added");

            _socket.On("sorting_log_added", resp =>
            {
                try
                {
                    var log = resp.GetValue<SortingLog>(0);

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("sorting_log_added", $"운송장:{log.TrackingNumber} 상태:{log.Status}");

                        vm.SortingLogs.Insert(0, log);

                        if (vm.SortingLogs.Count > 60)
                            vm.SortingLogs.RemoveAt(vm.SortingLogs.Count - 1);

                        if (log.Status == "불량")
                            vm.TriggerRecording?.Invoke(log.ErrorType ?? "인식실패");
                    });
                }
                catch (Exception ex)
                {
                    Log("sorting_log_added", ex);
                }
            });

            _socket.On("shipping_log_added", resp =>
            {
                try
                {
                    var log = resp.GetValue<ShippingLog>(0);

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("shipping_log_added", $"운송장:{log.TrackingNumber}");

                        vm.ShippingLogs.Insert(0, log);

                        if (vm.ShippingLogs.Count > 40)
                            vm.ShippingLogs.RemoveAt(vm.ShippingLogs.Count - 1);
                    });
                }
                catch (Exception ex)
                {
                    Log("shipping_log_added", ex);
                }
            });

            _socket.On("blackbox_event_added", resp =>
            {
                try
                {
                    var ev = resp.GetValue<BlackboxEvent>(0);

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("blackbox_event_added", ev.Description ?? "");

                        vm.BlackboxEvents.Insert(0, ev);

                        if (vm.BlackboxEvents.Count > 100)
                            vm.BlackboxEvents.RemoveAt(vm.BlackboxEvents.Count - 1);
                    });
                }
                catch (Exception ex)
                {
                    Log("blackbox_event_added", ex);
                }
            });

            _socket.On("emergency_stop", resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>(0);
                    bool isEmergency = GetBool(d, false, "isEmergency", "is_emergency", "emergencyStop", "emergency_stop");

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("emergency_stop", $"isEmergency:{isEmergency}");
                        vm.IsEmergencyStop = isEmergency;
                    });
                }
                catch (Exception ex)
                {
                    Log("emergency_stop", ex);
                }
            });

            _socket.On("physical_estop", resp =>
            {
                try
                {
                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("physical_estop", "민지 비상정지 버튼 눌림");
                        vm.IsEmergencyStop = true;
                        vm.DeviceStatus.ConveyorStatus = "비상정지";
                        vm.DeviceStatus.ConveyorSpeed = 0;
                        vm.RefreshDeviceStatus();
                    });
                }
                catch (Exception ex)
                {
                    Log("physical_estop", ex);
                }
            });

            _socket.On("estop_released", resp =>
            {
                try
                {
                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("estop_released", "민지 비상정지 버튼 풀림");
                        vm.IsEmergencyStop = false;
                        vm.DeviceStatus.ConveyorStatus = "작동중";
                        vm.RefreshDeviceStatus();
                    });
                }
                catch (Exception ex)
                {
                    Log("estop_released", ex);
                }
            });

            _socket.On("device_connected", resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>(0);

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("device_connected", d["device_id"]?.ToString() ?? "");
                    });

                    HandleDeviceChange(vm, d, "작동중");
                }
                catch (Exception ex)
                {
                    Log("device_connected", ex);
                }
            });

            _socket.On("device_disconnected", resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>(0);

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("device_disconnected", d["device_id"]?.ToString() ?? "");
                    });

                    HandleDeviceChange(vm, d, "오프라인");
                }
                catch (Exception ex)
                {
                    Log("device_disconnected", ex);
                }
            });

            _ = _socket.ConnectAsync();

            StartStatusPolling(vm);
        }

        private void RegisterDeviceStatusEvent(MainViewModel vm, string eventName)
        {
            _socket.On(eventName, resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>(0);

                    Dispatch(() =>
                    {
                        ApplyDeviceStatus(vm, d, eventName);
                    });
                }
                catch (Exception ex)
                {
                    Log(eventName, ex);
                }
            });
        }

        private void StartStatusPolling(MainViewModel vm)
        {
            if (_statusPollingStarted)
                return;

            _statusPollingStarted = true;

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (!_statusPollingBusy)
                        {
                            _statusPollingBusy = true;

                            try
                            {
                                await RefreshStatusAsync(vm);
                            }
                            finally
                            {
                                _statusPollingBusy = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("StatusPolling", ex);
                    }

                    await Task.Delay(1000);
                }
            });
        }

        private static void ApplyDeviceStatus(MainViewModel vm, JObject d, string source)
        {
            DebugWindow.Instance.AddLog(source, d.ToString(Formatting.None));

            JObject normalized = NormalizePayloadObject(d);

            if (TryGetDouble(normalized, out double conveyorSpeed,
                "conveyorSpeed",
                "conveyor_speed",
                "speed",
                "Speed",
                "currentSpeed",
                "current_speed",
                "pwm",
                "PWM",
                "motorSpeed",
                "motor_speed"))
            {
                vm.DeviceStatus.ConveyorSpeed = conveyorSpeed;
                DebugWindow.Instance.AddLog("conveyorSpeed", conveyorSpeed.ToString(CultureInfo.InvariantCulture));
            }

            string command = GetString(normalized, "", "command", "Command");

            if (command == "CONVEYOR_START")
            {
                vm.DeviceStatus.ConveyorStatus = "동작중";

                if (vm.DeviceStatus.ConveyorSpeed <= 0)
                {
                    double speed = GetDouble(normalized, 180, "speed", "Speed", "conveyorSpeed", "conveyor_speed");
                    vm.DeviceStatus.ConveyorSpeed = speed;
                    DebugWindow.Instance.AddLog("conveyorSpeed", speed.ToString(CultureInfo.InvariantCulture));
                }
            }
            else if (command == "CONVEYOR_STOP" || command == "EMERGENCY_STOP")
            {
                vm.DeviceStatus.ConveyorStatus = command == "EMERGENCY_STOP" ? "비상정지" : "정지중";
                vm.DeviceStatus.ConveyorSpeed = 0;
                DebugWindow.Instance.AddLog("conveyorSpeed", "0");
            }

            if (TryGetString(normalized, out string conveyorStatus,
                "conveyorStatus",
                "conveyor_status",
                "status",
                "state"))
            {
                if (conveyorStatus == "CONVEYOR_START")
                    vm.DeviceStatus.ConveyorStatus = "동작중";
                else if (conveyorStatus == "CONVEYOR_STOP")
                    vm.DeviceStatus.ConveyorStatus = "정지중";
                else if (conveyorStatus == "EMERGENCY_STOP")
                    vm.DeviceStatus.ConveyorStatus = "비상정지";
                else
                    vm.DeviceStatus.ConveyorStatus = conveyorStatus;
            }

            if (TryGetString(normalized, out string robotArmStatus,
                "robotArmStatus",
                "robot_arm_status",
                "robotStatus",
                "robot_status"))
            {
                vm.DeviceStatus.RobotArmStatus = robotArmStatus;
            }

            if (TryGetString(normalized, out string ocrCamStatus,
                "ocrCamStatus",
                "ocr_cam_status",
                "ocrStatus",
                "ocr_status"))
            {
                vm.DeviceStatus.OcrCamStatus = ocrCamStatus;
            }

            if (TryGetString(normalized, out string qrCamStatus,
                "qrCamStatus",
                "qr_cam_status",
                "qrStatus",
                "qr_status"))
            {
                vm.DeviceStatus.QrCamStatus = qrCamStatus;
            }

            if (TryGetString(normalized, out string inputUnitStatus,
                "inputUnitStatus",
                "input_unit_status"))
            {
                vm.DeviceStatus.InputUnitStatus = inputUnitStatus;
            }

            if (TryGetInt(normalized, out int todaySortedCount,
                "todaySortedCount",
                "today_sorted_count",
                "sortedCount",
                "sorted_count"))
            {
                vm.DeviceStatus.TodaySortedCount = todaySortedCount;
            }

            if (TryGetInt(normalized, out int todayErrorCount,
                "todayErrorCount",
                "today_error_count",
                "errorCount",
                "error_count"))
            {
                vm.DeviceStatus.TodayErrorCount = todayErrorCount;
            }

            if (TryGetDouble(normalized, out double successRate,
                "successRate",
                "success_rate"))
            {
                vm.DeviceStatus.SuccessRate = successRate;
            }

            if (TryGetBool(normalized, out bool emergencyStop,
                "emergencyStop",
                "emergency_stop",
                "isEmergency",
                "is_emergency"))
            {
                vm.DeviceStatus.EmergencyStop = emergencyStop;
                vm.IsEmergencyStop = emergencyStop;
            }

            vm.RefreshDeviceStatus();
        }

        private static JObject NormalizePayloadObject(JObject source)
        {
            JObject merged = new JObject();

            foreach (var prop in source.Properties())
                merged[prop.Name] = prop.Value.DeepClone();

            MergeNestedObject(merged, source, "data");
            MergeNestedObject(merged, source, "deviceStatus");
            MergeNestedObject(merged, source, "device_status");
            MergeNestedObject(merged, source, "conveyor");
            MergeNestedObject(merged, source, "statusData");
            MergeNestedObject(merged, source, "status_data");
            MergeNestedObject(merged, source, "lastCommand");
            MergeNestedObject(merged, source, "last_command");
            MergeNestedObject(merged, source, "latestCommand");
            MergeNestedObject(merged, source, "latest_command");
            MergeNestedObject(merged, source, "commandData");
            MergeNestedObject(merged, source, "command_data");
            MergeNestedObject(merged, source, "payload");

            return merged;
        }

        private static void MergeNestedObject(JObject target, JObject source, string key)
        {
            JToken? token = source[key];

            if (token == null || token.Type == JTokenType.Null)
                return;

            JObject? obj = null;

            if (token.Type == JTokenType.Object)
            {
                obj = (JObject)token;
            }
            else if (token.Type == JTokenType.String)
            {
                string text = token.ToString();

                if (text.StartsWith("{") && text.EndsWith("}"))
                {
                    try
                    {
                        obj = JObject.Parse(text);
                    }
                    catch
                    {
                        obj = null;
                    }
                }
            }

            if (obj == null)
                return;

            foreach (var prop in obj.Properties())
            {
                if (target[prop.Name] == null || target[prop.Name]?.Type == JTokenType.Null)
                    target[prop.Name] = prop.Value.DeepClone();
            }
        }

        private static void HandleDeviceChange(MainViewModel vm, JObject d, string label)
        {
            try
            {
                string deviceId = d["device_id"]?.ToString() ?? "";

                Dispatch(() =>
                {
                    switch (deviceId)
                    {
                        case "conveyor_agent_01":
                            vm.DeviceStatus.ConveyorStatus = label;
                            break;

                        case "robot_agent_01":
                            vm.DeviceStatus.RobotArmStatus = label;
                            break;

                        case "vision_agent_01":
                            vm.DeviceStatus.OcrCamStatus = label;
                            vm.DeviceStatus.QrCamStatus = label;
                            break;
                    }

                    vm.RefreshDeviceStatus();
                });
            }
            catch (Exception ex)
            {
                Log("device change", ex);
            }
        }

        private static bool TryGetToken(JObject d, out JToken? token, params string[] keys)
        {
            foreach (string key in keys)
            {
                token = d[key];

                if (token != null && token.Type != JTokenType.Null)
                    return true;
            }

            token = null;
            return false;
        }

        private static bool TryGetString(JObject d, out string value, params string[] keys)
        {
            value = "";

            if (!TryGetToken(d, out JToken? token, keys))
                return false;

            value = token?.ToString() ?? "";
            return true;
        }

        private static bool TryGetDouble(JObject d, out double value, params string[] keys)
        {
            value = 0;

            if (!TryGetToken(d, out JToken? token, keys))
                return false;

            if (token == null)
                return false;

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<double>();
                return true;
            }

            string text = token.ToString();

            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return true;

            if (double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
                return true;

            return false;
        }

        private static bool TryGetInt(JObject d, out int value, params string[] keys)
        {
            value = 0;

            if (!TryGetToken(d, out JToken? token, keys))
                return false;

            if (token == null)
                return false;

            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }

            string text = token.ToString();

            if (int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return true;

            if (int.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
                return true;

            return false;
        }

        private static bool TryGetBool(JObject d, out bool value, params string[] keys)
        {
            value = false;

            if (!TryGetToken(d, out JToken? token, keys))
                return false;

            if (token == null)
                return false;

            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }

            string text = token.ToString();

            if (bool.TryParse(text, out value))
                return true;

            if (text == "1")
            {
                value = true;
                return true;
            }

            if (text == "0")
            {
                value = false;
                return true;
            }

            return false;
        }

        private static string GetString(JObject d, string defaultValue, params string[] keys)
        {
            return TryGetString(d, out string value, keys) ? value : defaultValue;
        }

        private static double GetDouble(JObject d, double defaultValue, params string[] keys)
        {
            return TryGetDouble(d, out double value, keys) ? value : defaultValue;
        }

        private static int GetInt(JObject d, int defaultValue, params string[] keys)
        {
            return TryGetInt(d, out int value, keys) ? value : defaultValue;
        }

        private static bool GetBool(JObject d, bool defaultValue, params string[] keys)
        {
            return TryGetBool(d, out bool value, keys) ? value : defaultValue;
        }

        private static readonly JsonSerializerSettings _jsonSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTime,
        };

        private static T? Deserialize<T>(string json)
            => JsonConvert.DeserializeObject<T>(json, _jsonSettings);

        private static void Dispatch(Action action)
            => Application.Current?.Dispatcher.Invoke(action);

        private static void Log(string tag, Exception ex)
            => Console.WriteLine($"[ApiService:{tag}] {ex.Message}");

        public async Task DisconnectAsync()
        {
            await _socket.DisconnectAsync();
            _http.Dispose();
        }
    }
}
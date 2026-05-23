using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace MasterAdmin
{
    public class MqttService : IDisposable
    {
        private IMqttClient? _client;
        private readonly string _broker;
        private const int PORT = 1883;
        private int _frameSkip = 0;
        private bool _disposed = false;

        private const string ROBOT_COMMAND_TOPIC = "parcel/robot/command";
        private const string ROBOT_STATUS_TOPIC = "parcel/robot/status";
        private const string ROBOT_RESULT_TOPIC = "parcel/robot/result";
        private const string ROBOT_HEARTBEAT_TOPIC = "parcel/robot/heartbeat";

        public event Action<JObject>? RobotStatusReceived;
        public event Action<JObject>? RobotResultReceived;
        public event Action<JObject>? RobotHeartbeatReceived;
        public event Action<string, JObject>? RobotMessageReceived;
        public event Action<string>? LogReceived;

        public MqttService(string broker)
        {
            _broker = broker;
        }

        public async Task ConnectAsync()
        {
            if (_client != null && _client.IsConnected)
                return;

            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            _client.ApplicationMessageReceivedAsync += e =>
            {
                HandleReceivedMessage(e);
                return Task.CompletedTask;
            };

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_broker, PORT)
                .WithClientId("wpf_master_admin_" + Environment.MachineName + "_" + Guid.NewGuid().ToString("N")[..6])
                .WithCleanSession()
                .Build();

            await _client.ConnectAsync(options, CancellationToken.None);

            await SubscribeRobotTopicsAsync();

            Log("[MQTT] 연결 성공");
        }

        public bool IsConnected => _client?.IsConnected ?? false;

        private async Task SubscribeRobotTopicsAsync()
        {
            if (_client == null || !_client.IsConnected)
                return;

            await _client.SubscribeAsync(ROBOT_STATUS_TOPIC);
            await _client.SubscribeAsync(ROBOT_RESULT_TOPIC);
            await _client.SubscribeAsync(ROBOT_HEARTBEAT_TOPIC);

            Log("[MQTT] 로봇 토픽 구독 완료");
            Log("[MQTT] 구독: " + ROBOT_STATUS_TOPIC);
            Log("[MQTT] 구독: " + ROBOT_RESULT_TOPIC);
            Log("[MQTT] 구독: " + ROBOT_HEARTBEAT_TOPIC);
        }

        private void HandleReceivedMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

                if (string.IsNullOrWhiteSpace(payload))
                    return;

                JObject data;

                try
                {
                    data = JObject.Parse(payload);
                }
                catch
                {
                    data = new JObject
                    {
                        ["raw"] = payload
                    };
                }

                RobotMessageReceived?.Invoke(topic, data);

                if (topic == ROBOT_STATUS_TOPIC)
                {
                    RobotStatusReceived?.Invoke(data);
                }
                else if (topic == ROBOT_RESULT_TOPIC)
                {
                    RobotResultReceived?.Invoke(data);
                }
                else if (topic == ROBOT_HEARTBEAT_TOPIC)
                {
                    RobotHeartbeatReceived?.Invoke(data);
                }

                Log("[MQTT 수신] " + topic + " / " + data.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                Log("[MQTT 수신 오류] " + ex.Message);
            }
        }

        public void PublishFrame(Mat frame, string cameraId)
        {
            if (_client == null || !_client.IsConnected)
                return;

            _frameSkip++;

            if (_frameSkip % 3 != 0)
                return;

            try
            {
                var buf = frame.ImEncode(".jpg", new ImageEncodingParam(
                    ImwriteFlags.JpegQuality, 50));

                string b64 = Convert.ToBase64String(buf);

                var payload = JsonConvert.SerializeObject(new
                {
                    frame = b64,
                    camera_id = cameraId,
                    machine = Environment.MachineName
                });

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic("parcel/blackbox/frame/" + cameraId)
                    .WithPayload(Encoding.UTF8.GetBytes(payload))
                    .WithQualityOfServiceLevel(
                        MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                    .Build();

                _ = _client.PublishAsync(message);
            }
            catch (Exception ex)
            {
                Log("[MQTT] 프레임 전송 오류: " + ex.Message);
            }
        }

        private async Task<bool> PublishJsonAsync(string topic, JObject payload)
        {
            if (_client == null || !_client.IsConnected)
            {
                Log("[MQTT] 전송 실패: MQTT 연결 안 됨");
                return false;
            }

            try
            {
                string json = payload.ToString(Formatting.None);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(json))
                    .WithQualityOfServiceLevel(
                        MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                    .Build();

                await _client.PublishAsync(message);

                Log("[MQTT 발행] " + topic + " / " + json);
                return true;
            }
            catch (Exception ex)
            {
                Log("[MQTT] 전송 오류: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> SendRobotCommandAsync(string command, JObject? extra = null)
        {
            var payload = new JObject
            {
                ["command"] = command,
                ["command_id"] = Guid.NewGuid().ToString("N"),
                ["source"] = "WPF",
                ["machine"] = Environment.MachineName,
                ["timestamp"] = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            if (extra != null)
            {
                foreach (var prop in extra.Properties())
                {
                    payload[prop.Name] = prop.Value;
                }
            }

            return await PublishJsonAsync(ROBOT_COMMAND_TOPIC, payload);
        }

        public Task<bool> RobotHomeAsync()
        {
            return SendRobotCommandAsync("HOME");
        }

        public Task<bool> RobotStatusAsync()
        {
            return SendRobotCommandAsync("STATUS");
        }

        public Task<bool> RobotStopAsync()
        {
            return SendRobotCommandAsync("STOP");
        }

        public Task<bool> RobotEmergencyStopAsync()
        {
            return SendRobotCommandAsync("EMERGENCY_STOP");
        }

        public Task<bool> RobotResetEmergencyAsync()
        {
            return SendRobotCommandAsync("RESET_EMERGENCY");
        }

        public Task<bool> RobotOpenGripperAsync()
        {
            return SendRobotCommandAsync("OPEN_GRIPPER");
        }

        public Task<bool> RobotCloseGripperAsync()
        {
            return SendRobotCommandAsync("CLOSE_GRIPPER");
        }

        public Task<bool> RobotGetPoseAsync()
        {
            return SendRobotCommandAsync("GET_POSE");
        }

        public Task<bool> RobotPickReadyAsync()
        {
            return SendRobotCommandAsync("PICK_READY");
        }

        public Task<bool> RobotCameraStatusAsync()
        {
            return SendRobotCommandAsync("CAMERA_STATUS");
        }

        public Task<bool> RobotStartCameraAsync()
        {
            return SendRobotCommandAsync("START_CAMERA");
        }

        public Task<bool> RobotPlaceTestAsync(int slot)
        {
            return SendRobotCommandAsync("PLACE_TEST", new JObject
            {
                ["slot"] = slot
            });
        }

        public Task<bool> RobotSortAsync(
            int slot,
            string packageId = "WPF_TEST",
            string sortCode = "SEOUL_BOX",
            double offsetX = 0,
            double offsetY = 0,
            double offsetZ = 0)
        {
            return SendRobotCommandAsync("SORT", new JObject
            {
                ["package_id"] = packageId,
                ["slot"] = slot,
                ["sort_code"] = sortCode,
                ["offset_x"] = offsetX,
                ["offset_y"] = offsetY,
                ["offset_z"] = offsetZ
            });
        }

        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            LogReceived?.Invoke(message);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _client?.DisconnectAsync().Wait(1000);
                _client?.Dispose();
            }
            catch
            {
            }
        }
    }
}
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using Newtonsoft.Json;
using OpenCvSharp;

namespace MasterAdmin
{
    public class MqttService : IDisposable
    {
        private IMqttClient? _client;
        private readonly string _broker;
        private const int PORT = 1883;
        private int _frameSkip = 0;

        public MqttService(string broker)
        {
            _broker = broker;
        }

        public async Task ConnectAsync()
        {
            var factory = new MqttFactory();
            _client     = factory.CreateMqttClient();

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_broker, PORT)
                .WithClientId("wpf_blackbox_" + Environment.MachineName)
                .WithCleanSession()
                .Build();

            await _client.ConnectAsync(options, CancellationToken.None);
            System.Diagnostics.Debug.WriteLine("[MQTT] 연결 성공");
        }

        public bool IsConnected => _client?.IsConnected ?? false;

        // 카메라 프레임을 MQTT로 전송
        public void PublishFrame(Mat frame, string cameraId)
        {
            if (_client == null || !_client.IsConnected) return;

            // 3프레임마다 1번 전송 (부하 감소)
            _frameSkip++;
            if (_frameSkip % 3 != 0) return;

            try
            {
                var buf = frame.ImEncode(".jpg", new ImageEncodingParam(
                    ImwriteFlags.JpegQuality, 50));
                string b64 = Convert.ToBase64String(buf);

                var payload = JsonConvert.SerializeObject(new
                {
                    frame     = b64,
                    camera_id = cameraId,
                    machine   = Environment.MachineName
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
                System.Diagnostics.Debug.WriteLine("[MQTT] 전송 오류: " + ex.Message);
            }
        }

        public void Dispose()
        {
            try
            {
                _client?.DisconnectAsync().Wait(1000);
                _client?.Dispose();
            }
            catch { }
        }
    }
}

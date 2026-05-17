using System;
using System.IO.Ports;
using System.Diagnostics;
using System.Windows;

namespace MasterAdmin
{
    public class PtzHelper : IDisposable
    {
        private SerialPort? _port;
        private bool _disposed;

        public bool IsConnected => _port?.IsOpen ?? false;
        public int PanAngle  { get; private set; } = 90;
        public int TiltAngle { get; private set; } = 90;

        public bool Connect(string portName, int baudRate = 9600)
        {
            try
            {
                _port = new SerialPort(portName, baudRate)
                {
                    ReadTimeout  = 500,
                    WriteTimeout = 500
                };
                _port.Open();
                Debug.WriteLine($"[PTZ] 연결 성공: {portName}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PTZ] 연결 실패: {ex.Message}");
                MessageBox.Show($"PTZ 연결 실패: {ex.Message}\n포트: {portName}", "오류");
                return false;
            }
        }

        public void Disconnect()
        {
            _port?.Close();
            Debug.WriteLine("[PTZ] 연결 해제");
        }

        public void SetPan(int angle)
        {
            angle = Math.Clamp(angle, 0, 180);
            PanAngle = angle;
            string cmd = $"P{angle:D3}";
            Debug.WriteLine($"[PTZ] 전송: {cmd}");
            Send(cmd);
        }

        public void SetTilt(int angle)
        {
            angle = Math.Clamp(angle, 0, 180);
            TiltAngle = angle;
            string cmd = $"T{angle:D3}";
            Debug.WriteLine($"[PTZ] 전송: {cmd}");
            Send(cmd);
        }

        public void Reset()
        {
            SetPan(90);
            SetTilt(90);
        }

        /// <summary>자동 스윙 시작 — Arduino에 'A' 전송</summary>
        public void StartAutoSwing()
        {
            if (_port == null || !_port.IsOpen) return;
            try
            {
                _port.Write("A");
                Debug.WriteLine("[PTZ] 자동 스윙 시작");
            }
            catch (Exception ex) { Debug.WriteLine($"[PTZ] 오류: {ex.Message}"); }
        }

        /// <summary>자동 스윙 정지 — Arduino에 'S' 전송</summary>
        public void StopAutoSwing()
        {
            if (_port == null || !_port.IsOpen) return;
            try
            {
                _port.Write("S");
                Debug.WriteLine("[PTZ] 자동 스윙 정지");
            }
            catch (Exception ex) { Debug.WriteLine($"[PTZ] 오류: {ex.Message}"); }
        }

        private void Send(string cmd)
        {
            if (_port == null || !_port.IsOpen)
            {
                Debug.WriteLine("[PTZ] 전송 실패: 포트 닫혀있음");
                return;
            }
            try
            {
                _port.Write(cmd);
                Debug.WriteLine($"[PTZ] 전송 완료: {cmd}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PTZ] 전송 오류: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Disconnect();
            _port?.Dispose();
            _disposed = true;
        }
    }
}

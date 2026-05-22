using System;
using System.Windows;

namespace MasterAdmin
{
    public partial class DebugWindow : Window
    {
        private static DebugWindow? _instance;

        public static DebugWindow Instance
        {
            get
            {
                if (_instance == null || !_instance.IsLoaded)
                    _instance = new DebugWindow();
                return _instance;
            }
        }

        public DebugWindow()
        {
            InitializeComponent();
        }

        public void SetConnected(bool connected)
        {
            Dispatcher.Invoke(() =>
            {
                TxtConnectionStatus.Text      = connected ? "✅ WebSocket 연결됨" : "❌ 연결 안됨";
                TxtConnectionStatus.Foreground = connected
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(74, 222, 128))
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(248, 113, 113));
            });
        }

        public void AddLog(string eventName, string data)
        {
            Dispatcher.Invoke(() =>
            {
                string time    = DateTime.Now.ToString("HH:mm:ss.fff");
                string color   = GetEventColor(eventName);
                string newLine = $"[{time}] [{eventName}] {data}\n";

                TxtLog.Text = newLine + TxtLog.Text;

                // 최대 200줄 유지
                var lines = TxtLog.Text.Split('\n');
                if (lines.Length > 200)
                    TxtLog.Text = string.Join("\n", lines[..200]);
            });
        }

        private static string GetEventColor(string eventName) => eventName switch
        {
            "device_status"       => "🟦",
            "physical_estop"      => "🔴",
            "estop_released"      => "🟢",
            "sorting_log_added"   => "🟡",
            "shipping_log_added"  => "🟣",
            "blackbox_event_added"=> "🟠",
            "emergency_stop"      => "🔴",
            "device_connected"    => "🟢",
            "device_disconnected" => "⚫",
            _                     => "⚪"
        };

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Text = "";
        }
    }
}

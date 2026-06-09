using System.Windows;
using System.Windows.Controls;

namespace MasterAdmin
{
    public partial class OverviewPage : UserControl
    {
        // 공장 전체 CCTV — 현우 노트북(192.168.0.39)의 cctv_stream.py가 송출하는 MJPEG 스트림.
        //   QR캠과 동일한 네트워크 URL 방식. 노트북/스크립트가 꺼지면 플레이스홀더가 표시된다.
        private CameraHelper? _camCctv;

        public OverviewPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 탭 전환으로 Loaded가 여러 번 불릴 수 있으므로 중복 생성 방지
            if (_camCctv != null) return;

            _camCctv = new CameraHelper("http://192.168.0.39:8083/stream/cctv", CamCctvImg, CamCctvPlaceholder);
            _camCctv.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _camCctv?.Dispose();
            _camCctv = null;
        }
    }
}

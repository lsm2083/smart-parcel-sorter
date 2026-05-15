using System.Windows;
using System.Windows.Controls;

namespace MasterAdmin
{
    public partial class OverviewPage : UserControl
    {
        private CameraHelper? _cam1;
        private CameraHelper? _cam2;

        public OverviewPage()
        {
            InitializeComponent();
            Loaded   += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 0 = 8Mega autofous webcamera (현장 CAM)
            // 1 = QHD Webcam              (출고 CAM)
            _cam1 = new CameraHelper(0, CamFieldImg,    CamFieldPlaceholder);
            _cam2 = new CameraHelper(1, CamShippingImg, CamShippingPlaceholder);
            _cam1.Start();
            _cam2.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cam1?.Dispose();
            _cam2?.Dispose();
        }
    }
}

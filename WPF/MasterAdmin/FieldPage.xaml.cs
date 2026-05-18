using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MasterAdmin
{
    public partial class FieldPage : UserControl
    {
        private CameraHelper? _cam1;
        private CameraHelper? _cam2;

        private string _sortFilter = "전체";
        private string _shipFilter = "전체";

        // (버튼, 기본 배경, 기본 글자색) 튜플
        private (Button btn, string bg, string fg)[]? _sortBtnDefs;
        private (Button btn, string bg, string fg)[]? _shipBtnDefs;

        public FieldPage()
        {
            InitializeComponent();
            Loaded   += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _cam1 = new CameraHelper(0, CamFieldImg,    CamFieldPlaceholder);
            _cam2 = new CameraHelper(1, CamShippingImg, CamShippingPlaceholder);
            _cam1.Start();
            _cam2.Start();

            // 버튼별 고유 색상 정의 (배경, 글자)
            _sortBtnDefs = new[]
            {
                (BtnSortAll,  "#1E40AF", "#BFDBFE"),
                (BtnSortQR,   "#115E59", "#99F6E4"),
                (BtnSortOCR,  "#9A3412", "#FDBA74"),
                (BtnSortJam,  "#78350F", "#FDE68A"),
            };
            _shipBtnDefs = new[]
            {
                (BtnShipAll,  "#4C1D95", "#DDD6FE"),
                (BtnShipFail, "#991B1B", "#FECACA"),
            };

            RefreshBtnStyles(_sortBtnDefs, BtnSortAll);
            RefreshBtnStyles(_shipBtnDefs, BtnShipAll);

            if (DataContext is MainViewModel vm)
            {
                vm.SortingLogs.CollectionChanged  += (_, _) => ApplySortFilter();
                vm.ShippingLogs.CollectionChanged += (_, _) => ApplyShipFilter();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cam1?.Dispose();
            _cam2?.Dispose();
        }

        private void BtnSortFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            _sortFilter = btn.Tag?.ToString() ?? "전체";
            RefreshBtnStyles(_sortBtnDefs!, btn);
            ApplySortFilter();
        }

        private void BtnShipFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            _shipFilter = btn.Tag?.ToString() ?? "전체";
            RefreshBtnStyles(_shipBtnDefs!, btn);
            ApplyShipFilter();
        }

        private void ApplySortFilter()
        {
            if (DataContext is not MainViewModel vm) return;
            var filtered = _sortFilter switch
            {
                "QR인식실패"  => vm.SortingLogs.Where(l => l.RecognitionType == "QR"  && l.Status == "불량"),
                "OCR인식실패" => vm.SortingLogs.Where(l => l.RecognitionType == "OCR" && l.Status == "불량"),
                "박스걸림"    => vm.SortingLogs.Where(l => l.ErrorType == "박스걸림"),
                _             => vm.SortingLogs.AsEnumerable()
            };
            SortingGrid.ItemsSource = new ObservableCollection<SortingLog>(filtered);
        }

        private void ApplyShipFilter()
        {
            if (DataContext is not MainViewModel vm) return;
            var filtered = _shipFilter switch
            {
                "분류실패" => vm.ShippingLogs.Where(l => l.Status == "불량" || l.Status == "분류실패"),
                _          => vm.ShippingLogs.AsEnumerable()
            };
            ShippingGrid.ItemsSource = new ObservableCollection<ShippingLog>(filtered);
        }

        // 활성: 원래 색상 / 비활성: 고정 어두운 회색 (테마 무관)
        private static void RefreshBtnStyles(
            (Button btn, string bg, string fg)[] defs, Button active)
        {
            var inactiveBg = new SolidColorBrush(Color.FromRgb(45, 55, 72));   // 고정 어두운 회색
            var inactiveFg = new SolidColorBrush(Color.FromRgb(113, 128, 150)); // 고정 중간 회색

            foreach (var (btn, bg, fg) in defs)
            {
                bool isActive = btn == active;
                if (isActive)
                {
                    btn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
                    btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
                    btn.FontWeight = FontWeights.Bold;
                }
                else
                {
                    btn.Background = inactiveBg;
                    btn.Foreground = inactiveFg;
                    btn.FontWeight = FontWeights.Normal;
                }
            }
        }
    }
}

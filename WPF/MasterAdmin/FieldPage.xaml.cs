using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using Window = System.Windows.Window;

namespace MasterAdmin
{
    public partial class FieldPage : UserControl
    {
        private CameraHelper? _cam1;
        private CameraHelper? _cam2;

        private string _sortFilter = "전체";
        private string _shipFilter = "전체";

        private (Button btn, string bg, string fg)[]? _sortBtnDefs;
        private (Button btn, string bg, string fg)[]? _shipBtnDefs;

        private SerialPort? _irPort;
        private string _irBuffer = "";

        private const string IR_PORT = "COM5";
        private const int IR_BAUD = 115200;
        private const string SAVE_FOLDER = "blackbox\\jam\\";

        // 영상 녹화 관련
        private VideoCapture? _recorder;
        private readonly Queue<Mat> _frameBuffer = new();
        private readonly object _frameLock = new();
        private CancellationTokenSource? _recordCts;
        private const int BUFFER_SECONDS = 5;  // 오류 전 5초 보관
        private const int FPS = 15;

        public FieldPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _cam1 = new CameraHelper(0, CamFieldImg, CamFieldPlaceholder);
            _cam2 = new CameraHelper(1, CamShippingImg, CamShippingPlaceholder);
            _cam1.Start();
            _cam2.Start();

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
                vm.SortingLogs.CollectionChanged += (_, _) => ApplySortFilter();
                vm.ShippingLogs.CollectionChanged += (_, _) => ApplyShipFilter();

                // QR/OCR 실패 시 녹화 트리거 등록
                vm.TriggerRecording = reason =>
                {
                    string? videoPath = RecordJamVideo(reason);
                    // 가장 최근 로그에 영상 경로 업데이트
                    if (videoPath != null && vm.SortingLogs.Count > 0)
                        vm.SortingLogs[0].ImagePath = videoPath;
                };
            }

            ConnectIrSensor();
            StartFrameBuffer();  // 상시 프레임 버퍼링 시작
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cam1?.Dispose();
            _cam2?.Dispose();
            DisconnectIrSensor();
            StopFrameBuffer();
        }

        // ── 상시 프레임 버퍼링 (오류 전 5초 보관) ─────────────────────
        private void StartFrameBuffer()
        {
            // cam1 프레임을 버퍼에 저장
            _cam1!.OnFrame = frame =>
            {
                lock (_frameLock)
                {
                    _frameBuffer.Enqueue(frame);
                    int maxFrames = BUFFER_SECONDS * FPS;
                    while (_frameBuffer.Count > maxFrames)
                    {
                        var old = _frameBuffer.Dequeue();
                        old.Dispose();
                    }
                }
            };
        }

        private void StopFrameBuffer()
        {
            if (_cam1 != null)
                _cam1.OnFrame = null;

            lock (_frameLock)
            {
                while (_frameBuffer.Count > 0)
                    _frameBuffer.Dequeue().Dispose();
            }
        }

        // ── 오류 시점 영상 저장 (전 5초 + 후 5초) ─────────────────────
        private string? RecordJamVideo(string reason)
        {
            try
            {
                Directory.CreateDirectory(SAVE_FOLDER);
                string fileName = reason + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".avi";
                string fullPath = Path.Combine(SAVE_FOLDER, fileName);

                // 버퍼된 프레임 복사 (오류 전 5초)
                List<Mat> buffered;
                lock (_frameLock)
                {
                    buffered = _frameBuffer.ToList();
                }

                var writer = new VideoWriter(
                    fullPath,
                    FourCC.XVID,
                    FPS,
                    new OpenCvSharp.Size(640, 480));

                foreach (var f in buffered)
                    writer.Write(f);

                // 오류 후 5초 — cam1 OnFrame 으로 추가 녹화
                int afterFrames = BUFFER_SECONDS * FPS;
                int recordedAfter = 0;

                Action<Mat>? afterRecord = null;
                afterRecord = frame =>
                {
                    if (recordedAfter >= afterFrames)
                    {
                        if (_cam1 != null)
                            _cam1.OnFrame = null;
                        writer.Release();
                        System.Diagnostics.Debug.WriteLine("[녹화] 완료: " + fullPath);

                        // 녹화 완료 후 다시 버퍼링 재시작
                        StartFrameBuffer();
                        return;
                    }
                    writer.Write(frame);
                    recordedAfter++;
                };

                // 기존 버퍼링 콜백 교체 → 녹화 콜백으로
                if (_cam1 != null)
                    _cam1.OnFrame = afterRecord;

                return fullPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[녹화] 실패: " + ex.Message);
                return null;
            }
        }

        // ── 영상 보기 버튼 ─────────────────────────────────────────────
        private void BtnViewImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string? path = btn.Tag?.ToString();

            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show("영상 경로가 없습니다.", "알림");
                return;
            }

            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

            if (!File.Exists(path))
            {
                MessageBox.Show("영상 파일을 찾을 수 없습니다.\n경로: " + path +
                                "\n\n녹화 중일 수 있습니다. 5초 후 다시 시도하세요.", "알림");
                return;
            }

            // 영상 재생 창
            var win = new System.Windows.Window
            {
                Title = "블랙박스 녹화 — " + Path.GetFileName(path),
                Width = 720,
                Height = 460,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(10, 20, 35))
            };

            var media = new MediaElement
            {
                Source = new Uri(path, UriKind.Absolute),
                LoadedBehavior = MediaState.Manual,   // ← Manual로 변경
                UnloadedBehavior = MediaState.Manual,   // ← Manual로 변경
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8)
            };

            win.Content = media;
            win.Loaded += (_, _) => media.Play();     // ← Loaded 후 Play 호출
            win.Closed += (_, _) => media.Stop();
            win.Show();
        }

        // ── 적외선 센서 ───────────────────────────────────────────────
        private void ConnectIrSensor()
        {
            try
            {
                _irPort = new SerialPort(IR_PORT, IR_BAUD)
                {
                    ReadTimeout = 2000,
                    WriteTimeout = 500
                };
                _irPort.DataReceived += OnIrDataReceived;
                _irPort.Open();
                System.Diagnostics.Debug.WriteLine($"[IR] {IR_PORT} 연결 성공");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[IR] 연결 실패: " + ex.Message);
            }
        }

        private void DisconnectIrSensor()
        {
            try
            {
                if (_irPort != null)
                {
                    _irPort.DataReceived -= OnIrDataReceived;
                    if (_irPort.IsOpen) _irPort.Close();
                    _irPort.Dispose();
                    _irPort = null;
                }
            }
            catch { }
        }

        private void OnIrDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                _irBuffer += _irPort!.ReadExisting();

                while (_irBuffer.Contains('\n'))
                {
                    int idx = _irBuffer.IndexOf('\n');
                    string line = _irBuffer.Substring(0, idx)
                                  .Replace("\r", "").Trim();
                    _irBuffer = _irBuffer.Substring(idx + 1);

                    System.Diagnostics.Debug.WriteLine("[IR] 수신: " + line);

                    if (line == "BOX_PASS")
                        Dispatcher.Invoke(AddPassEvent);
                    else if (line == "BOX_JAM_STUCK")
                        Dispatcher.Invoke(() => AddJamEvent("박스걸림", "BOX_JAM_STUCK"));
                    else if (line == "BOX_JAM_NODETECT")
                        Dispatcher.Invoke(() => AddJamEvent("인지안됨", "BOX_JAM_NODETECT"));
                    else if (line == "BOX_JAM")
                        Dispatcher.Invoke(() => AddJamEvent("박스걸림", "BOX_JAM_STUCK"));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[IR] 수신 오류: " + ex.Message);
            }
        }

        private void AddPassEvent()
        {
            if (DataContext is not MainViewModel vm) return;

            vm.SortingLogs.Insert(0, new SortingLog
            {
                Id = vm.SortingLogs.Count + 1,
                Timestamp = DateTime.Now,
                TrackingNumber = "BOX-" + DateTime.Now.ToString("HHmmss"),
                RecognitionType = "IR",
                Region = "-",
                Status = "정상",
                ErrorType = "-",
                ProcessingTime = 0,
                Confidence = 100,
                ImagePath = ""
            });

            vm.DeviceStatus.TodaySortedCount++;
            vm.RefreshDeviceStatus();
        }

        private void AddJamEvent(string errorType, string reason)
        {
            if (DataContext is not MainViewModel vm) return;

            // 영상 녹화 시작 (전 5초 + 후 5초)
            string? videoPath = RecordJamVideo(errorType);

            vm.SortingLogs.Insert(0, new SortingLog
            {
                Id = vm.SortingLogs.Count + 1,
                Timestamp = DateTime.Now,
                TrackingNumber = "-",
                RecognitionType = "-",
                Region = "-",
                Status = "불량",
                ErrorType = errorType,
                ProcessingTime = 0,
                Confidence = 0,
                ImagePath = videoPath ?? ""
            });

            vm.BlackboxEvents.Insert(0, new BlackboxEvent
            {
                Id = vm.BlackboxEvents.Count + 1,
                Timestamp = DateTime.Now,
                EventType = BlackboxEventType.Jam,
                Description = errorType + " 감지 (" + DateTime.Now.ToString("HH:mm:ss") + ")",
                ImagePath = videoPath ?? "",
                SaveFolder = "blackbox\\jam\\",
                Severity = "오류"
            });

            vm.DeviceStatus.TodayErrorCount++;
            vm.RefreshDeviceStatus();
        }

        // ── 필터 ──────────────────────────────────────────────────────
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
            IEnumerable<SortingLog> filtered;
            if (_sortFilter == "QR인식실패")
                filtered = vm.SortingLogs.Where(l => l.RecognitionType == "QR" && l.Status == "불량");
            else if (_sortFilter == "OCR인식실패")
                filtered = vm.SortingLogs.Where(l => l.RecognitionType == "OCR" && l.Status == "불량");
            else if (_sortFilter == "박스걸림")
                filtered = vm.SortingLogs.Where(l => l.ErrorType == "박스걸림" || l.ErrorType == "인지안됨");
            else
                filtered = vm.SortingLogs;

            SortingGrid.ItemsSource = new ObservableCollection<SortingLog>(filtered);
        }

        private void ApplyShipFilter()
        {
            if (DataContext is not MainViewModel vm) return;
            IEnumerable<ShippingLog> filtered;
            if (_shipFilter == "분류실패")
                filtered = vm.ShippingLogs.Where(l => l.Status == "불량" || l.Status == "분류실패");
            else
                filtered = vm.ShippingLogs;

            ShippingGrid.ItemsSource = new ObservableCollection<ShippingLog>(filtered);
        }

        private static void RefreshBtnStyles(
            (Button btn, string bg, string fg)[] defs, Button active)
        {
            var inactiveBg = new SolidColorBrush(Color.FromRgb(45, 55, 72));
            var inactiveFg = new SolidColorBrush(Color.FromRgb(113, 128, 150));

            foreach (var (btn, bg, fg) in defs)
            {
                bool isActive = btn == active;
                if (isActive)
                {
                    btn.Background = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(bg));
                    btn.Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(fg));
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
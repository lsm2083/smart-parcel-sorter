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
using System.Windows.Threading;
using OpenCvSharp;
using Window = System.Windows.Window;

namespace MasterAdmin
{
    public partial class FieldPage : UserControl
    {
        private CameraHelper? _cam1;
        private CameraHelper? _cam2;
        private CameraHelper? _camQr;

        private string _sortFilter = "전체";
        private DateTime? _selectedDate = null;

        private (Button btn, string bg, string fg)[]? _sortBtnDefs;

        private SerialPort? _irPort;
        private string _irBuffer = "";

        private const string IR_PORT = "COM5";
        private const int IR_BAUD = 115200;
        private const string SAVE_FOLDER = "blackbox\\jam\\";

        private VideoCapture? _recorder;
        private readonly Queue<Mat> _frameBuffer = new();
        private readonly object _frameLock = new();
        private CancellationTokenSource? _recordCts;
        private const int BUFFER_SECONDS = 5;
        private const int FPS = 15;

        // ── YOLO 서비스 ───────────────────────────────────────────────────
        private YoloDetectionService? _yolo;

        // ── 출고캠 실제 해상도 (학습 해상도와 동일하게 맞춤) ─────────────
        private const int CAM_W = 1280;
        private const int CAM_H = 720;
        private const int ROI_MARGIN = 10;

        // ── 출고캠 박스 인지 ROI(빨간색) — 프레임 비율 0~1 ───────────────
        //   마우스 드래그로 그리면 갱신되고 roi_config.txt에 저장됨(재시작 유지).
        //   기본값: x 24%, y 40%, W 48%, H 34%
        private double _roiXFrac = 0.24;
        private double _roiYFrac = 0.40;
        private double _roiWFrac = 0.48;
        private double _roiHFrac = 0.34;

        // ── 현장캠 ROI (출고캠과 별도) ───────────────────────────────────
        private double _fieldRoiXFrac = 0.24;
        private double _fieldRoiYFrac = 0.40;
        private double _fieldRoiWFrac = 0.48;
        private double _fieldRoiHFrac = 0.34;

        private static readonly string RoiConfigPath =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "roi_config.txt");

        // ── ROI 드래그 그리기 상태 (출고/현장 각각) ──────────────────────
        private bool _drawingRoi = false;
        private System.Windows.Point _roiDrawStart;
        private bool _drawingFieldRoi = false;
        private System.Windows.Point _fieldRoiDrawStart;

        private bool _roi1Initialized = false;
        private int _boxCount = 0;

        // ── 캠별 그리기 상태 (ROI 크롭 좌표 — 그릴 때 ROI 원점 더함) ───────
        private sealed class CamDetState
        {
            public int[]? BoxBbox;          // 박스 bbox [x1,y1,x2,y2]
            public int[][]? BoxPoly;        // 박스 폴리곤(있으면)
            public double BoxConf;          // 박스/비닐 인지 신뢰도(0~1)
            public DateTime BoxTime = DateTime.MinValue;
            public DefectResult? DefectResult;   // 불량 그리기용
            public DateTime DefectTime = DateTime.MinValue;
        }
        private readonly CamDetState _shipState = new();   // cam2 출고(앞면)
        private readonly CamDetState _fieldState = new();  // cam1 현장(뒷면)
        private const double BOX_SHOW_SECS = 0.8;
        private const double DEFECT_SHOW_SECS = 3.0;

        // ── 비닐 채택 임계 인식률 ────────────────────────────────────────
        //   이 값 미만의 vinyl 검출은 false positive로 보고 무시(비닐로 안 침).
        //   비닐 오검출이 계속되면 값을 올리고, 진짜 비닐이 안 잡히면 낮추면 됨.
        private const double VINYL_CONF_THRESHOLD = 0.70;

        // ── 불량 채택 임계 인식률 ────────────────────────────────────────
        //   이 값 미만의 paper_crack/paper_gap 검출은 false positive로 보고 무시(불량으로 안 침).
        private const double DEFECT_CONF_THRESHOLD = 0.70;

        // ── 정상(박스) 채택 임계 인식률 ──────────────────────────────────
        //   이 값 미만의 paper(정상 박스) 검출은 채택/표시 안 함 (ROI 화면에도 70% 이상만 노출).
        private const double NORMAL_CONF_THRESHOLD = 0.70;

        // 공유 패스: 두 캠 중 하나라도 마지막으로 박스를 본 시각 (이탈 판단용)
        private DateTime _lastSeenTime = DateTime.MinValue;

        // ── 컨투어 프레임 스킵 (미사용 — YOLO paper 클래스로 대체)
        private int _contourFrameSkip = 0;
        private const int CONTOUR_EVERY_N = 4;

        // ── 현재 운송장번호 ───────────────────────────────────────────────
        private string _currentTrackingNumber = "";

        // ── 박스 ROI 진입/이탈 상태 추적 ────────────────────────────────
        // 매 프레임 중복 로그 방지: 박스가 ROI에 '새로 진입'한 순간 1회만 기록
        private bool _boxInRoi = false;   // 현재 박스가 ROI 안에 있는지
        // 이번 패스(박스 1대) 동안 본 것 누적 — 이탈 시 한 번에 최종 판정
        private bool _passSawBox = false;     // paper(정상 박스) 본 적 있음
        private bool _passSawVinyl = false;   // vinyl(비닐) 본 적 있음
        private bool _passSawDefect = false;  // paper_crack/paper_gap(불량) 본 적 있음
        private string _passDefectClass = ""; // 불량 클래스명
        private double _passDefectConf = 0;   // 불량 신뢰도
        private bool _passDefectCaptureStarted = false; // 이번 패스 검출프레임 캡처 시작 여부
        private byte[]? _passDefectImage = null;         // 검출 순간 잡아둔 프레임(이탈 시 최종판정에 따라 저장/폐기)
        private string _passDefectCam = "";              // 불량을 본 캠("field"/"shipping")
        private SortingLog? _currentBoxLog = null; // 이번 패스의 박스 단독 로그(택배상태 탭)
        private SortingLog? _passAnchorRow = null; // 이번 패스의 전체 탭 앵커 행(QR/OCR 오면 합쳐짐)
        private string _boxLogTrackNo = "";      // 이번 박스의 로그 ID

        private DateTime _boxExitTime = DateTime.MinValue; // ROI 이탈 시각
        // n초 연속 미감지 → 이탈 확정. 짧으면 한 박스가 여러 패스로 쪼개져 로그 중복 → 넉넉히.
        private const double BOX_EXIT_CONFIRM_SECS = 4.0;

        // ── 박스 이탈 감지 전용 타이머 (YOLO 콜백과 독립) ───────────────
        // YOLO 서버가 꺼져 있거나 박스 없을 때도 이탈을 확정할 수 있게 함
        private DispatcherTimer? _boxExitTimer;

        public FieldPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // ── 현장/출고캠: 스트림 서버에서 받아온다 (방식 A) ───────────────
            //   USB 카메라는 한 프로그램만 점유 가능 → 카메라는 cam_streamserver.py가
            //   독점으로 잡고, WPF·안드로이드 앱·브라우저가 모두 같은 MJPEG 스트림을
            //   받아본다. WPF는 받은 원본 위에 기존 불량 오버레이를 그대로 그리고(기능 유지),
            //   YOLO 처리도 OnFrame 경로로 동일하게 동작한다.
            //   ※ cam_streamserver.py(192.168.0.40:8082)가 켜져 있어야 한다.
            //   ※ FlipHorizontally=true → USB 직접연결 때와 같은 좌우반전 유지
            //     (화면·저장된 ROI·오버레이 좌표가 종전과 동일하게 맞도록).
            string camHost = "192.168.0.40";
            _cam1 = new CameraHelper($"http://{camHost}:8082/stream/field",    CamFieldImg,    CamFieldPlaceholder)    { FlipHorizontally = true }; // 현장캠
            _cam2 = new CameraHelper($"http://{camHost}:8082/stream/shipping", CamShippingImg, CamShippingPlaceholder) { FlipHorizontally = true }; // 출고캠
            _camQr = new CameraHelper("http://192.168.0.21:8081/stream", CamQrImg, CamQrPlaceholder);

            _cam1.Start();
            _cam2.Start();
            _camQr.Start();

            // YOLO 초기화
            _yolo = new YoloDetectionService();
            // 모든 판정을 OnScanResult(매 추론마다 호출)에서 처리 → 누락/중복 없음
            _yolo.OnScanResult += OnYoloScanResult;
            _ = Task.Run(async () =>
            {
                bool alive = await _yolo.IsServerAliveAsync();
                System.Diagnostics.Debug.WriteLine(alive
                    ? "[YOLO] 서버 연결 성공 ✓"
                    : "[YOLO] 서버 꺼져 있음 — yolo_server.py 먼저 실행하세요");
            });

            _sortBtnDefs = new[]
            {
                (BtnSortAll,  "#1E40AF", "#BFDBFE"),
                (BtnSortQR,   "#115E59", "#99F6E4"),
                (BtnSortOCR,  "#9A3412", "#FDBA74"),
                (BtnSortBox,  "#5B21B6", "#DDD6FE"),
            };

            RefreshBtnStyles(_sortBtnDefs, BtnSortAll);

            // 날짜 필터 초기화 (오늘 날짜)
            LogDatePicker.SelectedDate = DateTime.Today;

            if (DataContext is MainViewModel vm)
            {
                // QR/OCR 인식될 때마다 현재 운송장번호 업데이트
                vm.SortingLogs.CollectionChanged += OnTrackingNumberUpdated;

                vm.TriggerRecording = reason =>
                {
                    // 잼/불량 순간 영상은 blackbox\jam\ 에 .avi로 보관만 한다.
                    //   ※ 이 경로를 로그 행 ImagePath에 넣지 않는다 — '이미지' 미리보기는
                    //     BitmapImage라 .avi를 못 열고(영상), 녹화 중이면 파일이 잠겨
                    //     "다른 프로세스가 사용 중" 오류가 났다. 행은 백엔드가 준 JPG를 유지.
                    RecordJamVideo(reason);
                };

                // ★ 최신 프레임 제공 — 불량 감지 시 multipart 이미지 첨부용
                vm.GetShippingFrame = () => _cam2?.GetLatestJpeg();   // cam2 출고캠
                vm.GetQrFrame = () => _camQr?.GetLatestJpeg();        // QR/OCR
                vm.GetFieldFrame = () => _cam1?.GetLatestJpeg();      // cam1 현장캠
            }

            ConnectIrSensor();
            StartFrameBuffer();
            ConnectCam2Yolo();
            StartBoxExitTimer();

            // 저장된 ROI 불러와 적용 (없으면 기본값)
            LoadRoiConfig();
            InitRoi1Display();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _boxExitTimer?.Stop();
            _cam1?.Dispose();
            _cam2?.Dispose();
            _camQr?.Dispose();
            _yolo?.Dispose();
            DisconnectIrSensor();
            StopFrameBuffer();
        }

        private void StartFrameBuffer()
        {
            _cam1!.OnFrame = frame =>
            {
                // 현장캠(뒷면) YOLO 분석 — 출고캠과 동일 ROI로 검출
                _yolo?.HandleFieldFrame(frame);

                lock (_frameLock)
                {
                    _frameBuffer.Enqueue(frame.Clone());
                    int maxFrames = BUFFER_SECONDS * FPS;
                    while (_frameBuffer.Count > maxFrames)
                    {
                        var old = _frameBuffer.Dequeue();
                        old.Dispose();
                    }
                }
            };

            // 현장캠 화면에도 ROI + 검출 오버레이 (현장 ROI 사용)
            _cam1!.OnDisplayFrame = frame =>
            {
                var roi = GetRoiRect(frame, _fieldRoiXFrac, _fieldRoiYFrac, _fieldRoiWFrac, _fieldRoiHFrac);
                DrawRoi1Border(frame, roi);
                DrawBoxOverlay(frame, _fieldState, roi);
                DrawDefectOverlay(frame, _fieldState, roi);
            };
        }


        private void ConnectCam2Yolo()
        {
            // OnFrame: YOLO가 paper(박스) + paper_crack/paper_gap(불량) 모두 감지
            _cam2!.OnFrame = frame =>
            {
                _yolo?.HandleShippingFrame(frame);
            };

            // OnDisplayFrame: 화면에 표시되기 직전 프레임에 직접 그리기 (출고 ROI 사용)
            _cam2!.OnDisplayFrame = frame =>
            {
                var roi = GetRoiRect(frame, _roiXFrac, _roiYFrac, _roiWFrac, _roiHFrac);
                DrawRoi1Border(frame, roi);            // 빨간 외곽 테두리 (항상)
                DrawBoxOverlay(frame, _shipState, roi);    // 박스 인지 + 판정/인지율
                DrawDefectOverlay(frame, _shipState, roi); // 초록: 불량 위치
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

        private string? RecordJamVideo(string reason)
        {
            try
            {
                Directory.CreateDirectory(SAVE_FOLDER);
                string fileName = reason + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".avi";
                string fullPath = Path.Combine(SAVE_FOLDER, fileName);

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
                        StartFrameBuffer();
                        return;
                    }
                    writer.Write(frame);
                    recordedAfter++;
                };

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

        private void BtnViewImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string? path = btn.Tag?.ToString();

            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show("이미지 경로가 없습니다.\n불량 감지 시 자동 저장됩니다.", "알림");
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;

                if (path.StartsWith("http://") || path.StartsWith("https://"))
                {
                    // URL 이미지 (Flask 서버에서 제공)
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                }
                else
                {
                    // 로컬 파일
                    if (!Path.IsPathRooted(path))
                        path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

                    if (!File.Exists(path))
                    {
                        MessageBox.Show($"이미지 파일을 찾을 수 없습니다.\n{path}", "알림");
                        return;
                    }
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                }

                bitmap.EndInit();
                PreviewImage.Source = bitmap;
                PreviewImage.Visibility = Visibility.Visible;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show("이미지 로드 오류: " + ex.Message, "오류");
            }
        }

        private void BtnClearPreview_Click(object sender, RoutedEventArgs e)
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }

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
                    // 박스걸림(BOX_JAM_*) 처리 제거 — 택배 상태(정상/불량)로 일원화
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
                ImagePath = "",
                IsLocal = true   // DB 새로고침 시 보존
            });

            vm.DeviceStatus.TodaySortedCount++;
            vm.RefreshDeviceStatus();
        }

        // ── YOLO 추론 결과 (매 추론마다 호출) → 패스 추적 ──────────────────
        //   한 박스(패스) 동안 본 것을 누적하고, 이탈 시 한 번에 최종 판정.
        //   paper → 박스(정상), paper_crack/paper_gap → 불량, vinyl → 비닐
        private void OnYoloScanResult(DefectResult result)
        {
            // 출고(앞면)·현장(뒷면) 두 캠 모두 처리 → 하나의 공유 패스로 합침
            CamDetState st;
            if (result.CamId == "shipping") st = _shipState;
            else if (result.CamId == "field") st = _fieldState;
            else return;

            bool sawBox = false, sawVinyl = false, sawDefect = false;
            string defectClass = ""; double defectConf = 0;
            int[]? boxBbox = null; int[][]? boxPoly = null; double recogConf = 0;

            // box_detections: paper(박스) / vinyl(비닐)
            foreach (var det in result.BoxDetections)
            {
                if (det.Bbox.Length < 4) continue;
                if (IsVinylClass(det.Class))
                {
                    if (det.Confidence < VINYL_CONF_THRESHOLD) continue;   // 낮은 신뢰도 비닐 오검출 무시
                    sawVinyl = true; boxBbox = det.Bbox; boxPoly = det.Polygon; recogConf = det.Confidence;
                }
                else
                {
                    if (det.Confidence < NORMAL_CONF_THRESHOLD) continue;   // 인식률 70% 미만 정상박스는 채택 안 함
                    sawBox = true; boxBbox = det.Bbox; boxPoly = det.Polygon; recogConf = det.Confidence;
                }
            }

            // defect_detections: paper_crack/paper_gap(불량), 방어적으로 vinyl도 처리
            foreach (var det in result.Detections)
            {
                if (det.Bbox.Length < 4) continue;
                if (IsVinylClass(det.Class)) { if (det.Confidence >= VINYL_CONF_THRESHOLD) sawVinyl = true; continue; }
                if (det.Confidence < DEFECT_CONF_THRESHOLD) continue;   // 인식률 70% 미만 불량은 채택 안 함
                sawDefect = true;
                if (defectClass == "") { defectClass = det.Class; defectConf = det.Confidence; }
            }

            // vinyl_detections: 비닐 (서버 별도 필드)
            foreach (var det in result.VinylDetections)
            {
                if (det.Bbox.Length < 4) continue;
                if (det.Confidence < VINYL_CONF_THRESHOLD) continue;   // 낮은 신뢰도 비닐 오검출 무시
                sawVinyl = true; boxBbox = det.Bbox; boxPoly = det.Polygon; recogConf = det.Confidence;
            }

            if (!sawBox && !sawVinyl && !sawDefect) return;

            var now = DateTime.Now;

            // ── 이 캠의 그리기 상태 갱신 ──────────────────────────────────
            if (boxBbox != null) { st.BoxBbox = boxBbox; st.BoxPoly = boxPoly; st.BoxConf = recogConf; st.BoxTime = now; }
            if (sawDefect) { st.DefectResult = result; st.DefectTime = now; }

            // ── 공유 패스: 두 캠 중 하나라도 보면 ROI에 있는 것 → 이탈 타이머 리셋 ─
            _lastSeenTime = now;
            _boxExitTime = DateTime.MinValue;

            // ── 새 패스 시작 (어느 캠이든 박스가 ROI에 새로 들어온 순간) ──────
            bool newPass = !_boxInRoi;
            if (newPass)
            {
                _boxInRoi = true;
                _passSawBox = false;
                _passSawVinyl = false;
                _passSawDefect = false;
                _passDefectClass = "";
                _passDefectConf = 0;
                _passDefectCaptureStarted = false;
                _passDefectImage = null;
                _passDefectCam = "";
                _currentBoxLog = null;   // 진입 시 행 1개 생성 후 같은 행만 갱신
                _passAnchorRow = null;   // 이번 패스의 전체 탭 앵커 행 초기화
                _boxLogTrackNo = string.IsNullOrWhiteSpace(_currentTrackingNumber)
                    ? "BOX-" + DateTime.Now.ToString("HHmmss")
                    : _currentTrackingNumber;
            }

            // ── 이번 패스 누적 ─────────────────────────────────────────────
            if (sawBox) _passSawBox = true;
            if (sawVinyl) _passSawVinyl = true;
            if (sawDefect)
            {
                _passSawDefect = true;
                if (_passDefectClass == "") { _passDefectClass = defectClass; _passDefectConf = defectConf; }
                _passDefectCam = result.CamId;   // 불량을 본 캠 기록 (검출 프레임 캡처용)
            }

            // 현재까지 최선 판정 (불량 > 정상 > 비닐). 같은 박스는 한 행에서 갱신만.
            string status = _passSawDefect ? "불량" : _passSawBox ? "정상" : "비닐";
            string err = _passSawDefect ? _passDefectClass : "";

            Dispatcher.Invoke(() =>
            {
                if (DataContext is not MainViewModel vm) return;
                if (newPass)
                {
                    vm.DeviceStatus.TodaySortedCount++;   // 박스 1대 카운트
                    vm.RefreshDeviceStatus();
                }
                SetBoxResult(vm, status, err);   // 진입 시 행 생성, 이후 같은 행 갱신

                // 불량을 처음 본 순간의 프레임(해당 캠, 초록 오버레이 포함)을 메모리에 잡아둔다.
                //   ※ 디스크 저장/행 부착은 이탈 시 최종판정이 '불량'으로 확정될 때만 수행.
                //     이탈 시점엔 박스가 ROI를 빠져나가 라이브 프레임이 비어 있으므로 미리 캡처한다.
                if (status == "불량" && !_passDefectCaptureStarted)
                    CaptureDefectFrame(vm, _passDefectCam);
            });
            // ※ Flask 전송/정리는 이탈 시 _boxExitTimer가 1회 수행
        }

        // ── 택배상태 로그 행 생성/갱신 ───────────────────────────────────
        //   운송장·인식·권역은 비워둠(택배 상태만 인지하므로)
        //   '검사중' 없이 정상/불량/비닐 결과가 확정될 때만 택배상태·최종판정 표시
        private void SetBoxResult(MainViewModel vm, string boxStatus, string errorType)
        {
            // 오류유형은 항상 현재 박스상태와 동기화 — 불량일 때만 표시, 정상/비닐이면 비움.
            //   (이전 프레임에 잠깐 본 불량클래스가 정상 행에 남는 것 방지)
            string boxErr = (boxStatus == "불량") ? errorType : "";

            // ── 1) 박스 단독 로그 (택배상태 탭 — 로그대로 유지) ──────────────
            //   운송장/인식/권역 비움, 택배상태=최종판정=박스상태(정상/불량/비닐).
            //   전체 탭에선 필터(isBoxOnly)로 숨겨진다.
            if (_currentBoxLog == null)
                _currentBoxLog = CreateStandaloneBoxRow(vm);
            if (_currentBoxLog.BoxStatus != boxStatus) _currentBoxLog.BoxStatus = boxStatus;
            if (_currentBoxLog.FinalResult != boxStatus) _currentBoxLog.FinalResult = boxStatus;
            if (_currentBoxLog.ErrorType != boxErr) _currentBoxLog.ErrorType = boxErr;

            // ── 2) 전체 탭 앵커 행 — 택배상태 먼저 채우고 QR/OCR은 대기중 ──────
            //   박스가 QR/OCR보다 먼저 인식되므로, 전체 탭에 이 행을 먼저 올린다.
            //   QR/OCR이 도착하면 OnTrackingNumberUpdated가 이 행에 값을 합친다(새 행 X).
            if (_passAnchorRow == null)
                _passAnchorRow = CreateBoxAnchorRow(vm);
            if (_passAnchorRow.BoxStatus != boxStatus) _passAnchorRow.BoxStatus = boxStatus;
            // QR/OCR 불량 + 택배상태 불량이고 QR/OCR이 자체 오류유형을 가졌으면 오류유형은
            //   QR/OCR 우선 → 박스 클래스(paper_crack 등)로 덮어쓰지 않는다.
            bool qrOwnsErr = (_passAnchorRow.RecognitionType == "QR" || _passAnchorRow.RecognitionType == "OCR")
                             && _passAnchorRow.Status == "불량" && boxStatus == "불량"
                             && !string.IsNullOrEmpty(_passAnchorRow.ErrorType);
            if (!qrOwnsErr && _passAnchorRow.ErrorType != boxErr) _passAnchorRow.ErrorType = boxErr;

            // ── 앵커 최종판정 재계산 ──────────────────────────────────────────
            //   QR/OCR이 이미 병합된 행이면 교차검증으로 FinalResult를 '다시' 계산한다.
            //   (박스 정상→QR/OCR 병합(최종=정상)→그 뒤 paper_crack 검출 시
            //    BoxStatus만 불량으로 바뀌고 최종판정이 정상에 멈추던 버그 방지)
            bool qrMerged = _passAnchorRow.RecognitionType == "QR"
                         || _passAnchorRow.RecognitionType == "OCR";
            if (qrMerged)
            {
                string newFinal = ComputeFinalResult(_passAnchorRow.Status, boxStatus);
                if (_passAnchorRow.FinalResult != newFinal) _passAnchorRow.FinalResult = newFinal;
            }
            // QR/OCR 미병합(대기중)이면 FinalResult는 '대기중' 유지 → 이탈 시 박스 상태로 확정.

            System.Diagnostics.Debug.WriteLine(
                $"[앵커] set box={boxStatus} err='{boxErr}' anchor(St={_passAnchorRow.Status},Box={_passAnchorRow.BoxStatus},Final={_passAnchorRow.FinalResult},Rec='{_passAnchorRow.RecognitionType}',Trk='{_passAnchorRow.TrackingNumber}')");
        }

        // 전체 탭 앵커 행: 택배상태만 채우고 QR/OCR(운송장/인식/권역/상태)은 대기중.
        //   IsBoxAnchor=true → 전체 탭엔 보이고 택배상태 탭(단독 로그)엔 안 보인다.
        private static SortingLog CreateBoxAnchorRow(MainViewModel vm)
        {
            var row = new SortingLog
            {
                Id = vm.SortingLogs.Count + 1,
                Timestamp = DateTime.Now,
                TrackingNumber = "",
                RecognitionType = "",
                Region = "",
                Status = "대기중",       // QR/OCR 인식 대기
                FinalResult = "대기중",
                ProcessingTime = 0,
                Confidence = 0,
                ImagePath = "",
                IsLocal = true,
                IsBoxAnchor = true,
            };
            vm.SortingLogs.Insert(0, row);
            return row;
        }

        // 택배상태를 적용할 QR/OCR 행 찾기.
        //   1) 운송장이 정확히 같은 QR/OCR 행 우선,
        //   2) 없으면 아직 택배상태가 안 붙은(대기중) 가장 최근 QR/OCR 행.
        private static SortingLog? FindQrOcrTarget(MainViewModel vm, string trackNo)
        {
            if (!string.IsNullOrWhiteSpace(trackNo) && !trackNo.StartsWith("BOX-"))
                foreach (var log in vm.SortingLogs)
                    if (log.TrackingNumber == trackNo &&
                        (log.RecognitionType == "QR" || log.RecognitionType == "OCR"))
                        return log;

            foreach (var log in vm.SortingLogs)   // 최신순(Insert(0)) → 가장 최근 대기중 행
                if ((log.RecognitionType == "QR" || log.RecognitionType == "OCR") &&
                    log.BoxStatus == "대기중")
                    return log;
            return null;
        }

        // QR/OCR 매칭이 없을 때 박스 단독 행 (운송장/인식/권역 비움)
        private static SortingLog CreateStandaloneBoxRow(MainViewModel vm)
        {
            var row = new SortingLog
            {
                Id = vm.SortingLogs.Count + 1,
                Timestamp = DateTime.Now,
                TrackingNumber = "",
                RecognitionType = "",
                Region = "",
                Status = "",
                ProcessingTime = 0,
                Confidence = 0,
                ImagePath = "",
                IsLocal = true
            };
            vm.SortingLogs.Insert(0, row);
            return row;
        }

        // 교차검증 최종판정 (실시간 교차)
        //   ① QR/OCR 인식 결과(정상/불량)가 있어야 확정. 없으면(대기중·미인식) → 대기중.
        //   ② QR/OCR 불량 → 무조건 불량.
        //   ③ QR/OCR 정상 → 박스가 '확정 불량'일 때만 불량으로 강등.
        //      박스 정상/비닐/대기중(미검출)은 정상 유지.
        //   ※ ③ 덕분에: OCR/QR 정상인데 박스 미검출(대기중)이라고 대기중/불량으로 잘못
        //     뜨지 않고, 택배상태가 실제 '불량'으로 확정될 때만 최종판정이 불량으로 바뀐다.
        private static string ComputeFinalResult(string? ocrStatus, string boxStatus)
        {
            if (ocrStatus != "정상" && ocrStatus != "불량") return "대기중";
            if (ocrStatus == "불량") return "불량";
            return boxStatus == "불량" ? "불량" : "정상";
        }

        // ── 불량 검출 순간 프레임을 메모리에 캡처 (디스크 저장은 이탈 시 최종판정에 따라) ─
        //   살짝 지연 후, 불량을 본 캠(현장/출고)에서 초록 오버레이가 그려진 프레임을 잡는다.
        //   '항상 출고캠만' 캡처하던 문제를 없애고, 저장 여부 판단은 이탈 시점으로 미룬다.
        private void CaptureDefectFrame(MainViewModel vm, string camId)
        {
            _passDefectCaptureStarted = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(180);   // 불량 박스 오버레이가 그려진 프레임 대기
                    byte[]? jpeg = null;
                    // 박스가 ROI에 있는 동안이므로 프레임이 잠깐 비어도 짧게 재시도.
                    for (int i = 0; i < 5; i++)
                    {
                        jpeg = camId == "field"
                            ? vm.GetFieldFrame?.Invoke()
                            : vm.GetShippingFrame?.Invoke();
                        if (jpeg != null && jpeg.Length > 0) break;
                        await Task.Delay(60);
                    }
                    if (jpeg != null && jpeg.Length > 0)
                        _passDefectImage = jpeg;   // 이탈 시 PersistDefectImage가 사용
                    else
                        System.Diagnostics.Debug.WriteLine($"[불량캡처] {camId} 프레임 비어 캡처 실패");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[불량캡처] 실패: " + ex.Message);
                }
            });
        }

        // ── 이탈 시 최종판정이 '불량'으로 확정 → 검출 프레임을 blackbox\defect\ 에 저장하고
        //    이번 패스의 행(택배상태/전체 탭)에 ImagePath·오류유형을 확정 기입 ─
        private void PersistDefectImage(SortingLog? boxRow, SortingLog? anchorRow)
        {
            byte[]? jpeg = _passDefectImage;
            string defectClass = _passDefectClass;
            if (jpeg == null || jpeg.Length == 0)
            {
                System.Diagnostics.Debug.WriteLine("[불량이미지] 검출 프레임 없음 — 저장 생략");
                return;
            }
            _ = Task.Run(() =>
            {
                try
                {
                    string dir = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "blackbox", "defect");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir,
                        "defect_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".jpg");
                    File.WriteAllBytes(path, jpeg);

                    Dispatcher.Invoke(() =>
                    {
                        if (boxRow != null)
                        {
                            boxRow.ImagePath = path;
                            if (string.IsNullOrEmpty(boxRow.ErrorType)) boxRow.ErrorType = defectClass;
                        }
                        if (anchorRow != null)
                        {
                            // QR/OCR 불량 + 택배상태 불량이고 QR/OCR이 자체 이미지를 가졌으면
                            //   이미지는 QR/OCR 우선 → 박스 검출 이미지로 덮어쓰지 않는다.
                            bool qrOwnsImg = (anchorRow.RecognitionType == "QR" || anchorRow.RecognitionType == "OCR")
                                             && anchorRow.Status == "불량"
                                             && !string.IsNullOrEmpty(anchorRow.ImagePath);
                            if (!qrOwnsImg) anchorRow.ImagePath = path;
                            if (string.IsNullOrEmpty(anchorRow.ErrorType)) anchorRow.ErrorType = defectClass;
                        }
                    });
                    System.Diagnostics.Debug.WriteLine("[불량이미지] 저장: " + path);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[불량이미지] 실패: " + ex.Message);
                }
            });
        }

        // ── 비닐 클래스 판별 (서버 클래스명 'vinyl', 방어적으로 '비닐'도 허용) ─
        private static bool IsVinylClass(string? cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            return cls.IndexOf("vinyl", StringComparison.OrdinalIgnoreCase) >= 0
                || cls.Contains("비닐");
        }

        // ── 박스 이탈 전용 타이머 (100ms 주기, YOLO와 독립) ─────────────
        // YOLO 서버 꺼짐/프레임 스킵 상황에서도 이탈을 안정적으로 확정
        private void StartBoxExitTimer()
        {
            _boxExitTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _boxExitTimer.Tick += (_, _) =>
            {
                if (!_boxInRoi) return;  // 박스 없으면 할 일 없음

                // 두 캠 중 마지막으로 박스를 본 시각 기준으로 이탈 판단
                double secsSinceLastBox = (DateTime.Now - _lastSeenTime).TotalSeconds;

                if (secsSinceLastBox >= BOX_EXIT_CONFIRM_SECS)
                {
                    // 이탈 확정 → 행은 이미 진입 시 만들어 갱신해 둠. 여기선 Flask 1회 전송 + 정리.
                    _boxInRoi = false;

                    // 우선순위: 불량 > 정상(박스) > 비닐
                    string? status = _passSawDefect ? "불량"
                                   : _passSawBox    ? "정상"
                                   : _passSawVinyl  ? "비닐"
                                   : null;

                    if (status != null && DataContext is MainViewModel vm)
                    {
                        // ── Flask로 보낼 운송장번호 결정 ─────────────────────────────
                        //   박스가 QR/OCR보다 먼저 인식되므로 진입 시 잡아둔 _boxLogTrackNo는
                        //   대개 임시번호(BOX-HHmmss)다. 패스 도중 QR/OCR이 도착해 앵커에 병합되면
                        //   앵커에 실제 운송장이 채워지므로, 그 값을 우선 사용한다.
                        //   (임시번호로 저장하면 box_inspections.invoice_no가 실제 운송장과 달라
                        //    재시작 후 전체 탭에서 QR/OCR 행과 매칭되지 않아 택배상태가 복원 안 됨.)
                        string trackNo = (_passAnchorRow != null
                                          && !string.IsNullOrWhiteSpace(_passAnchorRow.TrackingNumber)
                                          && !_passAnchorRow.TrackingNumber.StartsWith("BOX-"))
                            ? _passAnchorRow.TrackingNumber
                            : _boxLogTrackNo;

                        // 박스 상태(정상/불량)를 Flask로 전송 — HTTP POST만 (v6.0: WPF→Flask는 HTTP)
                        if (status == "불량")
                        {
                            // 최종판정이 '불량'으로 확정된 경우에만 미리 잡아둔 검출 프레임을
                            //   디스크에 저장하고 이번 패스의 행(택배상태/전체 탭)에 이미지·오류유형 확정.
                            PersistDefectImage(_currentBoxLog, _passAnchorRow);
                            // 백엔드에도 같은 검출 프레임을 첨부 (이탈 시점 라이브 프레임은 비어 있으므로).
                            _ = vm.PostYoloResultAsync(trackNo, true, _passDefectClass, _passDefectConf, _passDefectImage);
                        }
                        else if (status == "정상")
                            _ = vm.PostYoloResultAsync(trackNo, false, "", 0);

                        // ※ QR/OCR이 병합되지 않은 앵커는 '최종판정'을 박스 상태로 확정하지 않는다.
                        //   교차검증은 OCR/QR 인식 결과가 있어야 성립하므로, 인식이 없으면
                        //   최종판정은 '대기중'으로 둔다 (택배상태 변화만으로 최종판정이 바뀌지 않게).
                        //   QR/OCR이 늦게 도착하면 MergeQrIntoAnchor가 교차검증으로 확정한다.
                        //   ※ 택배상태(박스 단독) 자체는 _currentBoxLog/택배상태 탭에 그대로 표시됨.
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[타이머] 이탈 확정 → {status} (box={_passSawBox} vinyl={_passSawVinyl} defect={_passSawDefect})");

                    _currentBoxLog = null;
                    // 이 박스가 쓴 운송장은 소비 완료 → 비운다. 다음 박스(특히 QR/OCR 없는 박스)가
                    //   이전 박스의 운송장을 물려받아, 그 운송장의 기존(다른) 로그를 폴링 때
                    //   불량/이미지로 덮어쓰던 오염 방지.
                    _currentTrackingNumber = "";
                }
            };
            _boxExitTimer.Start();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  교차검증: 기존 SortingLog의 BoxStatus + FinalResult 업데이트
        //
        //  OCR결과(Status) + YOLO결과(BoxStatus) → 최종판정(FinalResult)
        //   정상 + 정상 → ✅ 정상
        //   정상 + 불량 → ❌ 불량
        //   불량 + 정상 → ❌ 불량
        //   불량 + 불량 → ❌ 불량
        // ═══════════════════════════════════════════════════════════════════

        private void UpdateSortingLogWithYolo(MainViewModel vm, string trackNo, string yoloStatus, string defectClass)
        {
            // 운송장번호가 일치하고 아직 BoxStatus가 대기중인 가장 최근 로그 찾기
            SortingLog? target = null;
            foreach (var log in vm.SortingLogs)
            {
                if (log.TrackingNumber == trackNo && log.BoxStatus == "대기중")
                {
                    target = log;
                    break;  // 가장 최근 것 (Insert(0) 이므로 앞에 있는 게 최신)
                }
            }

            if (target != null)
            {
                // 기존 행 업데이트
                target.BoxStatus = yoloStatus;

                // 교차검증 최종 판정
                if (target.Status == "정상" && yoloStatus == "정상")
                    target.FinalResult = "정상";
                else
                    target.FinalResult = "불량";

                if (!string.IsNullOrEmpty(defectClass) && string.IsNullOrEmpty(target.ErrorType))
                    target.ErrorType = defectClass;

                // DataGrid 갱신
                var view = System.Windows.Data.CollectionViewSource
                    .GetDefaultView(SortingGrid.ItemsSource);
                view?.Refresh();

                System.Diagnostics.Debug.WriteLine(
                    $"[교차검증] {trackNo}: OCR={target.Status} + YOLO={yoloStatus} → {target.FinalResult}");
            }
            else
            {
                // 매칭 로그 없으면 YOLO 단독 행 생성
                string finalResult = (yoloStatus == "정상") ? "정상" : "불량";
                string displayError = string.IsNullOrEmpty(defectClass)
                    ? (yoloStatus == "정상" ? "이상없음" : "불량감지")
                    : defectClass;

                vm.SortingLogs.Insert(0, new SortingLog
                {
                    Id = vm.SortingLogs.Count + 1,
                    Timestamp = DateTime.Now,
                    TrackingNumber = trackNo,
                    RecognitionType = "YOLO",
                    Region = "출고캠",
                    Status = "-",
                    BoxStatus = yoloStatus,
                    FinalResult = finalResult,
                    ErrorType = displayError,
                    ProcessingTime = 0,
                    Confidence = 0,
                    ImagePath = ""
                });

                System.Diagnostics.Debug.WriteLine(
                    $"[교차검증] {trackNo}: OCR=없음 + YOLO={yoloStatus} → {finalResult} (단독)");
            }
        }

        // ── 운송장번호 업데이트 (QR/OCR 인식 시 최신값 기록) ─────────────
        private void OnTrackingNumberUpdated(object? sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                return;
            if (e.NewItems == null) return;

            foreach (SortingLog log in e.NewItems)
            {
                if (log.IsBoxAnchor) continue;   // 앵커 행 자신은 스킵
                if (log.RecognitionType != "QR" && log.RecognitionType != "OCR") continue;

                var vm = DataContext as MainViewModel;

                // 폴링/새로고침으로 과거 '오래된' 이력 행이 라이브 박스 앵커에 병합돼
                //   둔갑하거나(새로고침 시 옛 002 데이터가 현재 박스로 끌려들어오던 문제),
                //   _currentTrackingNumber를 옛 운송장으로 덮어써 박스상태 POST가 엉뚱한 옛
                //   패키지에 붙던 문제를 막는다.
                //   단, 이력 로딩이라도 '최근(현재 박스)' 행은 정상 병합해야 한다 — 현재 박스의
                //   QR/OCR이 폴링으로 들어와도 앵커에 합쳐지게(과보정 방지). 라이브 소켓 행은
                //   historyLoad=false라 항상 병합된다(소켓 페이로드엔 타임스탬프가 없어 staleness
                //   판정 대상이 아님).
                bool historyLoad = vm?.IsHistoryLoading ?? false;
                bool isStale = historyLoad
                               && (DateTime.Now - log.Timestamp) > TimeSpan.FromSeconds(120);

                bool isFail = log.Status == "불량";   // QR/OCR 인식 실패(불량)
                bool hasTrack = !string.IsNullOrWhiteSpace(log.TrackingNumber)
                                && log.TrackingNumber != "-";

                // 인식 성공(정상)은 운송장번호가 있어야 처리. 인식 실패(불량)는 운송장이
                //   비어 와도 처리해 앵커를 '불량'으로 확정한다(전체 탭에 정상으로 잘못 뜨던 버그).
                if (!hasTrack && !isFail) continue;

                // 다음 박스가 물려받을 현재 운송장번호는 '성공'일 때만 갱신.
                //   실패면 건드리지 않아 이전 박스 운송장이 재사용되지 않게 한다.
                //   (오래된 이력 행은 제외 — 현재 운송장을 오염시키지 않게)
                if (hasTrack && !isStale)
                {
                    _currentTrackingNumber = log.TrackingNumber;
                    System.Diagnostics.Debug.WriteLine(
                        $"[추적] 운송장번호 업데이트 → {_currentTrackingNumber}");
                }

                // 박스를 먼저 인식해 만든 "대기중" 앵커 행이 있으면 → 그 행에 QR/OCR 값을
                //   합치고, 방금 들어온 이 QR/OCR 행은 제거(전체 탭에 한 행만 남도록).
                if (vm != null)
                {
                    // 오래된 이력 행은 라이브 앵커에 병합하지 않는다(옛 행이 단독 이력으로 남게).
                    var anchor = isStale ? null : vm.SortingLogs.LastOrDefault(
                        x => x.IsBoxAnchor && x.Status == "대기중");
                    if (anchor != null)
                    {
                        MergeQrIntoAnchor(anchor, log);
                        var toRemove = log;
                        // CollectionChanged 재진입 방지 위해 제거는 지연 실행
                        Dispatcher.BeginInvoke(new Action(() => vm.SortingLogs.Remove(toRemove)));
                    }
                    else
                    {
                        // 박스 앵커가 없으면(QR/OCR이 박스보다 먼저 인식됐거나 박스 미검출)
                        //   이 QR/OCR 행 자체로 최종판정을 확정한다. 안 그러면 인식 정상인데
                        //   최종판정이 '대기중'에 멈춘다. 박스 미검출이면 BoxStatus='대기중'이라
                        //   ComputeFinalResult가 정상→정상으로 확정. 이후 박스 검수가 '불량'으로
                        //   매칭되면(폴링/이탈) BoxStatus·최종판정이 교차검증으로 갱신된다.
                        log.FinalResult = ComputeFinalResult(log.Status, log.BoxStatus);
                    }
                }
            }
        }

        // 박스 먼저 인식해 만든 앵커 행(전체 탭)에 QR/OCR 값을 합친다.
        //   대기중이던 QR/OCR 부분(운송장/인식/권역/상태)을 채우고 최종판정을 계산.
        private static void MergeQrIntoAnchor(SortingLog anchor, SortingLog qr)
        {
            anchor.TrackingNumber = qr.TrackingNumber;
            anchor.RecognitionType = qr.RecognitionType;   // 대기중 → QR/OCR
            anchor.Region = qr.Region;
            anchor.Status = qr.Status;                     // 대기중 → QR/OCR 인식 결과(정상/불량)
            anchor.Id = qr.Id;
            anchor.PackageId = qr.PackageId;               // 폴링 오류로그(api/logs/error) 중복방지 매칭용
            anchor.MergeKey = qr.MergeKey;                 // 폴링 중복방지 키 승계
            anchor.IsLocal = false;                        // 이제 DB행(QR/OCR) → 폴링 dedup 대상
            anchor.ProcessingTime = qr.ProcessingTime;
            anchor.Confidence = qr.Confidence;
            // QR/OCR 불량 + 택배상태 불량 → 오류유형·이미지를 QR/OCR 우선
            //   (박스 paper_crack·박스 이미지를 QR/OCR 값으로 덮어쓴다). QR/OCR 쪽이
            //   비어 있으면 기존 박스 값을 그대로 둔다. 그 외 경우는 종전과 동일.
            if (qr.Status == "불량" && anchor.BoxStatus == "불량")
            {
                if (!string.IsNullOrEmpty(qr.ErrorType)) anchor.ErrorType = qr.ErrorType;
                if (!string.IsNullOrEmpty(qr.ImagePath)) anchor.ImagePath = qr.ImagePath;
            }
            else
            {
                if (!string.IsNullOrEmpty(qr.ErrorType) && anchor.BoxStatus != "불량")
                    anchor.ErrorType = qr.ErrorType;
                if (!string.IsNullOrEmpty(qr.ImagePath) && string.IsNullOrEmpty(anchor.ImagePath))
                    anchor.ImagePath = qr.ImagePath;
            }
            // 최종판정 = QR/OCR 정상 AND 박스 정상 → 정상, 그 외 → 불량
            anchor.FinalResult = ComputeFinalResult(qr.Status, anchor.BoxStatus);

            System.Diagnostics.Debug.WriteLine(
                $"[병합] anchor←qr  Trk='{qr.TrackingNumber}' Rec='{qr.RecognitionType}' St='{qr.Status}' → anchor(Box={anchor.BoxStatus},Final={anchor.FinalResult},Err='{anchor.ErrorType}')");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  ROI2 — 불량 감지 시 bbox 위치로 즉시 이동 (스캔 없음)
        //
        //  동작 방식:
        //   ① ROI2 기본 상태 = 숨김
        //   ② YOLO가 ROI1 안에서 박스/불량을 감지 → ROI2가 해당 bbox로 점프
        //   ③ 3초 후 새 감지 없으면 ROI2 자동 숨김
        // ═══════════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════════
        //  OpenCV 프레임 직접 그리기 (1280×720 카메라 좌표)
        //  QR/OCR 캠처럼 detection 결과를 영상 위에 직접 렌더링
        // ═══════════════════════════════════════════════════════════════════

        // ── ROI1 고정 좌표 헬퍼 (1280x720 기준, 왼쪽 아래 영역) ──────────
        //   x: 0 ~ 57%,  y: 50% ~ 끝
        // 프레임 비율(0~1) → 실제 프레임 픽셀 Rect
        private static OpenCvSharp.Rect GetRoiRect(Mat frame, double fx, double fy, double fw, double fh) =>
            new OpenCvSharp.Rect(
                (int)(frame.Width  * fx),
                (int)(frame.Height * fy),
                (int)(frame.Width  * fw),
                (int)(frame.Height * fh));

        // ── ① ROI 외곽 테두리 (항상 표시, 빨간색 = 박스 감지 영역) ────────
        private static void DrawRoi1Border(Mat frame, OpenCvSharp.Rect roi)
        {
            Cv2.Rectangle(frame, roi, new Scalar(0, 0, 255), 2);
            Cv2.PutText(frame, "BOX DETECT",
                new OpenCvSharp.Point(roi.X + 4, roi.Y + 18),
                HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 255), 1);
        }

        // ── 폴리곤 점([[x,y],...], ROI 크롭 좌표) → 절대 프레임 좌표 변환 ──
        private static OpenCvSharp.Point[] ToAbsPolygon(int[][]? poly, int ox, int oy)
        {
            if (poly == null || poly.Length == 0)
                return Array.Empty<OpenCvSharp.Point>();
            var pts = new List<OpenCvSharp.Point>(poly.Length);
            foreach (var p in poly)
                if (p != null && p.Length >= 2)
                    pts.Add(new OpenCvSharp.Point(ox + p[0], oy + p[1]));
            return pts.ToArray();
        }

        // ── ② 박스 감지 표시 (빨간 외곽선) + 판정/인지율 라벨 (캠별) ───────
        private void DrawBoxOverlay(Mat frame, CamDetState st, OpenCvSharp.Rect roi1)
        {
            if ((DateTime.Now - st.BoxTime).TotalSeconds > BOX_SHOW_SECS) return;

            var red = new Scalar(0, 0, 255);

            // 박스 인지 영역(빨간 외곽선) — 폴리곤 우선, 없으면 bbox
            OpenCvSharp.Point anchor;
            var poly = ToAbsPolygon(st.BoxPoly, roi1.X, roi1.Y);
            if (poly.Length >= 3)
            {
                Cv2.Polylines(frame, new[] { poly }, true, red, 2);
                anchor = poly[0];
            }
            else
            {
                var b = st.BoxBbox;
                if (b == null || b.Length < 4) return;
                var r = new OpenCvSharp.Rect(
                    roi1.X + b[0], roi1.Y + b[1],
                    Math.Max(b[2] - b[0], 10), Math.Max(b[3] - b[1], 10));
                Cv2.Rectangle(frame, r, red, 2);
                anchor = new OpenCvSharp.Point(r.X, r.Y);
            }

            // 판정 + 인지율 (불량 > 비닐 > 정상). 판정은 앞/뒤 합친 공유 결과.
            // 한글 렌더 불가 → 영문 표기
            string verdict; Scalar bg; double conf;
            if (_passSawDefect)
            {
                verdict = "DEFECT"; bg = new Scalar(0, 0, 230); conf = _passDefectConf;   // 빨강
            }
            else if (_passSawVinyl && !_passSawBox)
            {
                verdict = "VINYL"; bg = new Scalar(220, 60, 160); conf = st.BoxConf;       // 보라
            }
            else
            {
                verdict = "NORMAL"; bg = new Scalar(0, 170, 0); conf = st.BoxConf;         // 초록
            }

            DrawStatusLabel(frame, anchor, $"{verdict} {conf * 100:F0}%", bg);
        }

        // 영상 위에 배경 박스 + 흰 글씨 라벨
        private static void DrawStatusLabel(Mat frame, OpenCvSharp.Point anchor, string text, Scalar bg)
        {
            const HersheyFonts font = HersheyFonts.HersheySimplex;
            const double scale = 0.6;
            const int thick = 2;
            var sz = Cv2.GetTextSize(text, font, scale, thick, out int baseline);
            int x = Math.Max(anchor.X, 2);
            int y = Math.Max(anchor.Y - 8, sz.Height + 8);
            Cv2.Rectangle(frame,
                new OpenCvSharp.Rect(x, y - sz.Height - 8, sz.Width + 12, sz.Height + baseline + 8),
                bg, -1);
            Cv2.PutText(frame, text, new OpenCvSharp.Point(x + 6, y),
                font, scale, new Scalar(255, 255, 255), thick, LineTypes.AntiAlias);
        }

        // ── ③ 불량 검출 표시 (초록색) — 폴리곤 외곽선 우선, 없으면 사각형 (캠별) ──
        private void DrawDefectOverlay(Mat frame, CamDetState st, OpenCvSharp.Rect roi1)
        {
            if (st.DefectResult == null) return;
            if ((DateTime.Now - st.DefectTime).TotalSeconds > DEFECT_SHOW_SECS) return;
            if (st.DefectResult.Detections.Length == 0) return;

            // ROI 화면에도 70% 이상만 표시 — 임계 미만 불량 bbox는 그리지 않음
            DetectionItem? d = null;
            foreach (var cand in st.DefectResult.Detections)
                if (cand.Confidence >= DEFECT_CONF_THRESHOLD) { d = cand; break; }
            if (d == null) return;

            var green = new Scalar(0, 255, 0);

            // YOLO 좌표는 ROI 크롭 기준 → ROI 원점 더해서 복원
            string cls = d.Class;
            double conf = d.Confidence;
            string label = $"DEFECT:{cls} {conf:P0}";

            // 폴리곤(세그멘테이션)이 있으면 외곽선 + 반투명 채움
            var poly = ToAbsPolygon(d.Polygon, roi1.X, roi1.Y);
            if (poly.Length >= 3)
            {
                using var overlay = frame.Clone();
                Cv2.FillPoly(overlay, new[] { poly }, green);
                Cv2.AddWeighted(overlay, 0.25, frame, 0.75, 0, frame);
                Cv2.Polylines(frame, new[] { poly }, true, green, 2);

                var p0 = poly[0];
                Cv2.PutText(frame, label,
                    new OpenCvSharp.Point(p0.X, Math.Max(p0.Y - 6, 14)),
                    HersheyFonts.HersheySimplex, 0.45, green, 1);
                return;
            }

            // 폴리곤 없으면 사각형 폴백
            var b = d.Bbox;
            if (b.Length < 4) return;
            var defRect = new OpenCvSharp.Rect(
                roi1.X + b[0], roi1.Y + b[1],
                Math.Max(b[2] - b[0], 10), Math.Max(b[3] - b[1], 10));

            using (var overlay = frame.Clone())
            {
                Cv2.Rectangle(overlay, defRect, green, -1);
                Cv2.AddWeighted(overlay, 0.25, frame, 0.75, 0, frame);
            }
            Cv2.Rectangle(frame, defRect, green, 2);
            Cv2.PutText(frame, label,
                new OpenCvSharp.Point(defRect.X, Math.Max(defRect.Y - 6, 14)),
                HersheyFonts.HersheySimplex, 0.45, green, 1);
        }

        // ── 현재 ROI 비율을 YOLO 서버에 적용 (캠별 크롭 영역 설정) ─────────
        private void ApplyRoiToYolo()
        {
            _yolo?.SetRoiFrac("shipping", _roiXFrac, _roiYFrac, _roiWFrac, _roiHFrac);
            _yolo?.SetRoiFrac("field", _fieldRoiXFrac, _fieldRoiYFrac, _fieldRoiWFrac, _fieldRoiHFrac);
        }

        // ── ROI 초기 적용 + 안내문구 ─────────────────────────────────────
        private void InitRoi1Display()
        {
            _roi1Initialized = true;
            ApplyRoiToYolo();

            // WPF Canvas 오버레이는 사용 안 함 — OpenCV로 프레임에 직접 그림
            Roi1Rect.Visibility = Visibility.Collapsed;
            Roi1Label.Visibility = Visibility.Collapsed;
            Roi2Rect.Visibility = Visibility.Collapsed;
            Roi2Label.Visibility = Visibility.Collapsed;
            BoxDetectRect.Visibility = Visibility.Collapsed;
            BoxDetectLabel.Visibility = Visibility.Collapsed;

            RoiGuideText.Text =
                $"🖱 드래그: 출고 ROI  (현재 x{_roiXFrac:P0} y{_roiYFrac:P0} w{_roiWFrac:P0} h{_roiHFrac:P0})";
            ShippingCamGrid.Cursor = System.Windows.Input.Cursors.Cross;

            FieldRoiGuideText.Text =
                $"🖱 드래그: 현장 ROI  (현재 x{_fieldRoiXFrac:P0} y{_fieldRoiYFrac:P0} w{_fieldRoiWFrac:P0} h{_fieldRoiHFrac:P0})";
        }

        private void ResetRoi()
        {
            _roi1Initialized = false;
            _boxCount = 0;
            _shipState.BoxBbox = _fieldState.BoxBbox = null;
            _shipState.BoxPoly = _fieldState.BoxPoly = null;
            _shipState.DefectResult = _fieldState.DefectResult = null;
            _boxInRoi = false;
            _passSawBox = false;
            _passSawVinyl = false;
            _passSawDefect = false;
            _passDefectClass = "";
            _passDefectConf = 0;
            _passDefectCaptureStarted = false;
            _passDefectImage = null;
            _passDefectCam = "";
            _currentBoxLog = null;
            _boxLogTrackNo = "";
            _boxExitTime = DateTime.MinValue;
            _yolo?.ClearRoi();
            InitRoi1Display();
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // ── 화면(UniformToFill) 좌표 → 프레임 비율(0~1) 변환 ──────────────
        //   fw/fh = 실제 카메라 프레임 픽셀 크기(예: 640×480) — 해상도 무관
        private (double fx, double fy) ScreenToFrameFrac(
            System.Windows.Point p, double cw, double ch, double fw, double fh)
        {
            if (cw <= 0 || ch <= 0 || fw <= 0 || fh <= 0) return (0, 0);
            double scale = Math.Max(cw / fw, ch / fh);
            double offX = (fw * scale - cw) / 2.0;
            double offY = (fh * scale - ch) / 2.0;
            double fx = (p.X + offX) / scale / fw;
            double fy = (p.Y + offY) / scale / fh;
            return (Clamp01(fx), Clamp01(fy));
        }

        // 현재 표시 중인 카메라 프레임의 실제 픽셀 크기
        private (double fw, double fh) GetFrameSize(System.Windows.Controls.Image img)
        {
            if (img.Source is System.Windows.Media.Imaging.BitmapSource bs
                && bs.PixelWidth > 0 && bs.PixelHeight > 0)
                return (bs.PixelWidth, bs.PixelHeight);
            return (CAM_W, CAM_H); // 폴백
        }

        // roi_config.txt: 출고4 + 현장4 (8값). 과거 4값이면 출고에만 적용.
        private void LoadRoiConfig()
        {
            try
            {
                if (!File.Exists(RoiConfigPath)) return;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var parts = File.ReadAllText(RoiConfigPath).Split(',');
                double P(int i) => double.TryParse(parts[i],
                    System.Globalization.NumberStyles.Float, ci, out var v) ? v : -1;

                if (parts.Length >= 4 && P(0) >= 0 && P(1) >= 0 && P(2) >= 0 && P(3) >= 0)
                {
                    _roiXFrac = Clamp01(P(0)); _roiYFrac = Clamp01(P(1));
                    _roiWFrac = Math.Max(0.05, Clamp01(P(2))); _roiHFrac = Math.Max(0.05, Clamp01(P(3)));
                }
                if (parts.Length >= 8 && P(4) >= 0 && P(5) >= 0 && P(6) >= 0 && P(7) >= 0)
                {
                    _fieldRoiXFrac = Clamp01(P(4)); _fieldRoiYFrac = Clamp01(P(5));
                    _fieldRoiWFrac = Math.Max(0.05, Clamp01(P(6))); _fieldRoiHFrac = Math.Max(0.05, Clamp01(P(7)));
                }
            }
            catch { }
        }

        private void SaveRoiConfig()
        {
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                string s = string.Join(",",
                    _roiXFrac.ToString(ci), _roiYFrac.ToString(ci),
                    _roiWFrac.ToString(ci), _roiHFrac.ToString(ci),
                    _fieldRoiXFrac.ToString(ci), _fieldRoiYFrac.ToString(ci),
                    _fieldRoiWFrac.ToString(ci), _fieldRoiHFrac.ToString(ci));
                File.WriteAllText(RoiConfigPath, s);
            }
            catch { }
        }

        // ── 마우스 드래그로 ROI 직접 그리기 ──────────────────────────────
        private void ShippingCam_MouseDown(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            _drawingRoi = true;
            _roiDrawStart = e.GetPosition(ShippingCamGrid);
            Canvas.SetLeft(DrawingRect, _roiDrawStart.X);
            Canvas.SetTop(DrawingRect, _roiDrawStart.Y);
            DrawingRect.Width = 0;
            DrawingRect.Height = 0;
            DrawingRect.Stroke = System.Windows.Media.Brushes.Red;
            DrawingRect.StrokeThickness = 2;
            DrawingRect.Visibility = Visibility.Visible;
            ShippingCamGrid.CaptureMouse();
        }

        private void ShippingCam_MouseMove(object sender,
            System.Windows.Input.MouseEventArgs e)
        {
            if (!_drawingRoi) return;
            var p = e.GetPosition(ShippingCamGrid);
            Canvas.SetLeft(DrawingRect, Math.Min(p.X, _roiDrawStart.X));
            Canvas.SetTop(DrawingRect, Math.Min(p.Y, _roiDrawStart.Y));
            DrawingRect.Width = Math.Abs(p.X - _roiDrawStart.X);
            DrawingRect.Height = Math.Abs(p.Y - _roiDrawStart.Y);
        }

        private void ShippingCam_MouseUp(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_drawingRoi) return;
            _drawingRoi = false;
            ShippingCamGrid.ReleaseMouseCapture();
            DrawingRect.Visibility = Visibility.Collapsed;

            var p = e.GetPosition(ShippingCamGrid);
            double cw = ShippingCamGrid.ActualWidth;
            double ch = ShippingCamGrid.ActualHeight;

            // 너무 작은 드래그(클릭 수준)는 무시
            if (Math.Abs(p.X - _roiDrawStart.X) < 15 ||
                Math.Abs(p.Y - _roiDrawStart.Y) < 15)
                return;

            var (fw, fh) = GetFrameSize(CamShippingImg);
            var (fx1, fy1) = ScreenToFrameFrac(_roiDrawStart, cw, ch, fw, fh);
            var (fx2, fy2) = ScreenToFrameFrac(p, cw, ch, fw, fh);

            _roiXFrac = Math.Min(fx1, fx2);
            _roiYFrac = Math.Min(fy1, fy2);
            _roiWFrac = Math.Max(0.05, Math.Abs(fx2 - fx1));
            _roiHFrac = Math.Max(0.05, Math.Abs(fy2 - fy1));

            SaveRoiConfig();
            ApplyRoiToYolo();
            ResetPassState();

            RoiGuideText.Text =
                $"✓ 출고 ROI  x{_roiXFrac:P1} y{_roiYFrac:P1} w{_roiWFrac:P1} h{_roiHFrac:P1}  (다시 드래그=재설정)";
            System.Diagnostics.Debug.WriteLine(
                $"[ROI] 출고 → x={_roiXFrac:F3} y={_roiYFrac:F3} w={_roiWFrac:F3} h={_roiHFrac:F3}");
        }

        // ── 현장캠 ROI 드래그 (출고캠과 동일, 현장 ROI에 적용) ────────────
        private void FieldCam_MouseDown(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            _drawingFieldRoi = true;
            _fieldRoiDrawStart = e.GetPosition(FieldCamGrid);
            Canvas.SetLeft(FieldDrawingRect, _fieldRoiDrawStart.X);
            Canvas.SetTop(FieldDrawingRect, _fieldRoiDrawStart.Y);
            FieldDrawingRect.Width = 0;
            FieldDrawingRect.Height = 0;
            FieldDrawingRect.Visibility = Visibility.Visible;
            FieldCamGrid.CaptureMouse();
        }

        private void FieldCam_MouseMove(object sender,
            System.Windows.Input.MouseEventArgs e)
        {
            if (!_drawingFieldRoi) return;
            var p = e.GetPosition(FieldCamGrid);
            Canvas.SetLeft(FieldDrawingRect, Math.Min(p.X, _fieldRoiDrawStart.X));
            Canvas.SetTop(FieldDrawingRect, Math.Min(p.Y, _fieldRoiDrawStart.Y));
            FieldDrawingRect.Width = Math.Abs(p.X - _fieldRoiDrawStart.X);
            FieldDrawingRect.Height = Math.Abs(p.Y - _fieldRoiDrawStart.Y);
        }

        private void FieldCam_MouseUp(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_drawingFieldRoi) return;
            _drawingFieldRoi = false;
            FieldCamGrid.ReleaseMouseCapture();
            FieldDrawingRect.Visibility = Visibility.Collapsed;

            var p = e.GetPosition(FieldCamGrid);
            double cw = FieldCamGrid.ActualWidth;
            double ch = FieldCamGrid.ActualHeight;

            if (Math.Abs(p.X - _fieldRoiDrawStart.X) < 15 ||
                Math.Abs(p.Y - _fieldRoiDrawStart.Y) < 15)
                return;

            var (fw, fh) = GetFrameSize(CamFieldImg);
            var (fx1, fy1) = ScreenToFrameFrac(_fieldRoiDrawStart, cw, ch, fw, fh);
            var (fx2, fy2) = ScreenToFrameFrac(p, cw, ch, fw, fh);

            _fieldRoiXFrac = Math.Min(fx1, fx2);
            _fieldRoiYFrac = Math.Min(fy1, fy2);
            _fieldRoiWFrac = Math.Max(0.05, Math.Abs(fx2 - fx1));
            _fieldRoiHFrac = Math.Max(0.05, Math.Abs(fy2 - fy1));

            SaveRoiConfig();
            ApplyRoiToYolo();
            ResetPassState();

            FieldRoiGuideText.Text =
                $"✓ 현장 ROI  x{_fieldRoiXFrac:P1} y{_fieldRoiYFrac:P1} w{_fieldRoiWFrac:P1} h{_fieldRoiHFrac:P1}  (다시 드래그=재설정)";
            System.Diagnostics.Debug.WriteLine(
                $"[ROI] 현장 → x={_fieldRoiXFrac:F3} y={_fieldRoiYFrac:F3} w={_fieldRoiWFrac:F3} h={_fieldRoiHFrac:F3}");
        }

        // ROI 변경 시 진행 중 박스 패스 초기화
        private void ResetPassState()
        {
            _boxInRoi = false;
            _passSawBox = false;
            _passSawVinyl = false;
            _passSawDefect = false;
            _currentBoxLog = null;
        }

        private void BtnSortFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            _sortFilter = btn.Tag?.ToString() ?? "전체";
            RefreshBtnStyles(_sortBtnDefs!, btn);
            ApplySortFilter();
        }

        // ── 분류 이력 로그 새로고침 (서버에서 다시 로드 후 현재 필터 재적용) ─
        private async void BtnRefreshLogs_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not Button btn) { await vm.RefreshLogsAsync(); ApplySortFilter(); return; }

            // 중복 클릭 방지 + 진행 표시
            btn.IsEnabled = false;
            string original = btn.Content?.ToString() ?? "🔄 새로고침";
            btn.Content = "불러오는 중…";
            try
            {
                await vm.RefreshLogsAsync();
                ApplySortFilter();   // 새로 들어온 행에도 현재 날짜/상태 필터 유지
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[새로고침] 실패: " + ex.Message);
            }
            finally
            {
                btn.Content = original;
                btn.IsEnabled = true;
            }
        }

        // ── 날짜 필터 ────────────────────────────────────────────────────
        private void LogDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedDate = LogDatePicker.SelectedDate;
            ApplySortFilter();
        }

        private void BtnShowAllDates_Click(object sender, RoutedEventArgs e)
        {
            _selectedDate = null;
            LogDatePicker.SelectedDate = null;
            ApplySortFilter();
        }

        private void ApplySortFilter()
        {
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(SortingGrid.ItemsSource);
            if (view == null) return;

            view.Filter = o =>
            {
                if (o is not SortingLog l) return false;

                // 날짜 필터
                if (_selectedDate.HasValue && l.Timestamp.Date != _selectedDate.Value.Date)
                    return false;

                // 택배상태 단독 로그(인식 없음) 여부 — 출고캠 YOLO만 판정한 박스 행
                bool isBoxOnly = string.IsNullOrEmpty(l.RecognitionType);

                // 박스에 합쳐지지 않은 '단독 인식실패' 행(택배상태 없음)은 전체 탭에서 숨김.
                //   ① 폴링 에러로그(운송장 'FAIL-xxx')  ② 라이브 스캔실패(SCAN_TIMEOUT, 인식 '-')
                //   ③ 박스 앵커에 못 합쳐진 라이브 QR/OCR 실패(빈 운송장)
                //   모두 박스 앵커도 아니고 택배상태가 안 붙은 실패 → 한 박스당 한 행만 남도록
                //   전체 탭에서만 숨긴다. (QR/OCR 실패 탭에는 해당 탭 조건으로 그대로 노출)
                bool unmatchedFail = !l.IsBoxAnchor
                                     && l.Status == "불량"
                                     && !string.IsNullOrEmpty(l.RecognitionType)
                                     && (string.IsNullOrEmpty(l.BoxStatus) || l.BoxStatus == "대기중");

                // 상태 필터
                //  · 전체: QR/OCR 행 + 박스 앵커 행(택배상태 먼저 뜬 행). 박스 단독 로그·
                //          박스 미매칭 단독실패는 숨김
                //  · 택배상태: 박스 단독 로그만 (앵커 행 제외 → 로그대로 유지)
                if (_sortFilter == "전체") return (!isBoxOnly || l.IsBoxAnchor) && !unmatchedFail;
                if (_sortFilter == "QR인식실패") return l.RecognitionType == "QR" && l.Status == "불량";
                if (_sortFilter == "OCR인식실패") return l.RecognitionType == "OCR" && l.Status == "불량";
                if (_sortFilter == "택배상태") return isBoxOnly && !l.IsBoxAnchor;
                return !isBoxOnly || l.IsBoxAnchor;
            };
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
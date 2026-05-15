using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace MasterAdmin
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;
        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public event EventHandler? CanExecuteChanged
        {
            add    { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
        public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
        public void Execute(object? p)    => _execute(p);
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly DispatcherTimer _timer;
        private readonly Random _rng = new();
        private int _autoId = 200;
        private int _tick   = 0;

        // ── 이미지 저장 기본 경로 (실제 환경에서는 설정으로 변경)
        private const string BlackboxRoot = @"blackbox\";

        // ── Navigation
        private string _currentPage = "현장";
        public string CurrentPage
        {
            get => _currentPage;
            set { _currentPage = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsFieldPage)); OnPropertyChanged(nameof(IsOverviewPage)); }
        }
        public bool IsFieldPage    => _currentPage == "현장";
        public bool IsOverviewPage => _currentPage == "총괄";

        // ── Clock
        private string _currentTime = "";
        public string CurrentTime
        {
            get => _currentTime;
            set { _currentTime = value; OnPropertyChanged(); }
        }

        // ── Emergency Stop
        private bool _isEmergencyStop = false;
        public bool IsEmergencyStop
        {
            get => _isEmergencyStop;
            set { _isEmergencyStop = value; OnPropertyChanged(); DeviceStatus.EmergencyStop = value; OnPropertyChanged(nameof(DeviceStatus)); }
        }

        public DeviceStatus DeviceStatus { get; set; } = new();

        public ObservableCollection<SortingLog>    SortingLogs    { get; } = new();
        public ObservableCollection<ShippingLog>   ShippingLogs   { get; } = new();
        public ObservableCollection<BlackboxEvent> BlackboxEvents { get; } = new();
        public ObservableCollection<LoginRecord>   LoginRecords   { get; } = new();

        public ICommand NavigateToFieldCommand    { get; }
        public ICommand NavigateToOverviewCommand { get; }
        public ICommand ToggleEmergencyCommand    { get; }
        public ICommand ToggleThemeCommand        { get; }
        public ICommand ClearLogsCommand          { get; }

        // ── Theme
        private bool _isDark = true;
        public bool IsDark
        {
            get => _isDark;
            set { _isDark = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThemeIcon)); OnPropertyChanged(nameof(ThemeLabel)); }
        }
        public string ThemeIcon  => IsDark ? "☀" : "🌙";
        public string ThemeLabel => IsDark ? "라이트 모드" : "다크 모드";

        public MainViewModel()
        {
            NavigateToFieldCommand    = new RelayCommand(_ => CurrentPage = "현장");
            NavigateToOverviewCommand = new RelayCommand(_ => CurrentPage = "총괄");
            ToggleEmergencyCommand    = new RelayCommand(_ => IsEmergencyStop = !IsEmergencyStop);
            ToggleThemeCommand        = new RelayCommand(_ =>
            {
                IsDark = !IsDark;
                App.ToggleTheme();
            });
            ClearLogsCommand = new RelayCommand(p =>
            {
                switch (p?.ToString())
                {
                    case "sorting":  SortingLogs.Clear();    break;
                    case "shipping": ShippingLogs.Clear();   break;
                    case "blackbox": BlackboxEvents.Clear(); break;
                    case "login":    LoginRecords.Clear();   break;
                }
            });

            LoadSampleData();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _tick++;
                if (_tick % 4 == 0) SimulateLiveData();
            };
            _timer.Start();
        }

        // ─────────────────────────────────────────────────────────────────
        // 블랙박스 이벤트 생성 헬퍼
        // 이벤트 타입별로 저장 폴더를 분리하고 이미지 파일명을 생성합니다.
        // 실제 환경에서는 이 메서드 안에서 카메라 캡처 → 파일 저장 로직을 연결합니다.
        // ─────────────────────────────────────────────────────────────────
        private BlackboxEvent CreateBlackboxEvent(
            string eventType,
            string description,
            string severity,
            string trackingNumber = "")
        {
            // 이벤트 타입별 하위 폴더
            string subFolder = eventType switch
            {
                BlackboxEventType.OcrFail  => @"ocr_fail\",
                BlackboxEventType.SortFail => @"sort_fail\",
                BlackboxEventType.Jam      => @"jam\",
                _                          => @"etc\"
            };

            string saveFolder = BlackboxRoot + subFolder;
            string fileName   = $"{eventType}_{DateTime.Now:yyyyMMdd_HHmmss}_{_autoId}.jpg";
            string fullPath   = saveFolder + fileName;

            // TODO: 실제 환경에서 여기서 카메라 이미지를 saveFolder에 저장
            // CameraCapture.SaveTo(saveFolder, fileName);

            return new BlackboxEvent
            {
                Id             = _autoId++,
                Timestamp      = DateTime.Now,
                EventType      = eventType,
                Description    = description,
                ImagePath      = fullPath,
                SaveFolder     = saveFolder,
                Severity       = severity,
                TrackingNumber = trackingNumber
            };
        }

        private void AddBlackboxEvent(BlackboxEvent evt)
        {
            BlackboxEvents.Insert(0, evt);
            if (BlackboxEvents.Count > 100) BlackboxEvents.RemoveAt(BlackboxEvents.Count - 1);
        }

        // ─────────────────────────────────────────────────────────────────
        // 시뮬레이션 — 각 이벤트를 독립 조건으로 트리거
        // ─────────────────────────────────────────────────────────────────
        private static readonly string[] Regions  = { "서울", "경기", "인천", "부산", "대구", "광주", "대전" };
        private static readonly string[] RecTypes = { "OCR", "QR" };
        private static readonly string[] ErrTypes = { "인식실패", "권역오류", "타이밍오류" };

        private void SimulateLiveData()
        {
            string region  = Regions[_rng.Next(Regions.Length)];
            string recType = RecTypes[_rng.Next(RecTypes.Length)];
            string tracking = $"1234{_rng.Next(100000, 999999)}";

            // ── 1) OCR 실패 판정 (신뢰도 기준)
            bool ocrFailed    = _rng.NextDouble() < 0.08;  // 8% 확률
            double confidence = ocrFailed
                ? Math.Round(_rng.NextDouble() * 35 + 25, 1)   // 25~60%
                : Math.Round(_rng.NextDouble() * 10 + 89, 1);  // 89~99%

            // ── 2) 분류 실패 판정 (OCR 실패이거나 별도 로봇팔 오류)
            bool sortFailed = ocrFailed || _rng.NextDouble() < 0.05; // OCR실패 + 추가 5%

            // ── 3) 박스 걸림 판정 (독립 확률)
            bool jammed = _rng.NextDouble() < 0.04; // 4% 확률

            bool anyError = ocrFailed || sortFailed || jammed;

            // 분류 로그 추가
            SortingLogs.Insert(0, new SortingLog
            {
                Id              = _autoId++,
                Timestamp       = DateTime.Now,
                TrackingNumber  = tracking,
                RecognitionType = recType,
                Region          = region,
                Status          = sortFailed ? "불량" : "정상",
                ErrorType       = ocrFailed  ? "인식실패"
                                : sortFailed ? ErrTypes[_rng.Next(1, ErrTypes.Length)]
                                : "-",
                ProcessingTime  = Math.Round(_rng.NextDouble() * 1.5 + 0.2, 2),
                Confidence      = confidence
            });
            if (SortingLogs.Count > 60) SortingLogs.RemoveAt(SortingLogs.Count - 1);

            // 통계 갱신
            DeviceStatus.TodaySortedCount++;
            if (anyError) DeviceStatus.TodayErrorCount++;
            DeviceStatus.SuccessRate = DeviceStatus.TodaySortedCount > 0
                ? Math.Round((1.0 - (double)DeviceStatus.TodayErrorCount / DeviceStatus.TodaySortedCount) * 100, 1)
                : 100.0;
            DeviceStatus.ConveyorSpeed = Math.Round(1.2 + _rng.NextDouble() * 0.4, 1);
            OnPropertyChanged(nameof(DeviceStatus));

            // ── 블랙박스: OCR 실패 이벤트 → ocr_fail 폴더에 이미지 저장
            if (ocrFailed)
            {
                AddBlackboxEvent(CreateBlackboxEvent(
                    eventType:      BlackboxEventType.OcrFail,
                    description:    $"신뢰도 {confidence}% — 임계값 미달 (송장번호: {tracking})",
                    severity:       "경고",
                    trackingNumber: tracking));
            }

            // ── 블랙박스: 분류 실패 이벤트 → sort_fail 폴더에 이미지 저장
            if (sortFailed && !ocrFailed) // OCR실패와 중복 방지
            {
                AddBlackboxEvent(CreateBlackboxEvent(
                    eventType:      BlackboxEventType.SortFail,
                    description:    $"로봇팔 분류 실패 — 권역: {region} (송장번호: {tracking})",
                    severity:       "오류",
                    trackingNumber: tracking));
            }

            // ── 블랙박스: 박스 걸림 이벤트 → jam 폴더에 이미지 저장
            if (jammed)
            {
                DeviceStatus.ConveyorStatus = "걸림감지";
                AddBlackboxEvent(CreateBlackboxEvent(
                    eventType:   BlackboxEventType.Jam,
                    description: "컨베이어 박스 걸림 감지 — 자동 정지 후 재시작",
                    severity:    "오류"));
                OnPropertyChanged(nameof(DeviceStatus));
            }
            else if (DeviceStatus.ConveyorStatus == "걸림감지")
            {
                DeviceStatus.ConveyorStatus = "작동중";
                OnPropertyChanged(nameof(DeviceStatus));
            }

            // 출고 로그
            if (_tick % 16 == 0)
            {
                ShippingLogs.Insert(0, new ShippingLog
                {
                    Id             = _autoId++,
                    Timestamp      = DateTime.Now,
                    TrackingNumber = $"9876{_rng.Next(100000, 999999)}",
                    Region         = region,
                    Destination    = $"{Regions[_rng.Next(Regions.Length)]} 물류센터",
                    Status         = new[] { "출고대기", "출고중", "출고완료" }[_rng.Next(3)],
                    SlotNumber     = _rng.Next(1, 8)
                });
                if (ShippingLogs.Count > 40) ShippingLogs.RemoveAt(ShippingLogs.Count - 1);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // 샘플 데이터 — 세 가지 이벤트 타입을 각각 분리해서 생성
        // ─────────────────────────────────────────────────────────────────
        private void LoadSampleData()
        {
            var r = new Random(7);

            for (int i = 0; i < 25; i++)
            {
                bool ok = r.NextDouble() > 0.15;
                SortingLogs.Add(new SortingLog
                {
                    Id              = i + 1,
                    Timestamp       = DateTime.Now.AddMinutes(-(i * 2)),
                    TrackingNumber  = $"1234{r.Next(100000, 999999)}",
                    RecognitionType = r.NextDouble() > 0.4 ? "OCR" : "QR",
                    Region          = Regions[r.Next(Regions.Length)],
                    Status          = ok ? "정상" : "불량",
                    ErrorType       = ok ? "-" : ErrTypes[r.Next(ErrTypes.Length)],
                    ProcessingTime  = Math.Round(r.NextDouble() * 1.5 + 0.2, 2),
                    Confidence      = ok ? Math.Round(r.NextDouble() * 10 + 89, 1)
                                        : Math.Round(r.NextDouble() * 35 + 25, 1)
                });
            }

            for (int i = 0; i < 12; i++)
            {
                ShippingLogs.Add(new ShippingLog
                {
                    Id             = i + 1,
                    Timestamp      = DateTime.Now.AddMinutes(-(i * 5)),
                    TrackingNumber = $"9876{r.Next(100000, 999999)}",
                    Region         = Regions[r.Next(Regions.Length)],
                    Destination    = $"{Regions[r.Next(Regions.Length)]} 물류센터",
                    Status         = new[] { "출고대기", "출고중", "출고완료" }[r.Next(3)],
                    SlotNumber     = r.Next(1, 8)
                });
            }

            // OCR 실패 샘플 4건
            for (int i = 0; i < 4; i++)
            {
                string tracking = $"1234{r.Next(100000, 999999)}";
                double conf = Math.Round(r.NextDouble() * 30 + 25, 1);
                BlackboxEvents.Add(new BlackboxEvent
                {
                    Id             = 100 + i,
                    Timestamp      = DateTime.Now.AddMinutes(-(i * 12)),
                    EventType      = BlackboxEventType.OcrFail,
                    Description    = $"신뢰도 {conf}% — 임계값 미달 (송장번호: {tracking})",
                    ImagePath      = $@"blackbox\ocr_fail\OCR실패_{DateTime.Now.AddMinutes(-(i*12)):yyyyMMdd_HHmmss}_{100+i}.jpg",
                    SaveFolder     = @"blackbox\ocr_fail\",
                    Severity       = "경고",
                    TrackingNumber = tracking
                });
            }

            // 분류 실패 샘플 3건
            for (int i = 0; i < 3; i++)
            {
                string tracking = $"1234{r.Next(100000, 999999)}";
                string region   = Regions[r.Next(Regions.Length)];
                BlackboxEvents.Add(new BlackboxEvent
                {
                    Id             = 110 + i,
                    Timestamp      = DateTime.Now.AddMinutes(-(i * 20 + 5)),
                    EventType      = BlackboxEventType.SortFail,
                    Description    = $"로봇팔 분류 실패 — 권역: {region} (송장번호: {tracking})",
                    ImagePath      = $@"blackbox\sort_fail\분류실패_{DateTime.Now.AddMinutes(-(i*20+5)):yyyyMMdd_HHmmss}_{110+i}.jpg",
                    SaveFolder     = @"blackbox\sort_fail\",
                    Severity       = "오류",
                    TrackingNumber = tracking
                });
            }

            // 박스 걸림 샘플 3건
            for (int i = 0; i < 3; i++)
            {
                BlackboxEvents.Add(new BlackboxEvent
                {
                    Id          = 120 + i,
                    Timestamp   = DateTime.Now.AddMinutes(-(i * 35 + 10)),
                    EventType   = BlackboxEventType.Jam,
                    Description = "컨베이어 박스 걸림 감지 — 자동 정지 후 재시작",
                    ImagePath   = $@"blackbox\jam\박스걸림_{DateTime.Now.AddMinutes(-(i*35+10)):yyyyMMdd_HHmmss}_{120+i}.jpg",
                    SaveFolder  = @"blackbox\jam\",
                    Severity    = "오류"
                });
            }

            (string id, string name, string role)[] users =
            {
                ("admin", "관리자",  "ADMIN"),
                ("op01",  "운영자1", "OPERATOR"),
                ("op02",  "운영자2", "OPERATOR")
            };
            for (int i = 0; i < 14; i++)
            {
                var u = users[r.Next(users.Length)];
                LoginRecords.Add(new LoginRecord
                {
                    Id        = i + 1,
                    Timestamp = DateTime.Now.AddHours(-i),
                    UserId    = u.id,
                    UserName  = u.name,
                    Role      = u.role,
                    IpAddress = $"192.168.1.{r.Next(10, 50)}",
                    Action    = r.NextDouble() > 0.3 ? "로그인" : "로그아웃",
                    Success   = r.NextDouble() > 0.07
                });
            }

            DeviceStatus.TodaySortedCount = 312;
            DeviceStatus.TodayErrorCount  = 16;
            DeviceStatus.SuccessRate      = 94.9;
            DeviceStatus.ConveyorSpeed    = 1.4;
            DeviceStatus.ConveyorStatus   = "작동중";
            DeviceStatus.RobotArmStatus   = "이동중";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

using System;
using Newtonsoft.Json;

namespace MasterAdmin
{
    public class SortingLog : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropChanged(string n)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

        public int Id { get; set; }

        // 패키지 DB id — QR/OCR 실시간 행과 폴링되는 오류로그(api/logs/error)를
        //   같은 건으로 매칭해 전체 탭 중복을 막는 데 쓴다. (실패 행은 운송장이 비어
        //   운송장으로는 매칭 불가하므로 package_id로 잇는다.)
        [JsonProperty("package_id")]
        public int PackageId { get; set; }

        public DateTime Timestamp { get; set; }
        public string TrackingNumber { get; set; } = "";
        public string RecognitionType { get; set; } = "";
        public string Region { get; set; } = "";

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropChanged(nameof(Status)); }
        }

        private string _errorType = "";
        public string ErrorType
        {
            get => _errorType;
            set { _errorType = value; OnPropChanged(nameof(ErrorType)); }
        }

        public double ProcessingTime { get; set; }
        public double Confidence { get; set; }

        private string? _imagePath;
        public string? ImagePath
        {
            get => _imagePath;
            set { _imagePath = value; OnPropChanged(nameof(ImagePath)); }
        }

        private string _boxStatus = "대기중";
        public string BoxStatus
        {
            get => _boxStatus;
            set { _boxStatus = value; OnPropChanged(nameof(BoxStatus)); }
        }

        private string _finalResult = "대기중";
        public string FinalResult
        {
            get => _finalResult;
            set { _finalResult = value; OnPropChanged(nameof(FinalResult)); }
        }

        // 클라이언트(WPF)에서 생성한 행 여부 — DB 새로고침 시 보존(삭제 금지)
        [JsonIgnore]
        public bool IsLocal { get; set; } = false;

        // DB 행 중복 판별용 키("S"+id / "E"+id). 로컬 행은 null.
        [JsonIgnore]
        public string? MergeKey { get; set; }

        // 박스를 먼저 인식해 만든 "앵커 행"(전체 탭). QR/OCR이 오면 이 행에 합쳐진다.
        //   전체 탭에는 보이고(인식 비어도), 택배상태 탭(단독 로그)에는 안 보이게 하는 표식.
        [JsonIgnore]
        public bool IsBoxAnchor { get; set; }
    }

    public class ErrorLog
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("package_id")]
        public int PackageId { get; set; }

        [JsonProperty("error_code")]
        public string? ErrorCode { get; set; }

        [JsonProperty("device_id")]
        public string? DeviceId { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }

        [JsonProperty("image_path")]
        public string? ImagePath { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    // 택배상태(YOLO 박스검수) 로그 — box_inspections 조회용
    public class BoxLog
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("package_id")]
        public int PackageId { get; set; }

        [JsonProperty("invoice_no")]
        public string? InvoiceNo { get; set; }

        [JsonProperty("box_status")]
        public string BoxStatus { get; set; } = "";

        [JsonProperty("defect_class")]
        public string? DefectClass { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("image_path")]
        public string? ImagePath { get; set; }

        [JsonProperty("image_url")]
        public string? ImageUrl { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    public class ShippingLog
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string TrackingNumber { get; set; } = "";
        public string Region { get; set; } = "";
        public string Destination { get; set; } = "";
        public string Status { get; set; } = ""; // 출고대기 / 출고중 / 출고완료
        public int SlotNumber { get; set; }
    }

    // ── 블랙박스 이벤트 타입 ────────────────────────────────────────────
    // OCR_FAIL  : OCR 인식 실패 시 → blackbox/ocr_fail/ 에 이미지 저장
    // SORT_FAIL : 분류 실패 시    → blackbox/sort_fail/ 에 이미지 저장
    // JAM       : 박스 걸림 발생  → blackbox/jam/ 에 이미지 저장
    public static class BlackboxEventType
    {
        public const string OcrFail = "OCR실패";
        public const string SortFail = "분류실패";
        public const string Jam = "박스훼손";
    }

    public class BlackboxEvent
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }

        /// <summary>OCR실패 / 분류실패 / 박스걸림</summary>
        public string EventType { get; set; } = "";

        /// <summary>이벤트 발생 원인 상세 설명</summary>
        public string Description { get; set; } = "";

        /// <summary>저장된 이미지 경로 (blackbox/{type}/{filename})</summary>
        public string ImagePath { get; set; } = "";

        /// <summary>전체 이미지 URL (http://...) — Image.Source에 바로 바인딩 가능</summary>
        [JsonProperty("image_url")]
        public string ImageUrl { get; set; } = "";

        /// <summary>이미지가 실제 저장된 폴더 경로</summary>
        public string SaveFolder { get; set; } = "";

        /// <summary>경고 / 오류</summary>
        public string Severity { get; set; } = "";

        /// <summary>연관 운송장 번호 (있는 경우)</summary>
        public string TrackingNumber { get; set; } = "";
    }

    // ── 박스 훼손 검수 로그 (Flask 교차검증 최종 결과) ──────────────
    public class BoxInspectionLog
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string TrackingNumber { get; set; } = "";
        public string OcrResult { get; set; } = "";   // QR/OCR DB 결과
        public string YoloResult { get; set; } = "";   // YOLO 판정 결과
        public string FinalResult { get; set; } = "";  // Flask 최종 판정
        public string DefectDetail { get; set; } = ""; // 불량 클래스명
        public double Confidence { get; set; }
    }

    public class LoginRecord
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Role { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string Action { get; set; } = ""; // 로그인 / 로그아웃
        public bool Success { get; set; }
    }

    // ── 아두이노 자동차 상태 ────────────────────────────────────────────
    // Status: "출발전" (분류박스 미충족) / "출발중" (이동 중)
    public class CarStatus : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnProp([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

        [Newtonsoft.Json.JsonProperty("car_id")]
        public string CarId { get; set; } = "";

        [Newtonsoft.Json.JsonProperty("car_name")]
        public string CarName { get; set; } = "";

        private string _status = "출발전";
        [Newtonsoft.Json.JsonProperty("status")]
        public string Status
        {
            get => _status;
            set { _status = value; OnProp(); OnProp(nameof(IsReady)); }
        }

        // 각 분류박스 채움 여부 (라파카가 채울 때마다 Flask가 업데이트)
        private int _filledSlots = 0;
        [Newtonsoft.Json.JsonProperty("filled_slots")]
        public int FilledSlots
        {
            get => _filledSlots;
            set { _filledSlots = value; OnProp(); }
        }

        private int _totalSlots = 0;
        [Newtonsoft.Json.JsonProperty("total_slots")]
        public int TotalSlots
        {
            get => _totalSlots;
            set { _totalSlots = value; OnProp(); }
        }

        [Newtonsoft.Json.JsonProperty("last_updated")]
        public DateTime LastUpdated { get; set; } = DateTime.Now;

        // 분류박스 다 채워졌는지 (표시용)
        public bool IsReady => Status == "출발중";
    }

    public class DeviceStatus
    {
        public string ConveyorStatus { get; set; } = "연결전";
        public double ConveyorSpeed { get; set; } = 0;
        public string RobotArmStatus { get; set; } = "대기";
        public string OcrCamStatus { get; set; } = "정상";
        public string QrCamStatus { get; set; } = "정상";
        public bool EmergencyStop { get; set; } = false;
        public string InputUnitStatus { get; set; } = "대기";
        public int TodaySortedCount { get; set; } = 0;
        public int TodayErrorCount { get; set; } = 0;
        public double SuccessRate { get; set; } = 0;
    }
}
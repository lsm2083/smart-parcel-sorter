// YoloDetectionService.cs

using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace MasterAdmin
{
    public class DefectResult
    {
        [JsonPropertyName("cam_id")]
        public string CamId { get; set; } = "";

        [JsonPropertyName("has_defect")]
        public bool HasDefect { get; set; }

        [JsonPropertyName("box_detected")]
        public bool BoxDetected { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        // 불량 bbox (paper_crack / paper_gap)
        [JsonPropertyName("defect_detections")]
        public DetectionItem[] Detections { get; set; } = Array.Empty<DetectionItem>();

        // 정상 박스 bbox (paper)
        [JsonPropertyName("box_detections")]
        public DetectionItem[] BoxDetections { get; set; } = Array.Empty<DetectionItem>();

        // 비닐 bbox (vinyl) — 서버가 별도 필드로 보냄
        [JsonPropertyName("vinyl_detections")]
        public DetectionItem[] VinylDetections { get; set; } = Array.Empty<DetectionItem>();
    }

    public class DetectionItem
    {
        [JsonPropertyName("class")]
        public string Class { get; set; } = "";

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("bbox")]
        public int[] Bbox { get; set; } = Array.Empty<int>();

        // 폴리곤(세그멘테이션) 외곽선 점 [[x,y],...] — ROI 크롭 좌표
        [JsonPropertyName("polygon")]
        public int[][] Polygon { get; set; } = Array.Empty<int[]>();
    }

    public class YoloDetectionService : IDisposable
    {
        private const string ServerUrl = "http://localhost:5050";
        private const int SendEveryNFrames = 1;    // 모든 프레임 후보 (실제 throttle은 in-flight 가드)
        private const double AlertCooldownSeconds = 0.5;  // 불량 콜백 간격 (WPF에서 패스당 1회로 dedupe)
        private const int JpegQuality = 75;

        // ★ 서버 연결 실패 시 재시도 대기 시간 (초)
        //   길면 한 번 실패에 검출이 오래 끊겨 한 박스가 여러 패스로 쪼개짐 → 짧게.
        private const double RetryAfterSeconds = 1.0;

        private readonly HttpClient _http;
        private int _frameCount = 0;
        private int _inFlightField = 0;     // 캠별 진행 중 /detect (백로그 방지, 캠끼리 안 막힘)
        private int _inFlightShipping = 0;
        private bool _disposed = false;
        private bool _serverAvailable = true;   // 서버 가용 여부
        private DateTime _nextRetryTime = DateTime.MinValue; // 다음 재시도 시각
        private int _consecutiveFailures = 0;   // 연속 실패 횟수 (200 성공 시 0으로 리셋)
        private const int FailuresBeforeBackoff = 2; // 이만큼 연속 실패해야 백오프(스킵) 진입 — 단발 실패는 무시

        private DateTime _lastAlertField = DateTime.MinValue;
        private DateTime _lastAlertShipping = DateTime.MinValue;

        // ── ROI 설정 (프레임 비율 0~1) — 캠별 독립 ─────────────────────────
        private double _shipRoiX, _shipRoiY, _shipRoiW, _shipRoiH;
        private double _fieldRoiX, _fieldRoiY, _fieldRoiW, _fieldRoiH;
        private bool _roiEnabled = false;

        public event Action<DefectResult>? OnDefectDetected;
        public event Action<DefectResult>? OnScanResult;   // 매 추론마다 (ROI2 스캔 위치용)

        // ★ 서버 상태가 바뀔 때 알려주는 이벤트 (UI에 표시용, 선택적)
        public event Action<bool>? OnServerStatusChanged;

        public YoloDetectionService()
        {
            _http = new HttpClient
            {
                // 추론이 다소 느려도(특히 Flask가 이미지 저장으로 바쁠 때) 타임아웃 안 나게 여유
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public void HandleFieldFrame(Mat frame)
            => _ = SendFrameAsync(frame.Clone(), "field");

        public void HandleShippingFrame(Mat frame)
            => _ = SendFrameAsync(frame.Clone(), "shipping");

        private async Task SendFrameAsync(Mat frame, string camId)
        {
            // N프레임마다 1번만 처리
            if (Interlocked.Increment(ref _frameCount) % SendEveryNFrames != 0)
            {
                frame.Dispose();
                return;
            }

            // ★ 서버가 꺼진 것으로 판단된 상태면 재시도 시각까지 완전히 스킵
            if (!_serverAvailable && DateTime.Now < _nextRetryTime)
            {
                frame.Dispose();
                return;
            }

            // ★ 같은 캠의 이전 추론이 진행 중이면 이 프레임은 건너뜀 (캠별 독립)
            bool isField = camId == "field";
            if (isField)
            {
                if (Interlocked.CompareExchange(ref _inFlightField, 1, 0) != 0) { frame.Dispose(); return; }
            }
            else
            {
                if (Interlocked.CompareExchange(ref _inFlightShipping, 1, 0) != 0) { frame.Dispose(); return; }
            }

            try
            {
                // ── ROI 적용: 출고캠이면 ROI 영역만 크롭해서 전송 ──────────
                //   비율 × 실제 프레임 크기 → 카메라 해상도가 달라도 정확히 일치
                Mat processFrame = frame;
                double rx, ry, rw, rh;
                if (isField) { rx = _fieldRoiX; ry = _fieldRoiY; rw = _fieldRoiW; rh = _fieldRoiH; }
                else         { rx = _shipRoiX;  ry = _shipRoiY;  rw = _shipRoiW;  rh = _shipRoiH; }

                if (_roiEnabled && rw > 0 && rh > 0)
                {
                    int fx = Math.Max(0, (int)(frame.Width  * rx));
                    int fy = Math.Max(0, (int)(frame.Height * ry));
                    int fw = Math.Min((int)(frame.Width  * rw), frame.Width  - fx);
                    int fh = Math.Min((int)(frame.Height * rh), frame.Height - fy);
                    if (fw > 20 && fh > 20)
                        processFrame = new Mat(frame, new OpenCvSharp.Rect(fx, fy, fw, fh));
                }

                var jpegBytes = processFrame.ToBytes(".jpg",
                    new ImageEncodingParam(ImwriteFlags.JpegQuality, JpegQuality));
                if (processFrame != frame) processFrame.Dispose();

                using var content = new ByteArrayContent(jpegBytes);
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                var response = await _http.PostAsync(
                    $"{ServerUrl}/detect?cam_id={camId}", content);

                if (!response.IsSuccessStatusCode)
                {
                    frame.Dispose();
                    return;
                }

                // ★ 200 성공 → 재시도/백오프 상태 즉시 해제 (성공했으면 절대 재시도 안 함)
                _consecutiveFailures = 0;
                _nextRetryTime = DateTime.MinValue;
                if (!_serverAvailable)
                {
                    _serverAvailable = true;
                    OnServerStatusChanged?.Invoke(true);
                    System.Diagnostics.Debug.WriteLine("[YOLO] 서버 재연결 성공 ✓");
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<DefectResult>(json);

                if (result == null) return;

                // 박스/비닐/불량 감지 시 OnScanResult 발생
                if (result.BoxDetected || result.HasDefect
                    || result.BoxDetections.Length > 0
                    || result.VinylDetections.Length > 0)
                    OnScanResult?.Invoke(result);

                if (!result.HasDefect) return;

                // 쿨다운 체크
                var now = DateTime.Now;
                if (camId == "field")
                {
                    if ((now - _lastAlertField).TotalSeconds < AlertCooldownSeconds) return;
                    _lastAlertField = now;
                }
                else
                {
                    if ((now - _lastAlertShipping).TotalSeconds < AlertCooldownSeconds) return;
                    _lastAlertShipping = now;
                }

                OnDefectDetected?.Invoke(result);
            }
            catch (HttpRequestException)
            {
                // 단발성 실패(간헐적 connection reset 등)는 무시 — 연속 실패가 쌓일 때만 백오프
                EnterBackoffIfRepeated("서버 연결 끊김");
            }
            catch (TaskCanceledException)
            {
                // 타임아웃도 동일하게 처리 (단발은 무시, 연속일 때만 백오프)
                EnterBackoffIfRepeated("타임아웃");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[YOLO] {camId} 오류: {ex.Message}");
            }
            finally
            {
                if (isField) Interlocked.Exchange(ref _inFlightField, 0);
                else Interlocked.Exchange(ref _inFlightShipping, 0);
                frame.Dispose();
            }
        }

        // 연속 실패가 임계치 이상일 때만 백오프(프레임 스킵) 진입.
        //   → 200이 정상적으로 나는 와중의 단발 실패로는 재시도 모드에 빠지지 않음.
        private void EnterBackoffIfRepeated(string reason)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures < FailuresBeforeBackoff)
                return;   // 아직 단발/소수 실패 — 다음 프레임 정상 시도

            if (_serverAvailable)
            {
                _serverAvailable = false;
                OnServerStatusChanged?.Invoke(false);
                System.Diagnostics.Debug.WriteLine($"[YOLO] {reason} — 잠시 후 재시도");
            }
            _nextRetryTime = DateTime.Now.AddSeconds(RetryAfterSeconds);
        }


        // ── ROI 설정 공개 메서드 ──────────────────────────────────────────

        /// <summary>캠별 ROI를 프레임 비율(0~1)로 설정. 크롭은 실제 프레임 크기에 맞춰 수행.</summary>
        public void SetRoiFrac(string cam, double x, double y, double w, double h)
        {
            if (cam == "field") { _fieldRoiX = x; _fieldRoiY = y; _fieldRoiW = w; _fieldRoiH = h; }
            else                { _shipRoiX = x;  _shipRoiY = y;  _shipRoiW = w;  _shipRoiH = h; }
            _roiEnabled = true;
            System.Diagnostics.Debug.WriteLine(
                $"[YOLO] {cam} ROI 비율 설정: x{x:F2} y{y:F2} w{w:F2} h{h:F2}");
        }

        public void ClearRoi()
        {
            _roiEnabled = false;
            _shipRoiW = _shipRoiH = _fieldRoiW = _fieldRoiH = 0;
            System.Diagnostics.Debug.WriteLine("[YOLO] ROI 초기화");
        }

        public async Task<bool> IsServerAliveAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{ServerUrl}/health");
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _http.Dispose();
            _disposed = true;
        }
    }
}
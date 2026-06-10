using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SocketIOClient;

//using SocketIO.Core;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Linq;

namespace MasterAdmin
{
    public class ApiService
    {
        private readonly HttpClient _http;
        private readonly SocketIO _socket;
        private bool _statusPollingStarted = false;
        private bool _statusPollingBusy = false;

        // 전체 탭 분류이력 복원 시 '최근' 행만 올린다. 폴링/초기로드가 DB 지난 100건을
        //   통째로 끌어와 새벽(예: 05시)의 옛 QR/OCR 행이 오후 테스트 화면에 끼어들던 문제 방지.
        //   (재시작 후 복원은 이 창 안의 최근 행으로 유지됨. 더 옛 이력은 날짜필터로만 본다.)
        //   값이 짧으면 휴식 후 재시작 시 직전 세션 행을 일부 잃을 수 있으니 넉넉히 둔다.
        private static readonly TimeSpan SortLogRestoreWindow = TimeSpan.FromHours(6);

        // 같은 박스 한 행으로 합치기: 같은 운송장 행이 연속 간격(SameBoxWindow) 이내로
        //   이어지면 동일 박스로 보고 한 행으로 합친다(다중 QR/OCR 스캔 + 정상→불량 박스검수
        //   분리로 한 박스가 여러 행이 되던 문제). 최종판정은 '불량 우선'(최종 ROI 반영).
        //   합치며 흡수한 DB행 키는 _absorbedKeys에 담아 폴링이 다시 끌어오지 못하게 막는다.
        private static readonly TimeSpan SameBoxWindow = TimeSpan.FromSeconds(20);
        private readonly HashSet<string> _absorbedKeys = new();

        public ApiService(string serverUrl)
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
                // 이미지 포함 multipart 전송이 오래 걸려도 타임아웃 안 나게 넉넉히
                Timeout = TimeSpan.FromSeconds(60)
            };

            // Flask-SocketIO 5.x = Engine.IO v4 → EIO=4로 맞춤
            _socket = new SocketIO(serverUrl, new SocketIOOptions { EIO = (SocketIOClient.EngineIO)4 });
            // 기존 코드가 Newtonsoft JObject를 쓰므로 직렬화기를 Newtonsoft로 고정
            _socket.JsonSerializer =
                new SocketIOClient.Newtonsoft.Json.NewtonsoftJsonSerializer();
        }

        // ── REST: 초기 데이터 로딩 ────────────────────────────────────────

        public async Task LoadInitialDataAsync(MainViewModel vm)
        {
            await Task.WhenAll(
                LoadStatusAsync(vm),
                LoadShippingLogsAsync(vm),
                LoadBlackboxEventsAsync(vm),
                LoadLoginRecordsAsync(vm),
                LoadCarStatusAsync(vm),
                LoadStatsAsync(vm)
            );

            // 분류이력은 순서가 중요하다: QR/OCR(성공·실패) 행을 먼저 채운 뒤
            //   박스검수를 매칭해야 전체 탭 행에 택배상태·최종판정이 복원된다.
            //   (동시 로드 시 HTTP가 1개뿐인 박스검수가 먼저 끝나 매칭 대상 QR/OCR 행이
            //    아직 없어 전체 탭 복원이 누락되던 문제 — WPF 재시작 후 전체 탭 미반영.)
            await LoadSortLogsAsync(vm);
            await LoadBoxLogsAsync(vm);
        }

        private async Task LoadStatusAsync(MainViewModel vm)
        {
            await RefreshStatusAsync(vm);
        }

        public async Task RefreshStatusAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/status");
                var d = JObject.Parse(json);

                Dispatch(() =>
                {
                    ApplyDeviceStatus(vm, d, "api/status");
                });
            }
            catch (Exception ex)
            {
                Log("RefreshStatus", ex);
                Dispatch(() =>
                {
                    vm.DeviceStatus.ConveyorStatus = "연결전";
                    vm.RefreshDeviceStatus();
                });
            }
        }

        public async Task LoadSortLogsAsync(MainViewModel vm)
        {
            try
            {
                // 성공 로그 가져오기
                var json = await _http.GetStringAsync("api/logs/sort");
                var logs = Deserialize<List<SortingLog>>(json) ?? new();

                // 실패 로그 가져오기
                var errorJson = await _http.GetStringAsync("api/logs/error");

                var obj = JObject.Parse(errorJson);

                var errorLogs =
                    obj["logs"]?.ToObject<List<ErrorLog>>() ?? new();

                // 성공 + 실패 로그 합치기
                List<SortingLog> mergedLogs = new();

                // 성공 로그 추가
                foreach (var l in logs)
                {
                    l.MergeKey = "S" + l.Id;
                    mergedLogs.Add(l);
                }

                // 실패 로그 추가
                foreach (var e in errorLogs)
                {
                    // 박스 외관 불량(paper_crack/paper_gap 등 YOLO 박스 클래스)이 OCR/QR 실패
                    //   메시지로 들어온 행은 전체 탭의 박스 앵커 행과 중복 → 분류 이력에 띄우지 않음.
                    var msg = e.Message ?? "";
                    if (msg.IndexOf("paper_", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    mergedLogs.Add(new SortingLog
                    {
                        MergeKey = "E" + e.Id,
                        Id = e.Id,

                        // 실시간으로 이미 전체 탭 앵커에 병합된 인식실패 행과 같은 건인지
                        //   매칭하기 위한 package_id (운송장이 비어 운송장 매칭이 불가하므로).
                        PackageId = e.PackageId,

                        Timestamp = e.CreatedAt,

                        TrackingNumber = $"FAIL-{e.PackageId}",

                        RecognitionType =
                            e.ErrorCode != null &&
                            e.ErrorCode.Contains("QR")
                            ? "QR"
                            : "OCR",

                        Region = "-",

                        Status = "불량",

                        // 오류유형: 메시지 우선, 비어 있으면 에러코드, 그것도 없으면 인식실패 라벨.
                        //   (QR/OCR 불량인데 message가 비어 전체 탭 오류유형이 공란이던 문제 보완)
                        ErrorType = !string.IsNullOrWhiteSpace(e.Message) ? e.Message!
                                  : !string.IsNullOrWhiteSpace(e.ErrorCode) ? e.ErrorCode!
                                  : (e.ErrorCode != null && e.ErrorCode.Contains("QR")
                                        ? "QR 인식 실패" : "OCR 인식 실패"),

                        ProcessingTime = 0,

                        Confidence = 0,

                        // 상대경로(/storage/qr_fail/..)면 절대 URL로 변환 → 미리보기 로드 가능
                        ImagePath = ToAbsoluteImageUrl(e.ImagePath)
                    });
                }

                // 최근(SortLogRestoreWindow 이내) 행만 + 시간 기준 최신순 정렬.
                //   옛 새벽 로그가 현재 테스트 화면에 끼어드는 것 방지.
                //   라이브 소켓 행은 이 경로를 안 거치므로 영향 없음.
                var sortLogCutoff = DateTime.Now - SortLogRestoreWindow;
                mergedLogs = mergedLogs
                    .Where(x => x.Timestamp >= sortLogCutoff)
                    .OrderByDescending(x => x.Timestamp)
                    .ToList();

                // 화면 반영 — 증분 병합(Clear 금지)
                //  · 로컬(WPF 생성) 행은 보존 → 떴다가 사라지는 현상 방지
                //  · 이미 있는 DB 행은 건너뜀 → 전체 재생성 깜빡임 방지
                //  · 신규 DB 행만 시간순 위치에 삽입
                Dispatch(() =>
                {
                    // 과거 이력 일괄 삽입 중 — FieldPage가 이 행들을 라이브 박스 앵커에
                    //   병합하거나 _currentTrackingNumber를 옛 운송장으로 덮어쓰지 않게 막는다.
                    vm.IsHistoryLoading = true;
                    try
                    {
                    var existingKeys = new HashSet<string>(
                        vm.SortingLogs
                          .Where(x => !x.IsLocal && x.MergeKey != null)
                          .Select(x => x.MergeKey!));

                    foreach (var log in mergedLogs)
                    {
                        // 같은-박스 합치기로 이미 흡수된 DB행은 다시 넣지 않는다(중복 재출현 방지).
                        if (log.MergeKey != null && _absorbedKeys.Contains(log.MergeKey))
                            continue;

                        if (log.MergeKey != null && existingKeys.Contains(log.MergeKey))
                        {
                            // 이미 있는 행이라도, 라이브 직후 비어 있던 이미지가 서버에 늦게
                            //   저장됐으면 폴링 때 채워준다(이미지가 페이지 재진입 전까지 안 뜨던 문제).
                            var existing = vm.SortingLogs.FirstOrDefault(x => x.MergeKey == log.MergeKey);
                            if (existing != null && string.IsNullOrEmpty(existing.ImagePath)
                                && !string.IsNullOrEmpty(log.ImagePath))
                                existing.ImagePath = log.ImagePath;
                            continue;
                        }

                        // 인식실패(오류로그) 행이 이미 라이브 때 전체 탭 앵커에 병합돼 있으면
                        //   (같은 package_id) → 새 'FAIL-..' 행을 또 넣지 않고 앵커가 흡수한다.
                        //   앵커는 운송장 비움(불량)으로 유지하고, 폴링 키·이미지만 승계.
                        if (log.MergeKey != null && log.MergeKey.StartsWith("E") && log.PackageId != 0)
                        {
                            var merged = vm.SortingLogs.FirstOrDefault(
                                x => x.PackageId == log.PackageId && x.IsBoxAnchor);
                            if (merged != null)
                            {
                                merged.MergeKey = log.MergeKey;   // 이후 폴링 중복방지
                                if (string.IsNullOrEmpty(merged.ImagePath)
                                    && !string.IsNullOrEmpty(log.ImagePath))
                                    merged.ImagePath = log.ImagePath;
                                existingKeys.Add(log.MergeKey);
                                continue;
                            }
                        }

                        int idx = 0;
                        while (idx < vm.SortingLogs.Count &&
                               vm.SortingLogs[idx].Timestamp > log.Timestamp)
                            idx++;
                        vm.SortingLogs.Insert(idx, log);

                        if (log.MergeKey != null)
                            existingKeys.Add(log.MergeKey);
                    }
                    }
                    finally { vm.IsHistoryLoading = false; }

                    // 같은 운송장이 여러 행(다중 스캔/정상→불량 분리)으로 들어온 경우 한 행으로 합침
                    CollapseSameBoxRows(vm);
                });
            }
            catch (Exception ex)
            {
                Log("LoadSortLogs", ex);
            }
        }

        // ── 택배상태(박스검수) 로그 로드 — box_inspections 조회 → BoxStatus·이미지 복원 ─
        //   새로고침/재시작해도 택배상태가 유지되도록 폴링·초기로드 때 호출.
        //   라이브 세션에 떠 있는 로컬 박스행은 흡수(중복 방지)한다.
        public async Task LoadBoxLogsAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/logs/box");
                var obj = JObject.Parse(json);
                var boxLogs = obj["logs"]?.ToObject<List<BoxLog>>() ?? new();

                Dispatch(() =>
                {
                    var existingKeys = new HashSet<string>(
                        vm.SortingLogs
                          .Where(x => x.MergeKey != null)
                          .Select(x => x.MergeKey!));

                    // 한 QR/OCR 행에는 박스검수 1건만 덧입힌다(가장 최근). 조회가 created_at DESC라
                    //   먼저 만나는 게 최신 → 한 번 채운 행은 usedQrRows로 잠가 옛 검수가 덮어쓰지 못하게.
                    //   (정상인데 과거 불량검수의 오류유형/이미지가 남던 문제 방지)
                    var usedQrRows = new HashSet<SortingLog>();

                    foreach (var b in boxLogs)
                    {
                        string key = "B" + b.Id;
                        string? img = ToAbsoluteImageUrl(
                            !string.IsNullOrEmpty(b.ImageUrl) ? b.ImageUrl : b.ImagePath);

                        // ── 전체 탭 복원: 같은 박스 패스의 QR/OCR 행에 택배상태·최종판정을 덧입힌다 ──
                        //   1순위: 운송장 일치(+시간 근접). 같은 운송장이라도 시간이 멀리 떨어진
                        //          (과거 테스트·재사용 QR) 검수는 제외 → 옛 불량검수로 오염되는 것 방지.
                        SortingLog? qrRow = null;
                        if (!string.IsNullOrEmpty(b.InvoiceNo))
                        {
                            qrRow = vm.SortingLogs.FirstOrDefault(x =>
                                x.TrackingNumber == b.InvoiceNo &&
                                (x.RecognitionType == "QR" || x.RecognitionType == "OCR") &&
                                !usedQrRows.Contains(x) &&
                                (b.CreatedAt - x.Timestamp).Duration() <= TimeSpan.FromSeconds(90));
                        }
                        //   2순위: 운송장 매칭 실패 시(박스가 OCR 인식 전에 이탈해 invoice_no가
                        //          임시번호 'BOX-..'로 저장된 경우) — 아직 택배상태가 안 채워진
                        //          QR/OCR 행 중 시간이 가장 가까운 행에 매칭한다. 이게 빠지면
                        //          재시작 후 전체 탭에서 택배상태·최종판정이 비어 보였다.
                        if (qrRow == null)
                        {
                            qrRow = vm.SortingLogs
                                .Where(x => (x.RecognitionType == "QR" || x.RecognitionType == "OCR")
                                            && !usedQrRows.Contains(x)
                                            && (string.IsNullOrEmpty(x.BoxStatus) || x.BoxStatus == "대기중")
                                            && (b.CreatedAt - x.Timestamp).Duration() <= TimeSpan.FromSeconds(90))
                                .OrderBy(x => (b.CreatedAt - x.Timestamp).Duration())
                                .FirstOrDefault();
                        }

                        if (qrRow != null)
                        {
                            usedQrRows.Add(qrRow);

                            string qrFinal = ComputeFinalResult(qrRow.Status, b.BoxStatus);
                            if (qrRow.BoxStatus != b.BoxStatus) qrRow.BoxStatus = b.BoxStatus;
                            if (qrRow.FinalResult != qrFinal) qrRow.FinalResult = qrFinal;

                            // 최종판정이 불량일 때만 오류유형·이미지 표시. 정상/비닐이면 둘 다 비운다.
                            //   (정상인데 과거 불량검수의 paper_crack·이미지가 남던 문제 방지)
                            if (qrFinal == "불량")
                            {
                                // QR/OCR 불량 + 택배상태 불량 → 오류유형·이미지를 QR/OCR 우선.
                                //   QR/OCR이 이미 가진 값(인식실패 사유·캡처 이미지)을 박스 검수값으로
                                //   덮어쓰지 않고, QR/OCR 쪽이 비어 있을 때만 박스 값으로 보완한다.
                                if (qrRow.Status == "불량" && b.BoxStatus == "불량")
                                {
                                    if (string.IsNullOrEmpty(qrRow.ErrorType) && !string.IsNullOrEmpty(b.DefectClass))
                                        qrRow.ErrorType = b.DefectClass!;
                                    if (string.IsNullOrEmpty(qrRow.ImagePath) && !string.IsNullOrEmpty(img))
                                        qrRow.ImagePath = img;
                                }
                                else
                                {
                                    if (!string.IsNullOrEmpty(b.DefectClass) && qrRow.ErrorType != b.DefectClass)
                                        qrRow.ErrorType = b.DefectClass!;
                                    if (string.IsNullOrEmpty(qrRow.ImagePath) && !string.IsNullOrEmpty(img))
                                        qrRow.ImagePath = img;
                                }
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(qrRow.ErrorType)) qrRow.ErrorType = "";
                                if (!string.IsNullOrEmpty(qrRow.ImagePath)) qrRow.ImagePath = "";
                            }
                        }

                        // ── 택배상태 탭 복원: 박스 단독 로그 (전체에선 isBoxOnly로 숨겨짐) ──
                        if (existingKeys.Contains(key)) continue;

                        // 라이브 세션에서 떠 있는 로컬 박스 단독행 흡수(중복 방지)
                        //   앵커 행(IsBoxAnchor)은 전체 탭 행이므로 흡수 대상에서 제외.
                        var local = vm.SortingLogs.FirstOrDefault(x =>
                            x.IsLocal && x.MergeKey == null && !x.IsBoxAnchor
                            && string.IsNullOrEmpty(x.RecognitionType)
                            && x.BoxStatus == b.BoxStatus
                            && (b.CreatedAt - x.Timestamp).Duration() < TimeSpan.FromSeconds(20));
                        if (local != null)
                        {
                            local.MergeKey = key;
                            if (string.IsNullOrEmpty(local.ImagePath) && !string.IsNullOrEmpty(img))
                                local.ImagePath = img;
                            existingKeys.Add(key);
                            continue;
                        }

                        var row = new SortingLog
                        {
                            MergeKey = key,
                            Id = b.Id,
                            Timestamp = b.CreatedAt,
                            TrackingNumber = b.InvoiceNo ?? "",
                            RecognitionType = "",
                            Region = "",
                            Status = "",
                            BoxStatus = b.BoxStatus,
                            FinalResult = b.BoxStatus,
                            ErrorType = b.DefectClass ?? "",
                            ProcessingTime = 0,
                            Confidence = b.Confidence,
                            ImagePath = img
                        };

                        int idx = 0;
                        while (idx < vm.SortingLogs.Count &&
                               vm.SortingLogs[idx].Timestamp > row.Timestamp)
                            idx++;
                        vm.SortingLogs.Insert(idx, row);
                        existingKeys.Add(key);
                    }

                    // 박스검수 반영 후, 같은 운송장 행을 한 행으로 합침(최종 불량 우선)
                    CollapseSameBoxRows(vm);
                });
            }
            catch (Exception ex)
            {
                Log("LoadBoxLogs", ex);
            }
        }

        // 같은 박스 한 행으로 합치기.
        //   같은 운송장(QR/OCR 인식행)끼리 시간순으로 늘어놓고, 연속 간격이 SameBoxWindow
        //   이내로 이어지는 묶음은 동일 박스로 보아 가장 이른 행 하나만 남기고 합친다.
        //   최종판정은 '불량 우선'(어느 행이든 불량이면 대표행을 불량으로 = 최종 ROI 반영).
        //   ※ 택배상태 단독 행(RecognitionType 빈 값)·앵커 임시행(FAIL-/BOX-)은 제외 → 택배상태 탭 보존.
        private void CollapseSameBoxRows(MainViewModel vm)
        {
            var groups = vm.SortingLogs
                .Where(r => (r.RecognitionType == "QR" || r.RecognitionType == "OCR")
                            && !string.IsNullOrWhiteSpace(r.TrackingNumber)
                            && !r.TrackingNumber.StartsWith("FAIL-")
                            && !r.TrackingNumber.StartsWith("BOX-"))
                .GroupBy(r => r.TrackingNumber)
                .ToList();

            foreach (var grp in groups)
            {
                var rows = grp.OrderBy(r => r.Timestamp).ToList();
                if (rows.Count < 2) continue;

                int i = 0;
                while (i < rows.Count)
                {
                    var keep = rows[i];
                    int j = i + 1;
                    // 연속 간격이 window 이내인 동안 같은 박스로 묶는다(체이닝).
                    while (j < rows.Count &&
                           (rows[j].Timestamp - rows[j - 1].Timestamp) <= SameBoxWindow)
                    {
                        var dup = rows[j];

                        // 불량 우선: 어느 행이든 불량이면 대표행을 불량으로.
                        if (dup.Status == "불량") keep.Status = "불량";
                        if (dup.BoxStatus == "불량") keep.BoxStatus = "불량";
                        if (string.IsNullOrEmpty(keep.ErrorType) && !string.IsNullOrEmpty(dup.ErrorType))
                            keep.ErrorType = dup.ErrorType;
                        if (string.IsNullOrEmpty(keep.ImagePath) && !string.IsNullOrEmpty(dup.ImagePath))
                            keep.ImagePath = dup.ImagePath;

                        if (!string.IsNullOrEmpty(dup.MergeKey))
                            _absorbedKeys.Add(dup.MergeKey!);   // 폴링 재삽입 방지

                        vm.SortingLogs.Remove(dup);
                        j++;
                    }

                    if (j > i + 1)
                        keep.FinalResult = ComputeFinalResult(keep.Status, keep.BoxStatus);

                    i = j;
                }
            }
        }

        // 교차검증 최종판정 (FieldPage.ComputeFinalResult와 동일 규칙):
        //   ① QR/OCR 인식 결과(정상/불량) 없으면 → 대기중.
        //   ② QR/OCR 불량 → 불량.  ③ QR/OCR 정상 → 박스 확정 불량일 때만 불량, 그 외 정상.
        private static string ComputeFinalResult(string? ocrStatus, string boxStatus)
        {
            if (ocrStatus != "정상" && ocrStatus != "불량") return "대기중";
            if (ocrStatus == "불량") return "불량";
            return boxStatus == "불량" ? "불량" : "정상";
        }

        private async Task LoadShippingLogsAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/logs/shipping");
                var logs = Deserialize<List<ShippingLog>>(json) ?? new();

                Dispatch(() =>
                {
                    vm.ShippingLogs.Clear();

                    foreach (var l in logs)
                        vm.ShippingLogs.Add(l);
                });
            }
            catch (Exception ex)
            {
                Log("LoadShippingLogs", ex);
            }
        }

        private async Task LoadBlackboxEventsAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/blackbox/events");
                var events = Deserialize<List<BlackboxEvent>>(json) ?? new();

                Dispatch(() =>
                {
                    vm.BlackboxEvents.Clear();

                    foreach (var e in events)
                        vm.BlackboxEvents.Add(e);
                });
            }
            catch (Exception ex)
            {
                Log("LoadBlackboxEvents", ex);
            }
        }

        private async Task LoadLoginRecordsAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/logs/login");
                var records = Deserialize<List<LoginRecord>>(json) ?? new();

                Dispatch(() =>
                {
                    vm.LoginRecords.Clear();

                    foreach (var r in records)
                        vm.LoginRecords.Add(r);
                });
            }
            catch (Exception ex)
            {
                Log("LoadLoginRecords", ex);
            }
        }


        // ── REST: 통계(총괄 페이지 차트) ──────────────────────────────────
        //   건수만 받아 막대/도넛 계산은 vm.ApplyStats에서 수행.
        //   · 오늘만        : api/stats/overview        ← 상단 카드와 기간을 맞추기 위해 오늘 기준 사용
        //   · 전체 누적(발표용): api/stats/overview/all
        private const string StatsEndpoint = "api/stats/overview";

        private bool _statsBusy = false;
        public async Task LoadStatsAsync(MainViewModel vm)
        {
            // 폴링 + 소켓 이벤트가 겹쳐 집계 쿼리가 중복 호출되는 것 방지
            if (_statsBusy) return;
            _statsBusy = true;
            try
            {
                var json = await _http.GetStringAsync(StatsEndpoint);
                var stats = Deserialize<StatsOverview>(json);
                if (stats == null) return;

                Dispatch(() => vm.ApplyStats(stats));
            }
            catch (Exception ex)
            {
                Log("LoadStats", ex);
            }
            finally { _statsBusy = false; }
        }

        // ── REST: 아두이노 자동차 상태 ────────────────────────────────────
        public async Task LoadCarStatusAsync(MainViewModel vm)
        {
            try
            {
                var json = await _http.GetStringAsync("api/cars/status");
                var cars = Deserialize<List<CarStatus>>(json) ?? new();

                Dispatch(() =>
                {
                    foreach (var incoming in cars)
                    {
                        var existing = vm.Cars.FirstOrDefault(c => c.CarId == incoming.CarId);
                        if (existing != null)
                        {
                            existing.Status = incoming.Status;
                            existing.FilledSlots = incoming.FilledSlots;
                            existing.TotalSlots = incoming.TotalSlots;
                            existing.LastUpdated = incoming.LastUpdated;
                        }
                        else
                        {
                            vm.Cars.Add(incoming);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log("LoadCarStatus", ex);
            }
        }

        // ── REST: 컨베이어 제어 ───────────────────────────────────────────

        public async Task<bool> ConveyorStartAsync(int speed = 180)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[컨베이어] 시작 명령 전송...");

                string json = "{\"command\":\"CONVEYOR_START\",\"speed\":" + speed + "}";
                var body = new StringContent(json, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/conveyor/command", body);

                System.Diagnostics.Debug.WriteLine("[컨베이어] 응답: " + res.StatusCode);

                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("ConveyorStart", ex);
                return false;
            }
        }

        public async Task<bool> ConveyorStopAsync()
        {
            try
            {
                string json = "{\"command\":\"CONVEYOR_STOP\"}";
                var body = new StringContent(json, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/conveyor/command", body);

                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("ConveyorStop", ex);
                return false;
            }
        }

        public async Task<bool> ConveyorResumeAsync()
        {
            try
            {
                string json = "{\"command\":\"CONVEYOR_START\",\"speed\":180}";
                var body = new StringContent(json, Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/conveyor/command", body);

                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("ConveyorResume", ex);
                return false;
            }
        }

        // ── REST: 비상정지 ────────────────────────────────────────────────

        // 비상정지 발동 → Flask가 전체(로봇/컨베이어/AGV) 정지 + 앱 푸시 + WebSocket 알림 일괄 처리.
        //   (기존: parcel/robot/command MQTT 직접 발행 → 로봇만 멈추고 Flask 미경유라 폐기)
        public async Task<bool> EmergencyStopAsync()
        {
            try
            {
                var body = new StringContent("{\"source\":\"WPF\"}", Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/emergency/stop", body);

                System.Diagnostics.Debug.WriteLine($"[HTTP] 비상정지 발동 → HTTP {(int)res.StatusCode}");
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("EmergencyStop", ex);
                return false;
            }
        }

        // 비상정지 해제 → Flask가 전체 재개 브로드캐스트.
        public async Task<bool> EmergencyResetAsync()
        {
            try
            {
                var body = new StringContent("{\"source\":\"WPF\"}", Encoding.UTF8, "application/json");
                var res = await _http.PostAsync("api/emergency/reset", body);

                System.Diagnostics.Debug.WriteLine($"[HTTP] 비상정지 해제 → HTTP {(int)res.StatusCode}");
                return res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log("EmergencyReset", ex);
                return false;
            }
        }

        // ── WebSocket: 실시간 이벤트 구독 ────────────────────────────────

        public void StartRealtimeEvents(MainViewModel vm)
        {
            _socket.OnConnected += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[Flask] WebSocket 연결 성공!");
                SockLog("[연결] WebSocket 연결 성공");

                Dispatch(() =>
                {
                    DebugWindow.Instance.SetConnected(true);
                    vm.DeviceStatus.ConveyorStatus = "연결됨";
                    vm.RefreshDeviceStatus();
                });
            };

            _socket.OnDisconnected += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[Flask] WebSocket 연결 끊김");

                Dispatch(() =>
                {
                    DebugWindow.Instance.SetConnected(false);
                    vm.DeviceStatus.ConveyorStatus = "연결전";
                    vm.DeviceStatus.ConveyorSpeed = 0;
                    vm.RefreshDeviceStatus();
                });
            };

            RegisterDeviceStatusEvent(vm, "device_status");
            RegisterDeviceStatusEvent(vm, "conveyor_status");
            RegisterDeviceStatusEvent(vm, "conveyor_speed");
            RegisterDeviceStatusEvent(vm, "status_update");
            RegisterDeviceStatusEvent(vm, "command_log_added");
            RegisterDeviceStatusEvent(vm, "command_added");
            RegisterDeviceStatusEvent(vm, "conveyor_command");
            RegisterDeviceStatusEvent(vm, "conveyor_command_added");

            _socket.On("sorting_log_added", resp =>
            {
                try
                {
                    var log = resp.GetValue<SortingLog>(0);

                    // 라이브 emit엔 timestamp가 없어 Timestamp가 기본값(0001-01-01)이 된다.
                    //   그대로 두면 박스 앵커에 병합되지 못한 단독 QR/OCR 행이 '오늘' 날짜필터에
                    //   걸려 분류이력에 안 보인다(인식했는데 안 뜨는 현상). 수신 시각(현재)으로 채워
                    //   오늘 행으로 표시되게 한다. (앵커에 병합되면 앵커의 시각을 그대로 쓰므로 무관)
                    if (log.Timestamp == default || log.Timestamp.Year < 2000)
                        log.Timestamp = DateTime.Now;

                    // Flask가 함께 보낸 이미지 URL 추출 (필드명 변형 모두 허용)
                    try
                    {
                        var raw = resp.GetValue<JObject>(0);
                        SockLog("[sorting_log_added] payload=" + raw.ToString(Formatting.None));
                        var url = ExtractImageUrl(raw);
                        if (!string.IsNullOrEmpty(url)) log.ImagePath = url;
                    }
                    catch { }

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("sorting_log_added",
                            $"운송장:{log.TrackingNumber} 상태:{log.Status} 이미지:{log.ImagePath ?? "없음"}");

                        if (log.Status == "불량")
                            vm.TriggerRecording?.Invoke(log.ErrorType ?? "인식실패");

                        // DB 새로고침 시 같은 행이 중복 추가되지 않도록 키 부여
                        log.MergeKey = "S" + log.Id;

                        // 폴링(api/logs/sort)이 라이브 emit보다 먼저 도착해 같은 행을
                        //   이미 넣었거나 앵커에 병합한 경우, 같은 키로 또 넣지 않는다.
                        //   (id가 실제값(≠0)인 정상 행에만 적용 — 불량 행은 id=0이라 무관)
                        if (log.Id != 0 &&
                            vm.SortingLogs.Any(x => x.MergeKey == log.MergeKey))
                            return;

                        vm.SortingLogs.Insert(0, log);

                        if (vm.SortingLogs.Count > 60)
                            vm.SortingLogs.RemoveAt(vm.SortingLogs.Count - 1);
                    });

                    // 새 분류 로그 → 통계(총 처리/오류 건수) 즉시 갱신 (폴링 대기 없이 실시간)
                    _ = LoadStatsAsync(vm);
                }
                catch (Exception ex)
                {
                    Log("sorting_log_added", ex);
                }
            });

            _socket.On("shipping_log_added", resp =>
            {
                try
                {
                    var log = resp.GetValue<ShippingLog>(0);

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("shipping_log_added", $"운송장:{log.TrackingNumber}");

                        vm.ShippingLogs.Insert(0, log);

                        if (vm.ShippingLogs.Count > 40)
                            vm.ShippingLogs.RemoveAt(vm.ShippingLogs.Count - 1);
                    });
                }
                catch (Exception ex)
                {
                    Log("shipping_log_added", ex);
                }
            });

            _socket.On("blackbox_event_added", resp =>
            {
                System.Diagnostics.Debug.WriteLine("★★★ blackbox_event_added 수신됨 ★★★");
                SockLog("[blackbox_event_added] ★ 수신됨 ★ payload=" + resp.ToString());
                // 디버거 없이도 보이도록 앱 내 로그창에도 즉시 기록(수신 여부 확인용)
                Dispatch(() => DebugWindow.Instance.AddLog(
                    "blackbox_event_added", "★ 수신됨 ★ " + resp.ToString()));
                try
                {
                    var raw = resp.GetValue<JObject>(0);

                    // 이미지 URL 추출 (필드명 변형 모두 허용)
                    string? fullImageUrl = ExtractImageUrl(raw);

                    string? trackingNo = raw["tracking_number"]?.ToString()
                                      ?? raw["invoice_no"]?.ToString();

                    var ev = raw.ToObject<BlackboxEvent>() ?? new BlackboxEvent();

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("blackbox_event_added",
                            $"{ev.Description ?? ""} | {trackingNo} | {fullImageUrl ?? "이미지없음"}");

                        // ── 이미지 부착 (불량 행에만) ────────────────────────
                        if (!string.IsNullOrEmpty(fullImageUrl))
                        {
                            SortingLog? target = null;

                            // 1) 운송장번호로 매칭 (있으면)
                            if (!string.IsNullOrEmpty(trackingNo))
                                target = vm.SortingLogs.FirstOrDefault(
                                    l => l.TrackingNumber == trackingNo && IsDefectRow(l));

                            // 2) 운송장 없거나 못 찾으면 → 이미지 없는 가장 최근 불량 행
                            //    (Vision/SCAN_FAILED는 운송장이 비어 매칭이 안 되므로)
                            if (target == null)
                                target = vm.SortingLogs.FirstOrDefault(
                                    l => IsDefectRow(l) && string.IsNullOrEmpty(l.ImagePath));

                            if (target != null)
                            {
                                // QR/OCR가 불량인 행은 이미 QR/OCR 캡처 이미지를 갖고 있다.
                                //   요청: QR/OCR 불량 + 택배상태 불량이면 이미지는 QR/OCR 이미지로 띄운다
                                //   → 박스 검출 이미지로 덮어쓰지 않고 QR/OCR 이미지를 유지한다.
                                //   (QR/OCR 이미지가 비어 있을 때만 박스 이미지로 보완)
                                bool qrFailKeepsImage = target.Status == "불량"
                                    && !string.IsNullOrEmpty(target.ImagePath);
                                if (qrFailKeepsImage)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[이미지] QR/OCR 불량 이미지 유지 → {target.TrackingNumber} (박스 이미지 무시)");
                                }
                                else
                                {
                                    target.ImagePath = fullImageUrl;
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[이미지] → {target.TrackingNumber}/{target.Status} : {fullImageUrl}");
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[이미지] 붙일 불량 행 없음 — {trackingNo}");
                            }
                        }

                        // BlackboxEvents 목록에도 추가
                        vm.BlackboxEvents.Insert(0, ev);
                        if (vm.BlackboxEvents.Count > 100)
                            vm.BlackboxEvents.RemoveAt(vm.BlackboxEvents.Count - 1);
                    });
                }
                catch (Exception ex)
                {
                    Log("blackbox_event_added", ex);
                }
            });

            _socket.On("emergency_stop", resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>(0);
                    bool isEmergency = GetBool(d, false, "isEmergency", "is_emergency", "emergencyStop", "emergency_stop");

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("emergency_stop", $"isEmergency:{isEmergency}");
                        vm.IsEmergencyStop = isEmergency;
                    });
                }
                catch (Exception ex)
                {
                    Log("emergency_stop", ex);
                }
            });

            _socket.On("physical_estop", resp =>
            {
                try
                {
                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("physical_estop", "민지 비상정지 버튼 눌림");
                        vm.IsEmergencyStop = true;
                        vm.DeviceStatus.ConveyorStatus = "비상정지";
                        vm.DeviceStatus.ConveyorSpeed = 0;
                        vm.RefreshDeviceStatus();
                    });
                }
                catch (Exception ex)
                {
                    Log("physical_estop", ex);
                }
            });

            _socket.On("estop_released", resp =>
            {
                try
                {
                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("estop_released", "민지 비상정지 버튼 풀림");
                        vm.IsEmergencyStop = false;
                        vm.DeviceStatus.ConveyorStatus = "작동중";
                        vm.RefreshDeviceStatus();
                    });
                }
                catch (Exception ex)
                {
                    Log("estop_released", ex);
                }
            });

            _socket.On("device_connected", resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>(0);

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("device_connected", d["device_id"]?.ToString() ?? "");
                    });

                    HandleDeviceChange(vm, d, "작동중");
                }
                catch (Exception ex)
                {
                    Log("device_connected", ex);
                }
            });

            _socket.On("device_disconnected", resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>(0);

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("device_disconnected", d["device_id"]?.ToString() ?? "");
                    });

                    HandleDeviceChange(vm, d, "오프라인");
                }
                catch (Exception ex)
                {
                    Log("device_disconnected", ex);
                }
            });

            // ── defect_inspected: Flask가 YOLO 검사 완료 후 결과+이미지 전달 ──
            // Flask가 QR/OCR 캠 이미지를 저장하고 URL을 여기로 보내줌
            _socket.On("defect_inspected", resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>(0);
                    SockLog("[defect_inspected] payload=" + d.ToString(Formatting.None));

                    string? trackingNo = d["tracking_number"]?.ToString()
                                       ?? d["invoice_no"]?.ToString();
                    string? yoloResult = d["yolo_result"]?.ToString();    // "정상" | "불량"
                    string? defectClass = d["defect_class"]?.ToString();

                    // 이미지 URL 추출 (필드명 변형 모두 허용)
                    string? fullImageUrl = ExtractImageUrl(d);

                    Dispatch(() =>
                    {
                        DebugWindow.Instance.AddLog("defect_inspected",
                            $"운송장:{trackingNo} | 결과:{yoloResult} | 불량:{defectClass} | 이미지:{fullImageUrl ?? "없음"}");

                        if (string.IsNullOrEmpty(trackingNo)) return;

                        // SortingLog에서 해당 운송장번호 행 찾아서 업데이트
                        var target = vm.SortingLogs
                            .FirstOrDefault(l => l.TrackingNumber == trackingNo);

                        if (target != null)
                        {
                            // Flask 판정 결과로 BoxStatus/FinalResult 갱신
                            if (!string.IsNullOrEmpty(yoloResult))
                            {
                                target.BoxStatus = yoloResult;
                                target.FinalResult = yoloResult;
                            }
                            // 최종판정이 불량일 때만 오류유형·이미지 표시. 정상이면 둘 다 비운다.
                            //   (정상인데 과거 불량검수의 오류유형/이미지가 남던 문제 방지)
                            bool isDefect = yoloResult == "불량" || IsDefectRow(target);
                            if (isDefect)
                            {
                                if (!string.IsNullOrEmpty(defectClass))
                                    target.ErrorType = defectClass;
                                // QR/OCR 불량 + 택배상태 불량이면 이미지는 QR/OCR 이미지로 유지
                                //   (박스 검출 이미지로 덮어쓰지 않음). QR/OCR 이미지가 비었을 때만 보완.
                                bool qrFailKeepsImage = target.Status == "불량"
                                    && !string.IsNullOrEmpty(target.ImagePath);
                                if (!qrFailKeepsImage && !string.IsNullOrEmpty(fullImageUrl))
                                {
                                    target.ImagePath = fullImageUrl;
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[defect_inspected] 이미지 저장 → {trackingNo} : {fullImageUrl}");
                                }
                            }
                            else
                            {
                                // 정상 → 오류유형·이미지 모두 비움
                                target.ErrorType = "";
                                target.ImagePath = "";
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[defect_inspected] SortingLog 없음 → {trackingNo}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log("defect_inspected", ex);
                }
            });

            // 소켓 에러 로깅 (핸드셰이크/프로토콜 실패 등을 드러냄)
            _socket.OnError += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[Flask] WebSocket 오류: {e}");
                SockLog($"[오류] {e}");
            };

            SockLog($"[시작] ConnectAsync 시도 → {_http.BaseAddress}  (EIO4)");

            // 연결을 fire-and-forget 하지 말고 실패를 로그로 드러냄
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await _socket.ConnectAsync();
                }
                catch (Exception ex)
                {
                    SockLog($"[연결실패] {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[Flask] WebSocket 연결 실패: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine(
                        "  → SocketIOClient(2.0.2.5, EIO3) ↔ Flask-SocketIO 버전 불일치 가능성. " +
                        "Flask가 python-socketio 5.x(EIO4)면 클라이언트 업그레이드 필요.");
                }
            });

            StartStatusPolling(vm);
        }

        private void RegisterDeviceStatusEvent(MainViewModel vm, string eventName)
        {
            _socket.On(eventName, resp =>
            {
                try
                {
                    var d = resp.GetValue<JObject>(0);

                    Dispatch(() =>
                    {
                        ApplyDeviceStatus(vm, d, eventName);
                    });
                }
                catch (Exception ex)
                {
                    Log(eventName, ex);
                }
            });
        }

        private void StartStatusPolling(MainViewModel vm)
        {
            if (_statusPollingStarted)
                return;

            _statusPollingStarted = true;

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (!_statusPollingBusy)
                        {
                            _statusPollingBusy = true;

                            try
                            {
                                await RefreshStatusAsync(vm);
                            }
                            finally
                            {
                                _statusPollingBusy = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("StatusPolling", ex);
                    }

                    await Task.Delay(1000);
                }
            });
        }

        private static void ApplyDeviceStatus(MainViewModel vm, JObject d, string source)
        {
            DebugWindow.Instance.AddLog(source, d.ToString(Formatting.None));

            JObject normalized = NormalizePayloadObject(d);

            if (TryGetDouble(normalized, out double conveyorSpeed,
                "conveyorSpeed",
                "conveyor_speed",
                "speed",
                "Speed",
                "currentSpeed",
                "current_speed",
                "pwm",
                "PWM",
                "motorSpeed",
                "motor_speed"))
            {
                vm.DeviceStatus.ConveyorSpeed = conveyorSpeed;
                DebugWindow.Instance.AddLog("conveyorSpeed", conveyorSpeed.ToString(CultureInfo.InvariantCulture));
            }

            string command = GetString(normalized, "", "command", "Command");

            if (command == "CONVEYOR_START")
            {
                vm.DeviceStatus.ConveyorStatus = "동작중";

                if (vm.DeviceStatus.ConveyorSpeed <= 0)
                {
                    double speed = GetDouble(normalized, 180, "speed", "Speed", "conveyorSpeed", "conveyor_speed");
                    vm.DeviceStatus.ConveyorSpeed = speed;
                    DebugWindow.Instance.AddLog("conveyorSpeed", speed.ToString(CultureInfo.InvariantCulture));
                }
            }
            else if (command == "CONVEYOR_STOP" || command == "EMERGENCY_STOP")
            {
                vm.DeviceStatus.ConveyorStatus = command == "EMERGENCY_STOP" ? "비상정지" : "정지중";
                vm.DeviceStatus.ConveyorSpeed = 0;
                DebugWindow.Instance.AddLog("conveyorSpeed", "0");
            }

            if (TryGetString(normalized, out string conveyorStatus,
                "conveyorStatus",
                "conveyor_status",
                "status",
                "state"))
            {
                if (conveyorStatus == "CONVEYOR_START")
                    vm.DeviceStatus.ConveyorStatus = "동작중";
                else if (conveyorStatus == "CONVEYOR_STOP")
                    vm.DeviceStatus.ConveyorStatus = "정지중";
                else if (conveyorStatus == "EMERGENCY_STOP")
                    vm.DeviceStatus.ConveyorStatus = "비상정지";
                else
                    vm.DeviceStatus.ConveyorStatus = conveyorStatus;
            }

            if (TryGetString(normalized, out string robotArmStatus,
                "robotArmStatus",
                "robot_arm_status",
                "robotStatus",
                "robot_status"))
            {
                // 로봇팔은 MQTT가 직접 상태를 받는 구조이므로,
                // MQTT 연결이 살아있을 때는 Flask api/status 값으로 덮어쓰지 않습니다.
                if (!vm.IsRobotMqttConnected)
                {
                    vm.DeviceStatus.RobotArmStatus = robotArmStatus;
                }
            }

            if (TryGetString(normalized, out string ocrCamStatus,
                "ocrCamStatus",
                "ocr_cam_status",
                "ocrStatus",
                "ocr_status"))
            {
                vm.DeviceStatus.OcrCamStatus = ocrCamStatus;
            }

            if (TryGetString(normalized, out string qrCamStatus,
                "qrCamStatus",
                "qr_cam_status",
                "qrStatus",
                "qr_status"))
            {
                vm.DeviceStatus.QrCamStatus = qrCamStatus;
            }

            if (TryGetString(normalized, out string inputUnitStatus,
                "inputUnitStatus",
                "input_unit_status"))
            {
                vm.DeviceStatus.InputUnitStatus = inputUnitStatus;
            }

            if (TryGetInt(normalized, out int todaySortedCount,
                "todaySortedCount",
                "today_sorted_count",
                "sortedCount",
                "sorted_count"))
            {
                vm.DeviceStatus.TodaySortedCount = todaySortedCount;
            }

            if (TryGetInt(normalized, out int todayErrorCount,
                "todayErrorCount",
                "today_error_count",
                "errorCount",
                "error_count"))
            {
                vm.DeviceStatus.TodayErrorCount = todayErrorCount;
            }

            if (TryGetDouble(normalized, out double successRate,
                "successRate",
                "success_rate"))
            {
                vm.DeviceStatus.SuccessRate = successRate;
            }

            if (TryGetBool(normalized, out bool emergencyStop,
                "emergencyStop",
                "emergency_stop",
                "isEmergency",
                "is_emergency"))
            {
                vm.DeviceStatus.EmergencyStop = emergencyStop;
                vm.IsEmergencyStop = emergencyStop;
            }

            vm.RefreshDeviceStatus();
        }

        private static JObject NormalizePayloadObject(JObject source)
        {
            JObject merged = new JObject();

            foreach (var prop in source.Properties())
                merged[prop.Name] = prop.Value.DeepClone();

            MergeNestedObject(merged, source, "data");
            MergeNestedObject(merged, source, "deviceStatus");
            MergeNestedObject(merged, source, "device_status");
            MergeNestedObject(merged, source, "conveyor");
            MergeNestedObject(merged, source, "statusData");
            MergeNestedObject(merged, source, "status_data");
            MergeNestedObject(merged, source, "lastCommand");
            MergeNestedObject(merged, source, "last_command");
            MergeNestedObject(merged, source, "latestCommand");
            MergeNestedObject(merged, source, "latest_command");
            MergeNestedObject(merged, source, "commandData");
            MergeNestedObject(merged, source, "command_data");
            MergeNestedObject(merged, source, "payload");

            return merged;
        }

        private static void MergeNestedObject(JObject target, JObject source, string key)
        {
            JToken? token = source[key];

            if (token == null || token.Type == JTokenType.Null)
                return;

            JObject? obj = null;

            if (token.Type == JTokenType.Object)
            {
                obj = (JObject)token;
            }
            else if (token.Type == JTokenType.String)
            {
                string text = token.ToString();

                if (text.StartsWith("{") && text.EndsWith("}"))
                {
                    try
                    {
                        obj = JObject.Parse(text);
                    }
                    catch
                    {
                        obj = null;
                    }
                }
            }

            if (obj == null)
                return;

            foreach (var prop in obj.Properties())
            {
                if (target[prop.Name] == null || target[prop.Name]?.Type == JTokenType.Null)
                    target[prop.Name] = prop.Value.DeepClone();
            }
        }

        private static void HandleDeviceChange(MainViewModel vm, JObject d, string label)
        {
            try
            {
                string deviceId = d["device_id"]?.ToString() ?? "";

                Dispatch(() =>
                {
                    switch (deviceId)
                    {
                        case "conveyor_agent_01":
                            vm.DeviceStatus.ConveyorStatus = label;
                            break;

                        case "robot_agent_01":
                            // 로봇팔은 MQTT 상태를 우선 사용합니다.
                            // Flask device_connected/device_disconnected 이벤트가 늦게 도착해도
                            // WPF 화면에서 로봇팔 상태가 오프라인으로 덮이지 않도록 막습니다.
                            if (!vm.IsRobotMqttConnected)
                            {
                                vm.DeviceStatus.RobotArmStatus = label;
                            }
                            break;

                        case "vision_agent_01":
                            vm.DeviceStatus.OcrCamStatus = label;
                            vm.DeviceStatus.QrCamStatus = label;
                            break;
                    }

                    vm.RefreshDeviceStatus();
                });
            }
            catch (Exception ex)
            {
                Log("device change", ex);
            }
        }

        private static bool TryGetToken(JObject d, out JToken? token, params string[] keys)
        {
            foreach (string key in keys)
            {
                token = d[key];

                if (token != null && token.Type != JTokenType.Null)
                    return true;
            }

            token = null;
            return false;
        }

        private static bool TryGetString(JObject d, out string value, params string[] keys)
        {
            value = "";

            if (!TryGetToken(d, out JToken? token, keys))
                return false;

            value = token?.ToString() ?? "";
            return true;
        }

        private static bool TryGetDouble(JObject d, out double value, params string[] keys)
        {
            value = 0;

            if (!TryGetToken(d, out JToken? token, keys))
                return false;

            if (token == null)
                return false;

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<double>();
                return true;
            }

            string text = token.ToString();

            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return true;

            if (double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
                return true;

            return false;
        }

        private static bool TryGetInt(JObject d, out int value, params string[] keys)
        {
            value = 0;

            if (!TryGetToken(d, out JToken? token, keys))
                return false;

            if (token == null)
                return false;

            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }

            string text = token.ToString();

            if (int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return true;

            if (int.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
                return true;

            return false;
        }

        private static bool TryGetBool(JObject d, out bool value, params string[] keys)
        {
            value = false;

            if (!TryGetToken(d, out JToken? token, keys))
                return false;

            if (token == null)
                return false;

            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }

            string text = token.ToString();

            if (bool.TryParse(text, out value))
                return true;

            if (text == "1")
            {
                value = true;
                return true;
            }

            if (text == "0")
            {
                value = false;
                return true;
            }

            return false;
        }

        private static string GetString(JObject d, string defaultValue, params string[] keys)
        {
            return TryGetString(d, out string value, keys) ? value : defaultValue;
        }

        private static double GetDouble(JObject d, double defaultValue, params string[] keys)
        {
            return TryGetDouble(d, out double value, keys) ? value : defaultValue;
        }

        private static int GetInt(JObject d, int defaultValue, params string[] keys)
        {
            return TryGetInt(d, out int value, keys) ? value : defaultValue;
        }

        private static bool GetBool(JObject d, bool defaultValue, params string[] keys)
        {
            return TryGetBool(d, out bool value, keys) ? value : defaultValue;
        }

        // 불량 행 판별 — 정상 행에는 이미지를 붙이지 않기 위함
        private static bool IsDefectRow(SortingLog l)
            => l.Status == "불량" || l.BoxStatus == "불량" || l.FinalResult == "불량";

        // 상대 경로(/storage/..) → 절대 URL. 이미 http면 그대로. 빈 값이면 null.
        private static string? ToAbsoluteImageUrl(string? p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            return p.StartsWith("http")
                ? p
                : "http://192.168.0.21:5000" + (p.StartsWith("/") ? "" : "/") + p;
        }

        // 이벤트에서 이미지 URL 추출 (필드명 변형 모두 허용) → 절대 URL
        private static string? ExtractImageUrl(JObject raw)
        {
            string? p = raw["imageUrl"]?.ToString()
                     ?? raw["image_url"]?.ToString()
                     ?? raw["imagePath"]?.ToString()
                     ?? raw["image_path"]?.ToString();
            return ToAbsoluteImageUrl(p);
        }

        // ── 소켓 진단 로그 → 파일(socket_debug.log)에 기록 (읽어서 판정용) ─
        private static readonly string SockLogPath =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "socket_debug.log");
        private static void SockLog(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(SockLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}");
            }
            catch { }
        }


        private static readonly JsonSerializerSettings _jsonSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTime,
        };

        private static T? Deserialize<T>(string json)
            => JsonConvert.DeserializeObject<T>(json, _jsonSettings);

        private static void Dispatch(Action action)
            => Application.Current?.Dispatcher.Invoke(action);

        private static void Log(string tag, Exception ex)
            => Console.WriteLine($"[ApiService:{tag}] {ex.Message}");


        // ── YOLO 결과 → Flask POST /api/yolo_result (불량이면 cam1/cam2 이미지 첨부) ─
        public async Task<bool> PostYoloResultAsync(
            string trackingNumber,
            bool hasDefect,
            string defectClass,
            double confidence,
            byte[]? imageBytes = null)
        {
            HttpContent? content = null;
            try
            {
                var payload = new
                {
                    yolo_result = hasDefect ? "불량" : "정상",
                    has_defect = hasDefect,
                    defect_class = defectClass,
                    confidence = Math.Round(confidence, 3),
                    timestamp = DateTime.Now.ToString("o"),
                    tracking_number = trackingNumber ?? "",
                    source = "WPF",
                };
                var json = JsonConvert.SerializeObject(payload);

                bool hasImg = hasDefect && imageBytes != null && imageBytes.Length > 0;

                if (hasImg)
                {
                    // 불량 → multipart/form-data:  data(JSON) + image(JPEG)
                    var form = new MultipartFormDataContent();
                    form.Add(new StringContent(json, System.Text.Encoding.UTF8), "data");

                    var imageContent = new ByteArrayContent(imageBytes!);
                    imageContent.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    form.Add(imageContent, "image", $"defect_{trackingNumber}.jpg");
                    content = form;
                }
                else
                {
                    // 정상 → JSON만
                    content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                }

                var res = await _http.PostAsync("api/yolo_result", content);

                System.Diagnostics.Debug.WriteLine(res.IsSuccessStatusCode
                    ? $"[HTTP] YOLO결과 전송 완료 → {trackingNumber} | {(hasDefect ? "불량" : "정상")} | 이미지:{(hasImg ? "O" : "X")} | HTTP {(int)res.StatusCode}"
                    : $"[HTTP] YOLO결과 전송 실패 → HTTP {(int)res.StatusCode}");

                return res.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                System.Diagnostics.Debug.WriteLine("[HTTP] Flask /api/yolo_result 연결 실패 — 전송 생략");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HTTP] YOLO결과 전송 오류: {ex.Message}");
                return false;
            }
            finally
            {
                content?.Dispose();
            }
        }

        // ── YOLO 불량 감지 → Flask /api/defect_notify POST (이미지 첨부) ─
        public async Task NotifyDefectAsync(string camId, DefectResult result, byte[]? imageBytes = null)
        {
            HttpContent? content = null;
            try
            {
                var detections = result.Detections.Select(d => new
                {
                    @class = d.Class,
                    confidence = Math.Round(d.Confidence, 3),
                }).ToList();

                var payload = new
                {
                    camera = camId,
                    timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    count = result.Count,
                    detections = detections,
                };

                var json = JsonConvert.SerializeObject(payload);

                bool hasImg = imageBytes != null && imageBytes.Length > 0;
                if (hasImg)
                {
                    // multipart/form-data:  data(JSON) + image(JPEG)
                    var form = new MultipartFormDataContent();
                    form.Add(new StringContent(json, System.Text.Encoding.UTF8), "data");
                    var imageContent = new ByteArrayContent(imageBytes!);
                    imageContent.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    form.Add(imageContent, "image", $"defect_{camId}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                    content = form;
                }
                else
                {
                    content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                }

                var res = await _http.PostAsync("api/defect_notify", content);

                System.Diagnostics.Debug.WriteLine(res.IsSuccessStatusCode
                    ? $"[Flask] 불량 신호 전송 완료 → {camId} | 이미지:{(hasImg ? "O" : "X")} | {string.Join(", ", detections.Select(d => d.@class))}"
                    : $"[Flask] 불량 신호 전송 실패 → HTTP {(int)res.StatusCode}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Flask] 불량 신호 전송 오류: {ex.Message}");
            }
            finally
            {
                content?.Dispose();
            }
        }

        public async Task DisconnectAsync()
        {
            await _socket.DisconnectAsync();
            _http.Dispose();
        }
    }
}
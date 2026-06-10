using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Newtonsoft.Json;

namespace MasterAdmin
{
    // ════════════════════════════════════════════════════════════════════
    //  통계(총괄 페이지 차트) — Flask GET /api/stats/overview (오늘 기준)
    //
    //  설계: 백엔드는 '건수(count)'만 보낸다. 비율(%)·막대 높이·도넛 호(arc)는
    //        전부 WPF가 계산한다. 권역은 값이 있는 것만 배열로 받아 그 개수만큼 그린다.
    //
    //  응답 예시:
    //  {
    //    "period": "today",
    //    "regions": [ { "name":"서울", "normal":72, "defect":8 }, ... ],
    //    "recognition": { "qr": 62, "ocr": 38 },
    //    "error_types": [ { "name":"인식실패", "count":45 }, ... ]
    //  }
    // ════════════════════════════════════════════════════════════════════

    public class StatsOverview
    {
        [JsonProperty("period")]
        public string Period { get; set; } = "today";

        [JsonProperty("regions")]
        public List<RegionStat> Regions { get; set; } = new();

        [JsonProperty("recognition")]
        public RecognitionStat Recognition { get; set; } = new();

        [JsonProperty("error_types")]
        public List<ErrorTypeStat> ErrorTypes { get; set; } = new();
    }

    public class RegionStat
    {
        [JsonProperty("name")]   public string Name { get; set; } = "";
        [JsonProperty("normal")] public int Normal { get; set; }
        [JsonProperty("defect")] public int Defect { get; set; }
    }

    public class RecognitionStat
    {
        [JsonProperty("qr")]  public int Qr { get; set; }
        [JsonProperty("ocr")] public int Ocr { get; set; }
    }

    public class ErrorTypeStat
    {
        [JsonProperty("name")]  public string Name { get; set; } = "";
        [JsonProperty("count")] public int Count { get; set; }
    }

    // ── 막대 그래프용 뷰 항목 (권역 1개 = 막대 1개, 정상/불량 쌓기) ──────────
    public class RegionBar
    {
        public string Name { get; set; } = "";
        public int Normal { get; set; }
        public int Defect { get; set; }
        public double NormalHeight { get; set; }   // 픽셀
        public double DefectHeight { get; set; }   // 픽셀
    }

    // ── 도넛 차트용 슬라이스 (인식방식/오류유형 공용) ────────────────────────
    public class DonutSlice
    {
        public Geometry Geometry { get; set; } = Geometry.Empty;
        public Brush Brush { get; set; } = Brushes.Gray;
        public string Label { get; set; } = "";
        public double Percent { get; set; }
        public string Display => $"{Label}  {Percent:0}%";
    }

    // ── 차트 빌더 (건수 → 화면 좌표/도형 계산) ──────────────────────────────
    public static class StatsCharts
    {
        // 막대 영역 기준 높이(px). 원래 디자인의 0~100 축과 동일한 스케일.
        private const double BarMaxPx = 100.0;

        // 도넛 기하 기준 (원본 XAML: 80×80, 중심 40,40, 반지름 40)
        private const double DonutR = 40.0;
        private const double DonutCx = 40.0;
        private const double DonutCy = 40.0;

        private static readonly Brush NormalBrush = Frozen("#38BDF8");
        private static readonly Brush DefectBrush = Frozen("#F87171");

        private static readonly Brush QrBrush = Frozen("#38BDF8");
        private static readonly Brush OcrBrush = Frozen("#4ADE80");

        // 오류유형 슬라이스 색 (인덱스 순환)
        private static readonly Brush[] ErrorPalette =
        {
            Frozen("#F87171"), Frozen("#FB923C"), Frozen("#FBBF24"),
            Frozen("#A78BFA"), Frozen("#38BDF8"),
        };

        public static Brush RegionNormalBrush => NormalBrush;
        public static Brush RegionDefectBrush => DefectBrush;

        // ── 권역 막대 ────────────────────────────────────────────────────
        //   axisMax(축 상한)을 '예쁜' 5의 배수로 올림 → 막대를 그 기준으로 스케일.
        //   가장 높은 막대가 상단 근처까지 차고, 축 라벨도 정수로 떨어진다.
        public static List<RegionBar> BuildRegionBars(IEnumerable<RegionStat> regions, int axisMax)
        {
            double max = Math.Max(axisMax, 1);
            return regions.Select(r => new RegionBar
            {
                Name = r.Name,
                Normal = r.Normal,
                Defect = r.Defect,
                NormalHeight = r.Normal / max * BarMaxPx,
                DefectHeight = r.Defect / max * BarMaxPx,
            }).ToList();
        }

        // 축 상한: 최대 스택합(정상+불량)을 5의 배수로 올림(최소 5).
        public static int RegionAxisMax(IEnumerable<RegionStat> regions)
        {
            int maxTotal = regions.Select(r => r.Normal + r.Defect).DefaultIfEmpty(0).Max();
            if (maxTotal <= 0) return 5;
            return (int)(Math.Ceiling(maxTotal / 5.0) * 5);
        }

        // Y축 라벨(위→아래 5개): max, .8max, .6max, .4max, .2max
        public static List<string> RegionAxisLabels(int axisMax)
            => new() { axisMax.ToString(), (axisMax * 4 / 5).ToString(),
                       (axisMax * 3 / 5).ToString(), (axisMax * 2 / 5).ToString(),
                       (axisMax / 5).ToString() };

        // ── 인식방식 도넛 (QR/OCR 2조각) ─────────────────────────────────
        public static List<DonutSlice> BuildRecognition(RecognitionStat r)
            => BuildDonut(new[]
            {
                ("QR",  (double)r.Qr,  QrBrush),
                ("OCR", (double)r.Ocr, OcrBrush),
            });

        // ── 오류유형 도넛 (N조각, 색 순환) ───────────────────────────────
        public static List<DonutSlice> BuildErrorTypes(IEnumerable<ErrorTypeStat> errors)
        {
            var items = errors.Select((e, i) =>
                (e.Name, (double)e.Count, ErrorPalette[i % ErrorPalette.Length])).ToArray();
            return BuildDonut(items);
        }

        private static List<DonutSlice> BuildDonut((string label, double value, Brush brush)[] items)
        {
            var slices = new List<DonutSlice>();
            double total = items.Sum(x => x.value);
            if (total <= 0) return slices;   // 데이터 없음 → 빈 도넛

            double angle = 0;
            foreach (var (label, value, brush) in items)
            {
                if (value <= 0) continue;
                double sweep = value / total * 360.0;
                slices.Add(new DonutSlice
                {
                    Label = label,
                    Percent = value / total * 100.0,
                    Brush = brush,
                    Geometry = BuildSlice(angle, sweep),
                });
                angle += sweep;
            }
            return slices;
        }

        // 부채꼴(파이 조각) 기하: 0도=12시, 시계방향. 한 조각이 360도면 원.
        private static Geometry BuildSlice(double startDeg, double sweepDeg)
        {
            if (sweepDeg >= 359.999)
                return new EllipseGeometry(new Point(DonutCx, DonutCy), DonutR, DonutR);

            double a0 = startDeg * Math.PI / 180.0;
            double a1 = (startDeg + sweepDeg) * Math.PI / 180.0;
            var p0 = new Point(DonutCx + DonutR * Math.Sin(a0), DonutCy - DonutR * Math.Cos(a0));
            var p1 = new Point(DonutCx + DonutR * Math.Sin(a1), DonutCy - DonutR * Math.Cos(a1));

            var fig = new PathFigure { StartPoint = new Point(DonutCx, DonutCy), IsClosed = true };
            fig.Segments.Add(new LineSegment(p0, true));
            fig.Segments.Add(new ArcSegment(p1, new Size(DonutR, DonutR), 0,
                sweepDeg > 180.0, SweepDirection.Clockwise, true));

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            return geo;
        }

        private static Brush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}

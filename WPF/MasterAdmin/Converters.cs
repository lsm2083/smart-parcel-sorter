using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MasterAdmin
{
    // FilledSlots, TotalSlots, ActualWidth(Grid) → 픽셀 너비 반환
    public class SlotFillConverter : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length == 3
                && values[0] is int filled
                && values[1] is int total && total > 0
                && values[2] is double width)
            {
                double ratio = Math.Clamp((double)filled / total, 0.0, 1.0);
                return width * ratio;
            }
            return 0.0;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string status = (value?.ToString() ?? "").Trim();

            return status switch
            {
                "정상" or "출고완료" or "작동중" or "동작중" or "작동" or "정지(정상)" or "로그인" or "연결됨" or "가동중" or "정상 가동"
                    => new SolidColorBrush(Color.FromRgb(34, 197, 94)),

                "불량" or "오류" or "실패" or "로그아웃" or "연결안됨" or "오프라인" or "분류실패" or "OCR실패"
                    => new SolidColorBrush(Color.FromRgb(239, 68, 68)),

                "비상정지" or "비상정지 활성" or "EMERGENCY" or "EMERGENCY_STOP"
                    => new SolidColorBrush(Color.FromRgb(220, 38, 38)),

                "대기" or "대기중" or "출고대기" or "처리중" or "연결전" or "연결중" or "정지중" or "정지중..." or "재개중" or "재개중..." or "초기화중"
                    => new SolidColorBrush(Color.FromRgb(234, 179, 8)),

                "출발중" or "출고중" or "이동중" or "시작중..." or "HOME 이동중" or "집기중" or "분류중" or "정렬중" or "복귀중" or "작업중"
                    => new SolidColorBrush(Color.FromRgb(59, 130, 246)),

                "박스훼손" or "경고"
                    => new SolidColorBrush(Color.FromRgb(234, 179, 8)),

                "비닐"
                    => new SolidColorBrush(Color.FromRgb(168, 85, 247)),

                _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class SeverityToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string severity = (value?.ToString() ?? "").Trim();

            return severity switch
            {
                "오류" or "심각" or "위험" or "비상"
                    => new SolidColorBrush(Color.FromRgb(239, 68, 68)),

                "경고" or "주의"
                    => new SolidColorBrush(Color.FromRgb(234, 179, 8)),

                "정상" or "정보"
                    => new SolidColorBrush(Color.FromRgb(34, 197, 94)),

                _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class SuccessToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b)
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                : new SolidColorBrush(Color.FromRgb(239, 68, 68));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool v = value is bool b && b;
            return v ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool v = value is bool b && b;
            return v ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BoolToEmergencyColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b)
                ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
                : new SolidColorBrush(Color.FromRgb(34, 197, 94));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BoolToEmergencyTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? "비상정지 활성" : "정상 가동";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BoolToConnectionColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b)
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                : new SolidColorBrush(Color.FromRgb(239, 68, 68));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BoolToConnectionTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? "연결됨" : "연결전";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BusyToRobotTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? "작업중" : "대기";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BusyToRobotColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b)
                ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
                : new SolidColorBrush(Color.FromRgb(34, 197, 94));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // 텍스트가 비어있으면 Visible(플레이스홀더 표시), 내용 있으면 Collapsed
    public class TextToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrEmpty(value?.ToString()) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // 컬렉션 개수(Count) → Visibility.
    //   기본: count==0 일 때 Visible (빈 상태 안내문구용).
    //   ConverterParameter="invert": count>0 일 때 Visible (실제 데이터 있을 때만 표시).
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int n = value is int i ? i : 0;
            bool empty = n == 0;
            bool invert = (parameter as string) == "invert";
            bool show = invert ? !empty : empty;
            return show ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // ImagePath가 null/빈 문자열이면 Collapsed, 값 있으면 Visible
    public class HasValueToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrEmpty(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
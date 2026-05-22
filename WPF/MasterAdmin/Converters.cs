using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MasterAdmin
{
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value?.ToString() ?? "") switch
            {
                "정상" or "출고완료" or "작동중" or "동작중" or "정지(정상)" or "로그인" or "연결됨"
                    => new SolidColorBrush(Color.FromRgb(34, 197, 94)),
                "불량" or "오류" or "실패" or "로그아웃"
                    => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                "대기" or "출고대기" or "처리중" or "연결전" or "정지중" or "재개중"
                    => new SolidColorBrush(Color.FromRgb(234, 179, 8)),
                "출고중" or "작동" or "이동중" or "시작중..."
                    => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                "OCR실패" or "분류실패"
                    => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                "박스걸림"
                    => new SolidColorBrush(Color.FromRgb(234, 179, 8)),
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
            return (value?.ToString() ?? "") switch
            {
                "오류" => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                "경고" => new SolidColorBrush(Color.FromRgb(234, 179, 8)),
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
}
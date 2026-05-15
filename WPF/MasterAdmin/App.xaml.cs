using System.Windows;
using System.Windows.Media;

namespace MasterAdmin
{
    public partial class App : Application
    {
        public static bool IsDark { get; private set; } = true;

        public static void ToggleTheme()
        {
            IsDark = !IsDark;
            ApplyTheme();
        }

        public static void ApplyTheme()
        {
            var res = Current.Resources;

            if (IsDark)
            {
                // ── 다크 ──
                res["BgWindow"]    = Brush("#0D1B2A");
                res["BgTopBar"]    = Brush("#111D2B");
                res["BgCard"]      = Brush("#1E2A3A");
                res["BgAltRow"]    = Brush("#0F1923");
                res["BgInput"]     = Brush("#0F1923");
                res["BgColHead"]   = Brush("#0F1923");
                res["FgPrimary"]   = Brush("#E2E8F0");
                res["FgSecondary"] = Brush("#94A3B8");
                res["FgMuted"]     = Brush("#64748B");
                res["FgData"]      = Brush("#CBD5E1");
                res["BorderLine"]  = Brush("#1E3A5F");
                res["AccentBg"]    = Brush("#1E3A5F");
                res["AccentFg"]    = Brush("#38BDF8");
                res["SmBtnBg"]     = Brush("#1E3A5F");
                res["SmBtnFg"]     = Brush("#94A3B8");
            }
            else
            {
                // ── 라이트 ──
                res["BgWindow"]    = Brush("#F1F5F9");
                res["BgTopBar"]    = Brush("#FFFFFF");
                res["BgCard"]      = Brush("#FFFFFF");
                res["BgAltRow"]    = Brush("#F8FAFC");
                res["BgInput"]     = Brush("#F1F5F9");
                res["BgColHead"]   = Brush("#F1F5F9");
                res["FgPrimary"]   = Brush("#0F172A");
                res["FgSecondary"] = Brush("#475569");
                res["FgMuted"]     = Brush("#94A3B8");
                res["FgData"]      = Brush("#1E293B");
                res["BorderLine"]  = Brush("#CBD5E1");
                res["AccentBg"]    = Brush("#DBEAFE");
                res["AccentFg"]    = Brush("#1D4ED8");
                res["SmBtnBg"]     = Brush("#E2E8F0");
                res["SmBtnFg"]     = Brush("#475569");
            }
        }

        private static SolidColorBrush Brush(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}

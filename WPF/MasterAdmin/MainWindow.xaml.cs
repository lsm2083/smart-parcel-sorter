using System.Windows;

namespace MasterAdmin
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        private void BtnDebug_Click(object sender, RoutedEventArgs e)
        {
            DebugWindow.Instance.Show();
            DebugWindow.Instance.Activate();
        }
    }
}

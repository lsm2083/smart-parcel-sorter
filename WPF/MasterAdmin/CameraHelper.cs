using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace MasterAdmin
{
    public class CameraHelper : IDisposable
    {
        private readonly int _deviceIndex;
        private readonly Image _target;
        private readonly StackPanel _placeholder;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public bool IsRunning { get; private set; }

        public CameraHelper(int deviceIndex, Image targetImage, StackPanel placeholder)
        {
            _deviceIndex = deviceIndex;
            _target = targetImage;
            _placeholder = placeholder;
        }

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsRunning = true;
            Task.Run(() => CaptureLoop(token), token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            IsRunning = false;
        }

        private void CaptureLoop(CancellationToken token)
        {
            using var cap = new VideoCapture(_deviceIndex);
            cap.Set(VideoCaptureProperties.FrameWidth, 640);
            cap.Set(VideoCaptureProperties.FrameHeight, 360);

            if (!cap.IsOpened())
            {
                ShowPlaceholder();
                return;
            }

            HidePlaceholder();

            using var frame = new Mat();
            while (!token.IsCancellationRequested)
            {
                if (!cap.Read(frame) || frame.Empty()) continue;

                // BitmapSourceConverter 없이 직접 변환
                var bitmap = MatToBitmapSource(frame);
                bitmap.Freeze();

                _target.Dispatcher.Invoke(() => _target.Source = bitmap);
                Thread.Sleep(33);
            }
        }

        /// <summary>OpenCvSharp.Extensions 없이 Mat → BitmapSource 변환</summary>
        private static BitmapSource MatToBitmapSource(Mat mat)
        {
            // BGR → BGRA 변환
            using var converted = new Mat();
            Cv2.CvtColor(mat, converted, ColorConversionCodes.BGR2BGRA);

            int width = converted.Width;
            int height = converted.Height;
            int channels = converted.Channels();
            int stride = width * channels;

            var data = new byte[height * stride];
            Marshal.Copy(converted.Data, data, 0, data.Length);

            return BitmapSource.Create(
                width, height,
                96, 96,
                PixelFormats.Bgra32,
                null,
                data,
                stride);
        }

        private void ShowPlaceholder() =>
            _placeholder.Dispatcher.Invoke(() =>
            {
                _placeholder.Visibility = Visibility.Visible;
                _target.Visibility = Visibility.Collapsed;
            });

        private void HidePlaceholder() =>
            _placeholder.Dispatcher.Invoke(() =>
            {
                _placeholder.Visibility = Visibility.Collapsed;
                _target.Visibility = Visibility.Visible;
            });

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _cts?.Dispose();
            _disposed = true;
        }
    }
}

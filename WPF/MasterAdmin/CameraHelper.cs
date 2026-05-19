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
        private readonly string? _streamUrl;
        private readonly Image _target;
        private readonly StackPanel _placeholder;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public Action<Mat>? OnFrame { get; set; }
        public bool IsRunning { get; private set; }

        // 로컬 카메라용 (기존)
        public CameraHelper(int deviceIndex, Image targetImage, StackPanel placeholder)
        {
            _deviceIndex = deviceIndex;
            _streamUrl = null;
            _target = targetImage;
            _placeholder = placeholder;
        }

        // 네트워크 스트림 URL용 (추가)
        public CameraHelper(string streamUrl, Image targetImage, StackPanel placeholder)
        {
            _deviceIndex = -1;
            _streamUrl = streamUrl;
            _target = targetImage;
            _placeholder = placeholder;
        }

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            Task.Run(() => CaptureLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            IsRunning = false;
        }

        private void CaptureLoop(CancellationToken token)
        {
            using var cap = _streamUrl != null
                ? new VideoCapture(_streamUrl)
                : new VideoCapture(_deviceIndex, VideoCaptureAPIs.DSHOW);

            cap.Set(VideoCaptureProperties.FrameWidth, 640);
            cap.Set(VideoCaptureProperties.FrameHeight, 480);
            cap.Set(VideoCaptureProperties.BufferSize, 1);

            if (!cap.IsOpened())
            {
                ShowPlaceholder();
                return;
            }

            HidePlaceholder();

            using var frame = new Mat();
            using var flipped = new Mat();
            using var converted = new Mat();
            WriteableBitmap? wb = null;

            while (!token.IsCancellationRequested)
            {
                cap.Grab();
                if (!cap.Retrieve(frame) || frame.Empty()) continue;

                if (_streamUrl == null)
                    Cv2.Flip(frame, flipped, FlipMode.Y);
                else
                    frame.CopyTo(flipped);

                OnFrame?.Invoke(flipped.Clone());

                Cv2.CvtColor(flipped, converted, ColorConversionCodes.BGR2BGRA);

                int w = converted.Width;
                int h = converted.Height;
                int stride = w * 4;

                _target.Dispatcher.Invoke(() =>
                {
                    if (wb == null || wb.PixelWidth != w || wb.PixelHeight != h)
                    {
                        wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                        _target.Source = wb;
                    }

                    wb.Lock();
                    var pixelData = new byte[stride * h];
                    Marshal.Copy(converted.Data, pixelData, 0, pixelData.Length);
                    Marshal.Copy(pixelData, 0, wb.BackBuffer, pixelData.Length);
                    wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
                    wb.Unlock();
                });
            }
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
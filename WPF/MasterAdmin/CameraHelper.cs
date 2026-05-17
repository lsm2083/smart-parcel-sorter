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
            _target      = targetImage;
            _placeholder = placeholder;
        }

        public void Start()
        {
            if (IsRunning) return;
            _cts      = new CancellationTokenSource();
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
            using var cap = new VideoCapture(_deviceIndex, VideoCaptureAPIs.DSHOW);

            // 해상도 낮게 설정 → 속도 향상
            cap.Set(VideoCaptureProperties.FrameWidth,  640);
            cap.Set(VideoCaptureProperties.FrameHeight, 480);

            // 버퍼 최소화 → 지연 감소
            cap.Set(VideoCaptureProperties.BufferSize, 1);

            if (!cap.IsOpened())
            {
                ShowPlaceholder();
                return;
            }

            HidePlaceholder();

            using var frame     = new Mat();
            using var converted = new Mat();

            // WriteableBitmap 재사용 → GC 부담 감소
            WriteableBitmap? wb = null;

            while (!token.IsCancellationRequested)
            {
                // 버퍼에 쌓인 프레임 버리고 최신 프레임만 가져오기
                cap.Grab();
                if (!cap.Retrieve(frame) || frame.Empty()) continue;

                Cv2.CvtColor(frame, converted, ColorConversionCodes.BGR2BGRA);

                int w      = converted.Width;
                int h      = converted.Height;
                int stride = w * 4;

                _target.Dispatcher.Invoke(() =>
                {
                    // 처음 한 번만 WriteableBitmap 생성
                    if (wb == null || wb.PixelWidth != w || wb.PixelHeight != h)
                    {
                        wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                        _target.Source = wb;
                    }

                    // 직접 픽셀 버퍼에 복사 (빠름)
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
                _target.Visibility      = Visibility.Collapsed;
            });

        private void HidePlaceholder() =>
            _placeholder.Dispatcher.Invoke(() =>
            {
                _placeholder.Visibility = Visibility.Collapsed;
                _target.Visibility      = Visibility.Visible;
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

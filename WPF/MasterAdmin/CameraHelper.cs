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
        public Action<Mat>? OnDisplayFrame { get; set; }
        public bool IsRunning { get; private set; }

        // 좌우반전 강제 옵션. USB 직접연결은 항상 반전(셀카처럼 미러)했는데, 스트림으로
        //   입력을 바꿔도 화면·ROI·오버레이가 종전과 동일하도록 현장/출고 스트림엔 이 값을
        //   true로 줘 같은 반전을 유지한다. (QR캠 등 다른 스트림은 기본 false → 영향 없음)
        public bool FlipHorizontally { get; set; }

        // ── 최신 프레임 JPEG 저장 (불량 감지 시 이미지 전송용) ────────────
        private byte[]? _latestJpeg;
        private readonly object _jpegLock = new();

        /// <summary>현재 카메라 프레임을 JPEG byte[]로 반환. 없으면 null.</summary>
        public byte[]? GetLatestJpeg()
        {
            lock (_jpegLock) return _latestJpeg;
        }

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

            // 출고캠(index 1) = YOLO 학습 해상도 1280×720 맞춤
            // 현장캠(index 0) / 네트워크 스트림 = 640×480 유지
            if (_deviceIndex == 1)
            {
                cap.Set(VideoCaptureProperties.FrameWidth, 1280);
                cap.Set(VideoCaptureProperties.FrameHeight, 720);
            }
            else
            {
                cap.Set(VideoCaptureProperties.FrameWidth, 640);
                cap.Set(VideoCaptureProperties.FrameHeight, 480);
            }
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

                // USB 직접연결(_streamUrl==null)은 항상 반전. 스트림은 FlipHorizontally일 때만 반전.
                if (_streamUrl == null || FlipHorizontally)
                    Cv2.Flip(frame, flipped, FlipMode.Y);
                else
                    frame.CopyTo(flipped);

                OnFrame?.Invoke(flipped.Clone());   // YOLO 등 처리용 (클린 복사본)
                OnDisplayFrame?.Invoke(flipped);     // ★ 화면 표시 직전 — 그리기 가능

                // ★ 최신 프레임 JPEG 저장 (불량 감지 시 이미지 전송용)
                Cv2.ImEncode(".jpg", flipped, out var jpegBuf,
                    new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
                lock (_jpegLock) { _latestJpeg = jpegBuf; }

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
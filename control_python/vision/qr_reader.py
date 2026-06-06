"""
QR 디코더 - 다중 전략, 속도 우선.

호출당 약 0.3~0.7초 (전체 프레임 기준).
박스 영역만 자르면 더 빠름.

전략:
  1) WeChat QR 디코더 (가장 강건, 손상/저해상도 QR 잘 잡음)
  2) OpenCV 내장 (WeChat 실패 시 빠른 폴백)
  3) pyzbar (선명한 QR 보장 케이스용, 다른 둘 다 실패 시)
"""
import os
import cv2


# =========================
# WeChat QR 디코더
# =========================
_wechat_detector = None

def _init_wechat():
    global _wechat_detector
    try:
        model_dir = r"C:\qr_models"
        detect_proto = os.path.join(model_dir, "detect.prototxt")
        detect_model = os.path.join(model_dir, "detect.caffemodel")
        sr_proto     = os.path.join(model_dir, "sr.prototxt")
        sr_model     = os.path.join(model_dir, "sr.caffemodel")
        if all(os.path.exists(p) for p in
               [detect_proto, detect_model, sr_proto, sr_model]):
            _wechat_detector = cv2.wechat_qrcode_WeChatQRCode(
                detect_proto, detect_model, sr_proto, sr_model
            )
            print("[QR] WeChat 디코더 로드 성공")
        else:
            print("[QR] WeChat 모델 파일 없음, pyzbar/OpenCV 디코더만 사용")
    except Exception as e:
        print(f"[QR] WeChat 디코더 초기화 실패: {e}")

_init_wechat()


# =========================
# pyzbar
# =========================
try:
    from pyzbar.pyzbar import decode as _pyzbar_decode
    _has_pyzbar = True
except Exception:
    _has_pyzbar = False


# =========================
# OpenCV 내장
# =========================
_cv_detector = cv2.QRCodeDetector()


# =========================
# 디코더 헬퍼
# =========================
def _try_wechat(img):
    if _wechat_detector is None:
        return None
    try:
        res, _ = _wechat_detector.detectAndDecode(img)
        if res and len(res) > 0 and res[0]:
            return res[0]
    except Exception:
        pass
    return None


def _try_opencv(img):
    try:
        data, _, _ = _cv_detector.detectAndDecode(img)
        if data:
            return data
    except Exception:
        pass
    return None


def _try_pyzbar(img):
    if not _has_pyzbar:
        return None
    try:
        decoded = _pyzbar_decode(img)
        for obj in decoded:
            try:
                return obj.data.decode("utf-8")
            except UnicodeDecodeError:
                try:
                    return obj.data.decode("cp949")
                except UnicodeDecodeError:
                    continue
    except Exception:
        pass
    return None


def detect_qr(frame):
    """
    빠른 QR 검출. WeChat → OpenCV → pyzbar 순으로 시도, 첫 성공 시 즉시 반환.
    호출당 약 0.3~0.7초 (입력 이미지 크기에 비례).
    """
    if frame is None or frame.size == 0:
        return None, None

    # 가장 강력한 WeChat 먼저
    data = _try_wechat(frame)
    if data:
        return data, None

    # WeChat 실패 시 OpenCV (가벼움)
    data = _try_opencv(frame)
    if data:
        return data, None

    # 마지막 보루 pyzbar
    data = _try_pyzbar(frame)
    if data:
        return data, None

    return None, None
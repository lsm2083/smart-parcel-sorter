"""
Vision Agent (자가 트리거 + 4모서리 진입 트리거) — v6.0 수정본

주요 변경:
  - fragile 파싱: handling=파손주의 인식 추가
  - sort_code 결정: fragile + BOX 일 때 _FRAGILE 자동 부착 (derive_sort_code 헬퍼)
  - OCR 채택 기준 강화: invoice_no가 숫자거나 region이 있어야만 채택
  - _finalize_and_publish: OCR이 헛소리면 publish_scan_failed로 처리
  - 발행 디버그 로그 (_safe_publish 헬퍼)
  - publish_scan_failed, publish_mismatch 에 image_path 포함
  - [최적화] BoxDetector 흑백 변환 시 배경(초록색)과 박스 분리도를 높이기 위해 Red 채널 분리 사용

상태:
  IDLE       — 박스 없음
  PREPARING  — ROI 안에 움직임 감지. 4모서리가 ROI 안쪽에 들어오길 대기
  SCANNING   — 박스 진입 확인. 워커 스레드에서 QR/OCR 시도
  COOLDOWN   — 스캔 종료. ROI 비워질 때까지 대기 후 IDLE 복귀
"""

import os
import sys
import re
import cv2
import json
import time
import threading
import numpy as np
from datetime import datetime
from flask import Flask, Response

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.abspath(os.path.join(BASE_DIR, ".."))
if PROJECT_DIR not in sys.path:
    sys.path.insert(0, PROJECT_DIR)

from common.agent_base import AgentBase
from common.topics import VISION_COMMAND, VISION_SCAN_RESULT, VISION_FAIL

from vision.qr_reader import detect_qr
from vision.ocr_reader import detect_ocr
from vision.ocr_parser import parse_ocr_fields


# =========================
# 설정
# =========================
BROKER_HOST   = "192.168.0.21"
DEVICE_ID     = "vision"

STREAM_HOST   = "0.0.0.0"
STREAM_PORT   = 8081
STREAM_URL    = f"http://192.168.0.21:{STREAM_PORT}/stream"

CAMERA_INDEX  = 0
CAMERA_WIDTH  = 1280
CAMERA_HEIGHT = 720

OCR_ROI_RATIO = (0.05, 0.03, 0.95, 0.97)
STREAM_JPEG_QUALITY = 70

USE_CALIBRATION = True
USE_CALIBRATION_DISPLAY = True
CALIB_ALPHA = 0.0
CALIBRATION_FILE = "camera_calibration.npz"

SCAN_TIMEOUT_S       = 10.0
MOTION_THRESHOLD     = 0.10
PRESENCE_FRAMES      = 5
FULL_BOX_FRAMES      = 2
ABORT_FRAMES         = 60
ABSENCE_FRAMES_RESET = 30
CORNER_CHECK_EVERY_N = 1
MIN_BOX_AREA_RATIO   = 0.10

TRIGGER_LINE_RATIO = 0.5
TRIGGER_TOLERANCE = 40

STRICT_CROSSCHECK = False


# =========================
# 전역
# =========================
app = Flask(__name__)
latest_frame = None
stream_frame = None
frame_lock = threading.Lock()
_last_mask_save_time = 0.0


def now_text():
    return datetime.now().strftime("%Y-%m-%dT%H:%M:%S")


# =========================
# sort_code 결정 (v6.0)
# =========================
def derive_sort_code(qr):
    """
    QR 파싱 결과에서 sort_code 결정.
    1) QR에 sort_code 명시되어 있으면 그대로
    2) 없으면 region + package_type 조합
    3) fragile=True 이고 BOX 일 때만 _FRAGILE 부착
    """
    sc = qr.get("sort_code")
    if sc:
        return sc
    region = qr.get("region", "")
    pkg = qr.get("package_type", "BOX")
    base = f"{region}_{pkg}"
    if qr.get('fragile') and pkg == 'BOX':
        base += "_FRAGILE"
    return base


# =========================
# 렌즈 왜곡 보정
# =========================
_undistort_map = None
_undistort_loaded = False


def _try_load_calibration(w, h):
    global _undistort_map, _undistort_loaded
    _undistort_loaded = True
    candidates = [
        os.path.join(BASE_DIR, CALIBRATION_FILE),
        CALIBRATION_FILE,
    ]
    calib_path = None
    for p in candidates:
        if os.path.exists(p):
            calib_path = p
            break
    if calib_path is None:
        print(f"[CAMERA] 캘리브레이션 파일 없음 — 보정 안 함")
        print(f"        찾아본 경로:")
        for p in candidates:
            print(f"          - {os.path.abspath(p)}")
        return
    try:
        data = np.load(calib_path)
        camera_matrix = data["camera_matrix"]
        dist_coeffs = data["dist_coeffs"]
        new_matrix, _ = cv2.getOptimalNewCameraMatrix(
            camera_matrix, dist_coeffs, (w, h), CALIB_ALPHA, (w, h)
        )
        mapx, mapy = cv2.initUndistortRectifyMap(
            camera_matrix, dist_coeffs, None, new_matrix,
            (w, h), cv2.CV_32FC1
        )
        _undistort_map = (mapx, mapy)
        print(f"[CAMERA] 캘리브레이션 로드 성공 (alpha={CALIB_ALPHA}) ({calib_path})")
    except Exception as e:
        print(f"[CAMERA] 캘리브레이션 로드 실패: {e}")


def undistort_frame_for_display(frame):
    global _undistort_loaded
    if not USE_CALIBRATION_DISPLAY:
        return frame
    h, w = frame.shape[:2]
    if not _undistort_loaded:
        _try_load_calibration(w, h)
    if _undistort_map is None:
        return frame
    mapx, mapy = _undistort_map
    return cv2.remap(frame, mapx, mapy, cv2.INTER_LINEAR)


def undistort_frame_for_processing(frame):
    global _undistort_loaded
    if not USE_CALIBRATION:
        return frame
    h, w = frame.shape[:2]
    if not _undistort_loaded:
        _try_load_calibration(w, h)
    if _undistort_map is None:
        return frame
    mapx, mapy = _undistort_map
    return cv2.remap(frame, mapx, mapy, cv2.INTER_CUBIC)


_qr_undistort_map = None
_qr_calib_loaded = False

def _undistort_for_qr(frame):
    global _qr_undistort_map, _qr_calib_loaded
    if frame is None:
        return None
    h, w = frame.shape[:2]
    if not _qr_calib_loaded:
        _qr_calib_loaded = True
        candidates = [os.path.join(BASE_DIR, CALIBRATION_FILE), CALIBRATION_FILE]
        path = next((p for p in candidates if os.path.exists(p)), None)
        if path is None:
            _qr_undistort_map = None
        else:
            try:
                data = np.load(path)
                cm = data["camera_matrix"]
                dc = data["dist_coeffs"]
                newcm, _ = cv2.getOptimalNewCameraMatrix(cm, dc, (w, h), 1.0, (w, h))
                mapx, mapy = cv2.initUndistortRectifyMap(
                    cm, dc, None, newcm, (w, h), cv2.CV_32FC1)
                _qr_undistort_map = (mapx, mapy)
            except Exception:
                _qr_undistort_map = None
    if _qr_undistort_map is None:
        return None
    mapx, mapy = _qr_undistort_map
    return cv2.remap(frame, mapx, mapy, cv2.INTER_CUBIC)


def get_ocr_roi(frame):
    h, w = frame.shape[:2]
    rx1, ry1, rx2, ry2 = OCR_ROI_RATIO
    return int(w * rx1), int(h * ry1), int(w * rx2), int(h * ry2)


def draw_overlay(frame, state_label, debug_corners=None, debug_text=None, box_center=None):
    out = frame.copy()
    h, w = out.shape[:2]
    x1, y1, x2, y2 = get_ocr_roi(out)
    color = {
        "IDLE":      (180, 180, 180),
        "PREPARING": (0, 165, 255),
        "SCANNING":  (0, 255, 255),
        "COOLDOWN":  (180, 130, 255),
    }.get(state_label, (255, 0, 0))
    cv2.rectangle(out, (x1, y1), (x2, y2), color, 2)
    line_x = int(w * TRIGGER_LINE_RATIO)
    cv2.line(out, (line_x, 0), (line_x, h), (0, 0, 255), 2)
    cv2.putText(out, "TRIGGER", (line_x + 8, 30),
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 255), 2)
    cv2.putText(out, f"VISION [{state_label}]", (10, 30),
                cv2.FONT_HERSHEY_SIMPLEX, 0.8, color, 2)
    if box_center is not None:
        bcx, bcy = box_center
        cv2.circle(out, (int(bcx), int(bcy)), 10, (255, 0, 255), -1)
    if debug_corners is not None and len(debug_corners) == 4:
        pts = np.array(debug_corners, dtype=np.int32)
        cv2.polylines(out, [pts], isClosed=True, color=(0, 255, 0), thickness=2)
    if debug_text:
        cv2.putText(out, debug_text, (10, 60),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (200, 200, 200), 2)
    return out


# =========================
# QR / OCR 헬퍼
# =========================
def _normalize_package_type(value):
    v = str(value or "").strip().upper()
    if v in ("일반", "BOX"):
        return "BOX"
    if v in ("비닐", "VINYL"):
        return "VINYL"
    return "BOX"


def _zero_pad_invoice(s):
    s = str(s or "").strip()
    if s.isdigit() and len(s) <= 3:
        return s.zfill(3)
    return s


def _is_fragile(kv):
    """
    fragile 여부 결정.
    1) fragile 키가 True/1/yes/파손주의
    2) handling 키가 "파손주의" (너 QR 데이터의 실제 키)
    """
    if kv.get("fragile", "").lower() in ("true", "1", "yes", "파손주의"):
        return True
    if kv.get("handling", "").strip() == "파손주의":
        return True
    return False


def parse_qr_text(qr_text):
    qr_text = qr_text.strip()
    if qr_text.startswith("{"):
        try:
            data = json.loads(qr_text)
        except json.JSONDecodeError:
            return None
        return {
            "invoice_no":     _zero_pad_invoice(data.get("invoice_no", "-")),
            "region":         data.get("region", "-"),
            "package_type":   _normalize_package_type(data.get("package_type", "BOX")),
            "sort_code":      data.get("sort_code"),
            "recipient_name": data.get("recipient_name") or data.get("name", "-"),
            "fragile":        bool(data.get("fragile", False))
                              or data.get("handling", "") == "파손주의",
            "priority":       str(data.get("priority", "NORMAL")).upper(),
            "created_at":     data.get("created_at"),
            "raw_text":       qr_text,
        }
    if "=" in qr_text and ";" in qr_text:
        kv = {}
        for part in qr_text.split(";"):
            if "=" in part:
                k, v = part.split("=", 1)
                kv[k.strip()] = v.strip()
        if "invoice_no" in kv:
            return {
                "invoice_no":     _zero_pad_invoice(kv.get("invoice_no", "-")),
                "region":         kv.get("region", "-"),
                "package_type":   _normalize_package_type(kv.get("type", "BOX")),
                "sort_code":      kv.get("sort_code"),
                "recipient_name": kv.get("name", "-"),
                "fragile":        _is_fragile(kv),
                "priority":       kv.get("priority", "NORMAL").upper(),
                "created_at":     kv.get("created_at"),
                "raw_text":       qr_text,
            }
    parts = [p.strip() for p in qr_text.split("|")]
    if len(parts) < 2:
        return None
    return {
        "invoice_no":     _zero_pad_invoice(parts[1] if len(parts) > 1 and parts[1] else "-"),
        "region":         parts[0] if parts[0] else "-",
        "package_type":   _normalize_package_type(parts[2] if len(parts) > 2 and parts[2] else "BOX"),
        "sort_code":      None,
        "recipient_name": parts[3] if len(parts) > 3 and parts[3] else "-",
        "fragile":        False,
        "priority":       "NORMAL",
        "created_at":     None,
        "raw_text":       qr_text,
    }


def _unwarp_box(frame, box_corners):
    if not box_corners or len(box_corners) != 4:
        return None
    pts = np.array(box_corners, dtype=np.float32)
    s = pts.sum(axis=1)
    diff = np.diff(pts, axis=1)
    tl = pts[np.argmin(s)]
    br = pts[np.argmax(s)]
    tr = pts[np.argmin(diff)]
    bl = pts[np.argmax(diff)]
    src = np.array([tl, tr, br, bl], dtype=np.float32)
    w = int(max(np.linalg.norm(tr - tl), np.linalg.norm(br - bl)))
    h = int(max(np.linalg.norm(bl - tl), np.linalg.norm(br - tr)))
    if w < 50 or h < 50:
        return None
    dst = np.array([[0, 0], [w, 0], [w, h], [0, h]], dtype=np.float32)
    M = cv2.getPerspectiveTransform(src, dst)
    return cv2.warpPerspective(frame, M, (w, h))


def _remove_dark_stripes(img):
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY) if len(img.shape) == 3 else img.copy()
    _, dark = cv2.threshold(gray, 50, 255, cv2.THRESH_BINARY_INV)
    h_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (25, 5))
    dark = cv2.morphologyEx(dark, cv2.MORPH_CLOSE, h_kernel)
    if len(img.shape) == 2:
        img = cv2.cvtColor(img, cv2.COLOR_GRAY2BGR)
    result = cv2.inpaint(img, dark, 5, cv2.INPAINT_TELEA)
    return result


def try_qr_aggressive(frame, box_corners=None):
    if frame is None:
        return None
    h, w = frame.shape[:2]
    if box_corners and len(box_corners) == 4:
        xs = [p[0] for p in box_corners]
        ys = [p[1] for p in box_corners]
        pad = 80
        x1 = max(0, min(xs) - pad)
        y1 = max(0, min(ys) - pad)
        x2 = min(w, max(xs) + pad)
        y2 = min(h, max(ys) + pad)
        target = frame[y1:y2, x1:x2]
    else:
        target = frame
    if target is None or target.size == 0:
        return None
    for scale, rot_code, label in [
        (2.5, cv2.ROTATE_180,  "2.5x 180°"),
        (2.5, None,            "2.5x 0°"),
    ]:
        scaled = cv2.resize(target, None, fx=scale, fy=scale,
                            interpolation=cv2.INTER_LINEAR)
        img = scaled if rot_code is None else cv2.rotate(scaled, rot_code)
        qr_text, _ = detect_qr(img)
        if qr_text:
            print(f"[QR-DEBUG] 성공: {label}")
            return qr_text
    return None


def _ocr_candidate_score(c):
    return (c["field_count"], c["confidence"])


def try_ocr_from_image(roi_image):
    """
    OCR 시도. 0°/180° 둘 다 돌려서 best 선택.
    채택 기준 강화: invoice_no가 숫자이거나 region이 추출돼야 채택.
    헛소리 텍스트(이름만 잡힌 거 등)는 채택 안 함.
    """
    if roi_image is None or roi_image.size == 0:
        return None

    h0, w0 = roi_image.shape[:2]
    max_side = 500
    if max(h0, w0) > max_side:
        scale = max_side / max(h0, w0)
        roi_image = cv2.resize(roi_image, None, fx=scale, fy=scale,
                               interpolation=cv2.INTER_AREA)

    best = None
    for angle, rot_code in [(0, None), (180, cv2.ROTATE_180)]:
        img = roi_image if rot_code is None else cv2.rotate(roi_image, rot_code)
        ocr_result = detect_ocr(img)
        if not ocr_result:
            print(f"[OCR-DEBUG] angle={angle}: detect_ocr 결과 None")
            continue
        text = ocr_result.get("text", "").strip()
        confidence = float(ocr_result.get("confidence", 0.0))
        if not text:
            print(f"[OCR-DEBUG] angle={angle}: 빈 텍스트")
            continue
        fields = parse_ocr_fields(text)
        print(f"[OCR-DEBUG] angle={angle}: text='{text[:80]}' "
              f"conf={confidence:.2f} fields={fields['field_count']}/3 "
              f"valid={fields['valid']}")
        candidate = {
            "invoice_no":  _zero_pad_invoice(fields["invoice_no"]),
            "region":      fields["region"],
            "region_code": fields["region_code"],
            "name":        fields["name"],
            "field_count": fields["field_count"],
            "confidence":  confidence,
            "raw_text":    text,
            "ocr_angle":   angle,
        }
        if fields["valid"]:
            # 그래도 invoice_no가 숫자인지 한 번 더 검증
            inv = candidate["invoice_no"].replace(" ", "")
            if inv and inv != "-" and inv.isdigit():
                return candidate
        if best is None or _ocr_candidate_score(candidate) > _ocr_candidate_score(best):
            best = candidate

    # 변경: invoice_no가 숫자이거나 region이 있어야만 채택
    has_valid_invoice = (
        best
        and best.get("invoice_no")
        and best["invoice_no"] != "-"
        and best["invoice_no"].replace(" ", "").isdigit()
    )
    has_region = (
        best
        and best.get("region_code")
        and best["region_code"] != "-"
    )

    if has_valid_invoice or has_region:
        print(f"[OCR-DEBUG] 최종 채택: angle={best['ocr_angle']} "
              f"invoice={best.get('invoice_no')} region={best.get('region_code')} "
              f"fields={best['field_count']}/3 conf={best['confidence']:.2f}")
        return best

    print(f"[OCR-DEBUG] 채택 거부: invoice_no="
          f"{best.get('invoice_no') if best else None} "
          f"region={best.get('region_code') if best else None} "
          f"(텍스트 불량)")
    return None


def _digits_only(s):
    return re.sub(r"[^0-9]", "", str(s or ""))


def cross_check(qr, ocr):
    qr_inv = _digits_only(qr.get("invoice_no"))
    ocr_inv = _digits_only(ocr.get("invoice_no"))
    if qr_inv and ocr_inv and len(ocr_inv) >= 1:
        if qr_inv.zfill(3) == ocr_inv.zfill(3):
            return True, "INVOICE_MATCH"
        if qr_inv.endswith(ocr_inv) or qr_inv.startswith(ocr_inv):
            return True, "INVOICE_PARTIAL_MATCH"
        return False, "INVOICE_MISMATCH"
    qr_region = str(qr.get("region", "")).strip()
    ocr_region = str(ocr.get("region", "")).strip()
    ocr_region_code = str(ocr.get("region_code", "")).strip()
    if qr_region and ocr_region != "-":
        if qr_region == ocr_region or qr_region == ocr_region_code:
            return True, "REGION_MATCH_ONLY"
        if qr_region in ocr_region or ocr_region_code in qr_region:
            return True, "REGION_MATCH_ONLY"
        return False, "REGION_MISMATCH"
    return True, "NO_COMPARISON"


# =========================
# 박스 검출
# =========================
class BoxDetector:
    def __init__(self):
        self.bg_for_motion = cv2.createBackgroundSubtractorMOG2(
            history=500, varThreshold=25, detectShadows=False,
        )
        self._last_corners = None
        self._last_center = None
        self._last_debug = ""

    def update_motion(self, roi, learning_rate=0.001):
        if roi is None or roi.size == 0:
            return 0.0
        mask = self.bg_for_motion.apply(roi, learningRate=learning_rate)
        return cv2.countNonZero(mask) / float(mask.size)

    def is_box_fully_in_roi(self, frame):
        x1, y1, x2, y2 = get_ocr_roi(frame)
        h, w = frame.shape[:2]
        roi_area = (x2 - x1) * (y2 - y1)
        min_area = roi_area * MIN_BOX_AREA_RATIO
        max_area = (w * h) * 0.85
        
        # [수정됨] 기존 명도 기준 흑백 변환 대신 Red 채널 분리 사용 (초록색 배경에서 박스 추출에 유리)
        gray = frame[:, :, 2]
        blurred = cv2.GaussianBlur(gray, (5, 5), 0)
        edges = cv2.Canny(blurred, 40, 130)
        
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (5, 5))
        edges = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, kernel)
        contours, _ = cv2.findContours(edges, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        
        if not contours:
            self._last_corners = None
            self._last_debug = "no contour"
            return False
        candidates = []
        for c in contours:
            area = cv2.contourArea(c)
            if area < min_area or area > max_area:
                continue
            M = cv2.moments(c)
            if M["m00"] == 0:
                continue
            cx = int(M["m10"] / M["m00"])
            cy = int(M["m01"] / M["m00"])
            if not (x1 < cx < x2 and y1 < cy < y2):
                continue
            candidates.append((area, c, cx, cy))
            
        if not candidates:
            all_areas = sorted([cv2.contourArea(c) for c in contours], reverse=True)[:3]
            self._last_corners = None
            self._last_debug = (
                f"ROI 내 박스 후보 없음 "
                f"(min={int(min_area)}, max={int(max_area)}, "
                f"top={[int(a) for a in all_areas]})"
            )
            return False
            
        candidates.sort(key=lambda x: x[0], reverse=True)
        area, c, cx, cy = candidates[0]
        rect = cv2.minAreaRect(c)
        box_pts = cv2.boxPoints(rect)
        corners = [(int(px), int(py)) for px, py in box_pts]
        self._last_corners = corners
        self._last_center = (cx, cy)
        bx, by, bw_i, bh_i = cv2.boundingRect(c)
        ix1 = max(x1, bx); iy1 = max(y1, by)
        ix2 = min(x2, bx + bw_i); iy2 = min(y2, by + bh_i)
        inter_area = max(0, ix2 - ix1) * max(0, iy2 - iy1)
        box_area = bw_i * bh_i
        overlap_ratio = inter_area / box_area if box_area > 0 else 0.0
        center_in_roi = (x1 < cx < x2 and y1 < cy < y2)
        
        if center_in_roi and overlap_ratio >= 0.4:
            self._last_debug = f"OK area={int(area)} overlap={overlap_ratio:.0%} cx={cx}"
            return True
        self._last_debug = f"박스 잡힘 (overlap={overlap_ratio:.0%}, center={center_in_roi})"
        return False

    @property
    def last_center(self):
        return getattr(self, "_last_center", None)

    @property
    def last_corners(self):
        return self._last_corners

    @property
    def last_debug(self):
        return self._last_debug


# =========================
# 스캔 상태 머신
# =========================
class ScanController:
    IDLE      = "IDLE"
    PREPARING = "PREPARING"
    SCANNING  = "SCANNING"
    COOLDOWN  = "COOLDOWN"

    def __init__(self, agent):
        self.agent = agent
        self.detector = BoxDetector()
        self.state = self.IDLE
        self.state_lock = threading.Lock()
        self.presence_count = 0
        self.full_box_count = 0
        self.absence_count = 0
        self.tick_count = 0
        self.scan_id = None
        self._prev_cx = None

    def _set_state(self, new_state):
        with self.state_lock:
            self.state = new_state

    def tick(self, frame):
        if frame is None:
            return
        self.tick_count += 1
        x1, y1, x2, y2 = get_ocr_roi(frame)
        roi = frame[y1:y2, x1:x2]
        motion_ratio = self.detector.update_motion(roi)
        present = motion_ratio > MOTION_THRESHOLD
        state = self.state
        if state == self.IDLE:
            self._tick_idle(present)
        elif state == self.PREPARING:
            check_corners = (self.tick_count % CORNER_CHECK_EVERY_N == 0)
            self._tick_preparing(frame, present, check_corners)
        elif state == self.SCANNING:
            pass
        elif state == self.COOLDOWN:
            self._tick_cooldown(present)

    def _tick_idle(self, present):
        if present:
            self.presence_count += 1
            if self.presence_count >= PRESENCE_FRAMES:
                self.presence_count = 0
                self.full_box_count = 0
                self.absence_count = 0
                self._set_state(self.PREPARING)
                print("[VISION] 움직임 감지 → 준비 (PREPARING)")
        else:
            self.presence_count = max(0, self.presence_count - 1)

    def _tick_preparing(self, frame, present, check_corners):
        if not present:
            self.absence_count += 1
            if self.absence_count >= ABORT_FRAMES:
                self._set_state(self.IDLE)
                self.full_box_count = 0
                self.absence_count = 0
                self._prev_cx = None
                print(f"[VISION] 박스 사라짐 → IDLE 복귀")
                return
        else:
            self.absence_count = 0
        if not check_corners:
            return
        detected = self.detector.is_box_fully_in_roi(frame)
        center = self.detector.last_center
        if not detected or center is None:
            return
        cx = center[0]
        w = frame.shape[1]
        line_x = int(w * TRIGGER_LINE_RATIO)
        prev_cx = getattr(self, "_prev_cx", None)
        near_line = abs(cx - line_x) <= TRIGGER_TOLERANCE
        crossed = (prev_cx is not None and prev_cx < line_x <= cx)
        if near_line or crossed:
            print(f"[VISION] 박스 중심이 트리거 라인 통과 (cx={cx}, line={line_x}) → 캡처")
            self._prev_cx = None
            self._start_scan()
            return
        self._prev_cx = cx
        if self.tick_count % 20 == 0:
            print(f"[VISION] 박스 추적 중 (cx={cx}, line={line_x})")

    def _start_scan(self):
        self.full_box_count = 0
        ms = datetime.now().strftime("%f")[:3]
        self.scan_id = datetime.now().strftime("scan_%H%M%S_") + ms
        self._set_state(self.SCANNING)
        print(f"\n[VISION] 4모서리 ROI 진입 확인 → 스캔 시작 ({self.scan_id})")
        threading.Thread(target=self._scan_worker, daemon=True).start()

    def _scan_worker(self):
        scan_start = time.time()
        with frame_lock:
            if latest_frame is None:
                print("[VISION] 스캔 시작했으나 프레임 없음")
                self.agent.publish_scan_failed(self.scan_id, "NO_FRAME")
                self._set_state(self.COOLDOWN)
                return
            frozen_frame = latest_frame.copy()
        box_corners = self.detector.last_corners
        h, w = frozen_frame.shape[:2]
        if box_corners and len(box_corners) == 4:
            xs = [p[0] for p in box_corners]
            ys = [p[1] for p in box_corners]
            pad = 60
            bx1 = max(0, min(xs) - pad)
            by1 = max(0, min(ys) - pad)
            bx2 = min(w, max(xs) + pad)
            by2 = min(h, max(ys) + pad)
        else:
            bx1, by1, bx2, by2 = get_ocr_roi(frozen_frame)
        box_roi = frozen_frame[by1:by2, bx1:bx2].copy()
        try:
            os.makedirs("debug_scans", exist_ok=True)
            cv2.imwrite(f"debug_scans/{self.scan_id}_roi.jpg", box_roi)
            cv2.imwrite(f"debug_scans/{self.scan_id}_full.jpg", frozen_frame)
            print(f"[DEBUG] 이미지 저장: debug_scans/{self.scan_id}_(roi|full).jpg")
        except Exception as e:
            print(f"[DEBUG] 저장 실패: {e}")
            
        qr_result = None
        QR_MAX_TRIES = 1
        for attempt in range(QR_MAX_TRIES):
            if attempt == 0:
                qr_frame = frozen_frame
            else:
                with frame_lock:
                    qr_frame = latest_frame.copy() if latest_frame is not None else frozen_frame
            qr_text = try_qr_aggressive(qr_frame, box_corners=box_corners)
            if qr_text:
                parsed = parse_qr_text(qr_text)
                if parsed:
                    qr_result = parsed
                    print(f"[VISION] QR 인식 성공 (시도 {attempt+1}):")
                    print(f"        raw_text     : {parsed.get('raw_text', '')[:100]}")
                    print(f"        invoice_no   : {parsed.get('invoice_no')}")
                    print(f"        region       : {parsed.get('region')}")
                    print(f"        package_type : {parsed.get('package_type')}")
                    print(f"        sort_code    : {parsed.get('sort_code')}")
                    print(f"        recipient    : {parsed.get('recipient_name')}")
                    print(f"        fragile      : {parsed.get('fragile')}")
                    print(f"        priority     : {parsed.get('priority')}")
                    print(f"        created_at   : {parsed.get('created_at')}")
                    break
                else:
                    print(f"[VISION] QR 텍스트 잡혔으나 파싱 실패: {qr_text[:100]}")
            time.sleep(0.1)
            
        if qr_result is None:
            print(f"[VISION] QR {QR_MAX_TRIES}회 시도 실패 → OCR 폴백으로 진행")
        print(f"[VISION] OCR 시도 시작 (QR 결과: {'있음' if qr_result else '없음'})")
        
        ocr_start = time.time()
        ocr = try_ocr_from_image(box_roi)
        ocr_elapsed = time.time() - ocr_start
        if ocr:
            print(f"[VISION] OCR 결과 ({ocr_elapsed:.1f}s): "
                  f"text='{ocr['raw_text'][:60]}' "
                  f"invoice={ocr['invoice_no']} region={ocr['region_code']}")
        else:
            print(f"[VISION] OCR 실패 ({ocr_elapsed:.1f}s): 텍스트 추출 불가")
            
        self._finalize_and_publish(qr_result, ocr)
        self.absence_count = 0
        self._set_state(self.COOLDOWN)
        print("[VISION] 스캔 종료 → COOLDOWN (ROI 비워지길 대기)\n")

    def _finalize_and_publish(self, qr, ocr):
        """
        QR/OCR 결과 결합 후 적절한 발행:
        - QR 성공 + OCR 성공: 교차 검증 통과 시 success, 실패 시 mismatch
        - QR 성공 + OCR 없음: success (STRICT_CROSSCHECK=False 기준)
        - QR 실패 + OCR 성공: OCR이 유효한 데이터면 success, 헛소리면 fail
        - QR 실패 + OCR 실패: fail
        """
        sid = self.scan_id
        if qr and ocr:
            ok, reason = cross_check(qr, ocr)
            if ok:
                self.agent.publish_qr_success(sid, qr, ocr, reason)
            else:
                self.agent.publish_mismatch(sid, qr, ocr, reason)
        elif qr and not ocr:
            if STRICT_CROSSCHECK:
                self.agent.publish_mismatch(sid, qr, None, "OCR_UNREADABLE")
            else:
                self.agent.publish_qr_success(sid, qr, None, "OCR_UNREADABLE_IGNORED")
        elif not qr and ocr:
            # OCR 결과에 invoice_no(숫자) 또는 region이 진짜로 있어야 의미 있음
            inv = str(ocr.get("invoice_no", "")).replace(" ", "")
            has_invoice = inv and inv != "-" and inv.isdigit()
            has_region = ocr.get("region_code") and ocr["region_code"] != "-"
            if has_invoice or has_region:
                self.agent.publish_ocr_success(sid, ocr)
            else:
                print(f"[VISION] OCR 결과가 무의미 (invoice/region 둘 다 없음) → 실패 처리")
                self.agent.publish_scan_failed(sid, "OCR_NO_USEFUL_DATA")
        else:
            self.agent.publish_scan_failed(sid, "SCAN_TIMEOUT")

    def _tick_cooldown(self, present):
        if not present:
            self.absence_count += 1
            if self.absence_count >= ABSENCE_FRAMES_RESET:
                self.absence_count = 0
                self.presence_count = 0
                self.full_box_count = 0
                self._set_state(self.IDLE)
                print("[VISION] IDLE — 다음 박스 대기 중")
        else:
            self.absence_count = 0


# =========================
# Vision Agent
# =========================
class VisionAgent(AgentBase):
    def __init__(self, broker_host):
        super().__init__(
            broker_host, DEVICE_ID, "VISION",
            VISION_COMMAND, "parcel/vision/result"
        )

    def _on_connect(self, client, userdata, flags, rc):
        super()._on_connect(client, userdata, flags, rc)
        self.publish_event(f"parcel/{self.device_id}/status", {
            "status":      "ONLINE",
            "device_id":   self.device_id,
            "device_type": self.device_type,
            "stream_url":  STREAM_URL,
        })

    def handle_command(self, data):
        cmd = str(data.get("command", "")).strip().upper()
        if cmd == "START_SCAN":
            print("[VISION] START_SCAN 무시 (자가 트리거 모델)")

    def _safe_publish(self, topic, payload, kind):
        """발행 시도. 어떤 토픽으로 어떤 결과로 발행됐는지 명시 로그."""
        try:
            print(f"[PUBLISH-DEBUG] kind={kind} topic={topic} "
                  f"keys={list(payload.keys())}")
            self.publish_event(topic, payload)
            print(f"[PUBLISH-DEBUG] kind={kind} 발행 완료")
        except Exception as e:
            print(f"[PUBLISH-DEBUG] kind={kind} 발행 실패: {e}")

    def publish_qr_success(self, scan_id, qr, ocr, verify_reason):
        # v6.0: fragile 반영한 sort_code 결정
        sort_code = derive_sort_code(qr)
        payload = {
            "scan_id":        scan_id,
            "package_id":     None,
            "invoice_no":     qr["invoice_no"],
            "region":         qr["region"],
            "package_type":   qr["package_type"],
            "sort_code":      sort_code,
            "recipient_name": qr.get("recipient_name", "-"),
            "fragile":        qr.get("fragile", False),
            "priority":       qr.get("priority", "NORMAL"),
            "created_at":     qr.get("created_at"),
            "scan_method":    "QR",
            "verify_reason":  verify_reason,
            "ocr_text":       ocr["raw_text"] if ocr else "",
            "ocr_confidence": ocr["confidence"] if ocr else 0.0,
            "time_text":      now_text(),
        }
        if ocr:
            print(f"[VISION] QR+OCR 교차검증 성공: {sort_code} ({verify_reason})")
        else:
            print(f"[VISION] QR 단독 성공: {sort_code} ({verify_reason})")
        self._safe_publish(VISION_SCAN_RESULT, payload, "qr_success")

    def publish_ocr_success(self, scan_id, ocr):
        payload = {
            "scan_id":        scan_id,
            "package_id":     None,
            "invoice_no":     ocr["invoice_no"],
            "region_code":    ocr["region_code"],
            "recipient_name": ocr["name"],
            "scan_method":    "OCR",
            "ocr_text":       ocr["raw_text"],
            "ocr_confidence": ocr["confidence"],
            "ocr_angle":      ocr["ocr_angle"],
            "time_text":      now_text(),
        }
        print(f"[VISION] OCR 폴백 성공: invoice={ocr['invoice_no']} "
              f"region={ocr['region_code']} name={ocr['name']}")
        self._safe_publish(VISION_SCAN_RESULT, payload, "ocr_success")

    def publish_mismatch(self, scan_id, qr, ocr, reason):   # ← 스페이스 4칸 (클래스 안)
        image_path = f"debug_scans/{scan_id}_roi.jpg"
        payload = {
            "type":           "QR_OCR_MISMATCH",
            "scan_id":        scan_id,
            "package_id":     None,
            "reason":         reason,
            "qr_invoice":     qr.get("invoice_no") if qr else None,
            "qr_region":      qr.get("region") if qr else None,
            "ocr_invoice":    ocr.get("invoice_no") if ocr else None,
            "ocr_region":     ocr.get("region") if ocr else None,
            "ocr_confidence": ocr.get("confidence") if ocr else 0.0,
            "image_path":     image_path,
            "time_text":      now_text(),
        }
        print(f"[VISION] QR/OCR 불일치 → DEFECT: {reason}")
        self.publish_event(VISION_FAIL, payload)

    def publish_scan_failed(self, scan_id, reason):
        image_path = f"debug_scans/{scan_id}_roi.jpg"
        payload = {
            "type":       "SCAN_FAILED",
            "scan_id":    scan_id,
            "package_id": None,
            "reason":     reason,
            "image_path": image_path,
            "time_text":  now_text(),
        }
        print(f"[VISION] 스캔 실패 → WPF 알림 (reason={reason}, image={image_path})")
        self.publish_event(VISION_FAIL, payload)


# =========================
# 카메라 루프 + Flask
# =========================
def camera_loop(controller):
    global latest_frame, stream_frame
    cap = cv2.VideoCapture(CAMERA_INDEX, cv2.CAP_DSHOW)
    if not cap.isOpened():
        print("[CAMERA] 카메라 열기 실패")
        return
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, CAMERA_WIDTH)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CAMERA_HEIGHT)
    cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*'MJPG'))
    print("[CAMERA] 시작")
    while True:
        ret, frame = cap.read()
        if not ret or frame is None:
            time.sleep(0.03)
            continue
        frame = undistort_frame_for_processing(frame)
        controller.tick(frame)
        with frame_lock:
            latest_frame = frame.copy()
            in_prep = controller.state == ScanController.PREPARING
            stream_frame = draw_overlay(
                frame, controller.state,
                debug_corners=controller.detector.last_corners if in_prep else None,
                debug_text=controller.detector.last_debug if in_prep else None,
                box_center=controller.detector.last_center if in_prep else None,
            )


def mjpeg_generate():
    while True:
        with frame_lock:
            if stream_frame is None:
                time.sleep(0.03)
                continue
            frame = stream_frame.copy()
        ret, jpeg = cv2.imencode(
            ".jpg", frame,
            [int(cv2.IMWRITE_JPEG_QUALITY), STREAM_JPEG_QUALITY],
        )
        if not ret:
            continue
        yield (b"--frame\r\n"
               b"Content-Type: image/jpeg\r\n\r\n"
               + jpeg.tobytes() + b"\r\n")
        time.sleep(0.04)


@app.route("/stream")
def stream():
    return Response(mjpeg_generate(),
                    mimetype="multipart/x-mixed-replace; boundary=frame")


@app.route("/snapshot.jpg")
def snapshot():
    with frame_lock:
        if stream_frame is None:
            return Response("no frame yet", status=503)
        frame = stream_frame.copy()
    ret, jpeg = cv2.imencode(
        ".jpg", frame,
        [int(cv2.IMWRITE_JPEG_QUALITY), STREAM_JPEG_QUALITY],
    )
    if not ret:
        return Response("encode failed", status=500)
    return Response(jpeg.tobytes(), mimetype="image/jpeg",
                    headers={"Cache-Control": "no-store"})


@app.route("/health")
def health():
    with frame_lock:
        has_frame = latest_frame is not None
    return {"status": "ok", "has_frame": has_frame}


@app.route("/")
def index():
    return """<html><body style="margin:0;background:#222;text-align:center;">
<h3 style="color:#fff;font-family:sans-serif;">VISION STREAM TEST</h3>
<img src="/stream" style="max-width:100%;"/>
</body></html>"""


# =========================
# main
# =========================
if __name__ == "__main__":
    def _warmup_ocr():
        print("[OCR] 백그라운드 워밍업 시작...")
        import numpy as np
        dummy = np.zeros((100, 100, 3), dtype=np.uint8)
        detect_ocr(dummy)
        print("[OCR] 워밍업 완료 — 스캔 준비됨")
    threading.Thread(target=_warmup_ocr, daemon=True).start()

    # 시작 시 토픽 상수 출력 (디버깅용)
    print(f"[STARTUP] VISION_SCAN_RESULT = '{VISION_SCAN_RESULT}'")
    print(f"[STARTUP] VISION_FAIL = '{VISION_FAIL}'")
    print(f"[STARTUP] VISION_COMMAND = '{VISION_COMMAND}'")

    agent = VisionAgent(BROKER_HOST)
    controller = ScanController(agent)
    threading.Thread(target=camera_loop, args=(controller,), daemon=True).start()
    threading.Thread(
        target=lambda: app.run(
            host=STREAM_HOST, port=STREAM_PORT,
            threaded=True, use_reloader=False, debug=False,
        ),
        daemon=True,
    ).start()
    print(f"[FLASK] 스트림 서버 시작 {STREAM_URL}")
    print(f"[FLASK] 엔드포인트: /stream (MJPEG), /snapshot.jpg, /health, /")
    print("[VISION] 자가 트리거 모드 — 4모서리 ROI 진입 시 인식 시작")
    try:
        agent.connect()
    except KeyboardInterrupt:
        print("\n[VISION] 종료")
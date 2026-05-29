"""
Vision Agent (Simplified)
- ROI 영역에 상자 네 모서리가 완전히 들어오면 캡쳐 1장
- QR 인식 시도 → 성공하면 결과 발행
- QR 실패 → OCR 시도 → 성공하면 결과 발행
- 둘 다 실패 → 불량(DEFECT) 판정
"""

import os
import sys
import cv2
import time
import json
import threading
import traceback
from datetime import datetime
from flask import Flask, Response

# =========================
# 경로 설정
# =========================
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.abspath(os.path.join(BASE_DIR, ".."))

if PROJECT_DIR not in sys.path:
    sys.path.insert(0, PROJECT_DIR)

from common.agent_base import AgentBase

try:
    from common.topics import VISION_COMMAND, VISION_SCAN_RESULT, VISION_FAIL, BLACKBOX_EVENT
except Exception:
    from common.topics import VISION_COMMAND, VISION_SCAN_RESULT, VISION_FAIL
    BLACKBOX_EVENT = "parcel/blackbox/event"

from vision.qr_reader import detect_qr
from vision.ocr_reader import detect_ocr


# =========================
# 설정
# =========================
BROKER_HOST = "192.168.0.21"
DEVICE_ID = "vision"

STREAM_HOST = "0.0.0.0"
STREAM_PORT = 8081
STREAM_URL = f"http://192.168.0.6:{STREAM_PORT}/stream"

CAMERA_INDEX = 1
CAMERA_WIDTH = 640
CAMERA_HEIGHT = 480

# ROI 영역 (x1, y1, x2, y2) - 이 영역 안에서 녹색 비율로 상자 감지
ROI_BOX = (20, 20, 620, 460)

# 상자 감지 설정 (녹색 배경 대비)
BOX_STABLE_FRAMES = 5          # 연속 N프레임 감지되어야 캡쳐
COOLDOWN_SECONDS = 3.0         # 캡쳐 후 다음 감지까지 대기 시간

# OCR 설정
OCR_FIXED_ROI = (200, 150, 550, 380)   # OCR 크롭 영역
OCR_MIN_CONFIDENCE = 0.05             # 신뢰도 기준 낮춤 (필드 파싱 성공이 더 중요)
OCR_REQUIRED_MIN_FIELDS = 2
OCR_ROTATION_ANGLES = [0, 90, 180, 270]  # 전방향 회전 검사

# 저장 경로
CAPTURE_SAVE_DIR = os.path.join(os.getcwd(), "vision_captures")
DEFECT_SAVE_DIR = os.path.join(os.getcwd(), "vision_defect_logs")

# MJPEG 스트림 설정
STREAM_FPS = 12.0
STREAM_JPEG_QUALITY = 60


# =========================
# 전역 상태
# =========================
app = Flask(__name__)

latest_frame = None
frame_lock = threading.Lock()

package_seq = 1
vision_phase = "IDLE"  # IDLE / WAITING / RECOGNIZING

last_result = {
    "scan_type": "-",
    "package_id": "-",
    "invoice_no": "-",
    "region": "-",
    "package_type": "-",
    "sort_code": "-",
    "message": "READY"
}


# =========================
# 유틸
# =========================

def now_text():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def make_package_id():
    global package_seq
    pid = f"CAM_{datetime.now():%Y%m%d_%H%M%S}_{package_seq:04d}"
    package_seq += 1
    return pid


def safe_publish(topic, data):
    try:
        agent.publish_event(topic, data)
        print(f"[MQTT] publish topic={topic}")
    except Exception as e:
        print(f"[MQTT] publish 실패: {e}")


def save_image(directory, prefix, frame):
    """이미지를 지정 폴더에 저장하고 경로 반환"""
    if frame is None:
        return ""
    os.makedirs(directory, exist_ok=True)
    filename = f"{prefix}_{datetime.now():%Y%m%d_%H%M%S_%f}.jpg"
    path = os.path.join(directory, filename)
    cv2.imwrite(path, frame)
    return path


# =========================
# 녹색 배경 대비 상자 감지
# =========================

# 녹색 비율이 이 값 아래로 떨어지면 상자가 있다고 판단
# 빈 컨베이어 green≈0.61, 상자 올라오면 green≈0.42
GREEN_RATIO_THRESHOLD = 0.52

def detect_box_in_roi(frame):
    """
    ROI 영역 내 녹색 비율로 상자 유무 판단.
    녹색이 줄어들면 = 상자가 녹색 컨베이어를 가리고 있음.
    Returns: (detected: bool, green_ratio: float)
    """
    rx1, ry1, rx2, ry2 = ROI_BOX
    roi = frame[ry1:ry2, rx1:rx2]

    hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)
    green_mask = cv2.inRange(hsv, (40, 60, 30), (80, 255, 180))
    green_ratio = cv2.countNonZero(green_mask) / (roi.shape[0] * roi.shape[1])

    detected = green_ratio < GREEN_RATIO_THRESHOLD
    return detected, green_ratio


# =========================
# QR 인식
# =========================

def try_qr(frame):
    """QR 인식 시도 (0°/90°/180°/270° 회전, 해상도 업스케일). 성공하면 파싱된 dict 반환, 실패하면 None"""
    # 작은 QR 대응: 해상도를 2배로 키워서 시도
    # h, w = frame.shape[:2]
    # scale = 2.0 if max(h, w) < 800 else 1.5
    # upscaled = cv2.resize(frame, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)

    # rotations = [
    #     (0,   None),
    #     (90,  cv2.ROTATE_90_CLOCKWISE),
    #     (180, cv2.ROTATE_180),
    #     (270, cv2.ROTATE_90_COUNTERCLOCKWISE),
    # ]
    # # 원본 크기와 업스케일 둘 다 시도
    # for img_label, img in [("upscaled", upscaled), ("original", frame)]:
    #     for angle, rot_code in rotations:
    #         try:
    #             rotated = img if rot_code is None else cv2.rotate(img, rot_code)
    #             qr_text, bbox = detect_qr(rotated)
    #             if qr_text:
    #                 print(f"[QR] 인식 성공 ({img_label}, angle={angle}°): {qr_text}")
    #                 return parse_qr_payload(qr_text)
    #         except Exception as e:
    #             print(f"[QR] 예외 ({img_label}, angle={angle}°): {e}")
    # return None

    qr_big = cv2.resize(
        frame,
        None,
        fx=1.5,
        fy=1.5,
        interpolation=cv2.INTER_LINEAR
    )

    for i in range(3):
        qr_text, bbox = detect_qr(qr_big)

        if qr_text:
            print(f"[QR] {i + 1}번째 시도 성공:", qr_text)
            return parse_qr_payload(qr_text)

        print(f"[QR] {i + 1}번째 시도 실패")
        time.sleep(0.2)

    return None


def parse_qr_payload(qr_text):
    """QR 텍스트를 파싱하여 dict 반환 (JSON / key=value / pipe 형식 지원)"""
    raw = qr_text.strip()
    data = {
        "invoice_no": "UNKNOWN",
        "name": "-",
        "region": "UNKNOWN",
        "package_type": "BOX",
        "raw_text": raw,
    }

    if not raw:
        return data

    # JSON 형식
    if raw.startswith("{"):
        try:
            parsed = json.loads(raw)
            data["invoice_no"] = str(parsed.get("invoice_no", "UNKNOWN")).strip()
            data["name"] = str(parsed.get("name", "-")).strip()
            data["region"] = str(parsed.get("region", "UNKNOWN")).strip()
            data["package_type"] = str(parsed.get("package_type", "BOX")).strip().upper()
            return data
        except json.JSONDecodeError:
            pass

    # key=value;key=value 형식
    if "=" in raw:
        parts = raw.replace(",", ";").replace("|", ";").split(";")
        kv = {}
        for part in parts:
            if "=" in part:
                k, v = part.split("=", 1)
                kv[k.strip().lower()] = v.strip()
        data["invoice_no"] = kv.get("invoice_no", kv.get("invoice", "UNKNOWN"))
        data["name"] = kv.get("name", "-")
        data["region"] = kv.get("region", "UNKNOWN")
        data["package_type"] = kv.get("package_type", kv.get("type", "BOX")).upper()
        return data

    # REGION|INVOICE|TYPE 파이프 형식
    parts = raw.split("|")
    if len(parts) >= 2:
        data["region"] = parts[0].strip()
        data["invoice_no"] = parts[1].strip()
        if len(parts) >= 3:
            data["package_type"] = parts[2].strip().upper()
        if len(parts) >= 4:
            data["name"] = parts[3].strip()

    return data

# 흰 종이 찾기
def crop_text_label_area(frame):
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)

    # 흰 종이/밝은 라벨 영역 찾기
    _, th = cv2.threshold(gray, 160, 255, cv2.THRESH_BINARY)

    contours, _ = cv2.findContours(th, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    h, w = frame.shape[:2]
    candidates = []

    for cnt in contours:
        x, y, bw, bh = cv2.boundingRect(cnt)
        area = bw * bh

        if area < 3000:
            continue
        if bw < 80 or bh < 40:
            continue
        if bw > w * 0.95 or bh > h * 0.95:
            continue

        candidates.append((x, y, x + bw, y + bh, area))

    if not candidates:
        return frame

    # 가장 큰 흰 라벨 영역 선택
    x1, y1, x2, y2, _ = max(candidates, key=lambda c: c[4])

    margin = 40
    x1 = max(x1 - margin, 0)
    y1 = max(y1 - margin, 0)
    x2 = min(x2 + margin, w)
    y2 = min(y2 + margin, h)

    return frame[y1:y2, x1:x2].copy()


# =========================
# OCR 인식
# =========================

def try_ocr(frame):
    """
    OCR 인식 시도 (전방향 회전, early-exit).
    '송장번호' '이름' '지역' 등 라벨 키워드가 읽힌 방향만 정답으로 인정.
    라벨이 읽힌 = 글자가 바른 방향 = 그 결과가 정확함.
    """
    try:
        # 글자 크기 키우기 (1장만)
        h, w = frame.shape[:2]

        # OCR 입력을 너무 크게 만들지 않음
        # target_w = 360
        # if w > target_w:
        #     scale = target_w / w
        #     up = cv2.resize(frame, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
        # else:
        #     up = frame

        #up = frame

        up = crop_text_label_area(frame)

        # # 살짝 확대
        # up = cv2.resize(
        #     up,
        #     None,
        #     fx=1.3,
        #     fy=1.3,
        #     interpolation=cv2.INTER_LINEAR
        # )

        rotations = [
            (0,   None),
            #(90,  cv2.ROTATE_90_CLOCKWISE),
            (180, cv2.ROTATE_180),
            #(270, cv2.ROTATE_90_COUNTERCLOCKWISE),
        ]

        # 라벨 키워드 목록 (이것들이 읽혔다 = 글자 방향이 맞다)
        LABEL_KEYWORDS = ["송장", "번호", "이름", "성명", "지역", "권역"]

        best_with_label = None   # 라벨이 있는 결과 중 최선
        best_without_label = None  # 라벨 없는 결과 중 최선 (폴백)

        for angle, rot_code in rotations:
            rotated = up if rot_code is None else cv2.rotate(up, rot_code)

            start = time.time()
            result = detect_ocr(rotated)
            elapsed = time.time() - start

            if result is None or not isinstance(result, dict):
                continue

            text = result.get("text", "").strip()
            confidence = float(result.get("confidence", 0.0))

            # 라벨 키워드가 포함되어 있는지 확인
            has_label = any(kw in text for kw in LABEL_KEYWORDS)
            fields = parse_ocr_fields(text)
            fc = fields.get("field_count", 0)

            if has_label:
                # 라벨이 읽힌 방향 = 정답 방향
                if fields.get("valid", False):
                    # 2개 이상 필드 → 즉시 성공, 더 안 돌림
                    print(f"[OCR] 성공 (angle={angle}°): invoice={fields['invoice_no']}, name={fields['name']}, region={fields['region']}")
                    return _make_ocr_result(fields, confidence, text, angle)

                if best_with_label is None or fc > best_with_label[0]:
                    best_with_label = (fc, confidence, fields, text, angle)
            else:
                if best_without_label is None or fc > best_without_label[0]:
                    best_without_label = (fc, confidence, fields, text, angle)

        # 라벨이 읽힌 결과가 있으면 그걸 우선 사용
        if best_with_label and best_with_label[0] >= 1:
            _, confidence, fields, text, angle = best_with_label
            print(f"[OCR] 부분 성공 - 라벨 방향 (angle={angle}°, fields={fields['field_count']}): {text[:60]}")
            return _make_ocr_result(fields, confidence, text, angle)

        # 라벨 없는 결과라도 필드가 있으면 폴백
        if best_without_label and best_without_label[0] >= 1:
            _, confidence, fields, text, angle = best_without_label
            print(f"[OCR] 폴백 - 라벨 없음 (angle={angle}°, fields={fields['field_count']}): {text[:60]}")
            return _make_ocr_result(fields, confidence, text, angle)

        print("[OCR] 전방향 실패")
        return None

    except Exception as e:
        print(f"[OCR] 예외: {e}")
        traceback.print_exc()
        return None


def _make_ocr_result(fields, confidence, text, angle):
    return {
        "invoice_no": fields["invoice_no"],
        "name": fields["name"],
        "region": fields["region"],
        "region_code": fields["region_code"],
        "package_type": "-",
        "confidence": confidence,
        "raw_text": text,
        "scan_type": "OCR",
        "ocr_angle": angle,
    }


def parse_ocr_fields(text):
    """OCR 텍스트에서 송장번호/이름/지역 추출"""
    import re

    print("[OCR RAW]", text)

    # 라벨 오타 보정 (승장→송장, 숭장→송장 등)
    text = re.sub(r"[승숭중]\s*장\s*번\s*[호오흐]", "송장번호", text)
    text = re.sub(r"송\s*장\s*번\s*[호오흐]", "송장번호", text)
    text = re.sub(r"운\s*송\s*장\s*번\s*[호오흐]", "송장번호", text)
    text = re.sub(r"이\s*[름듬금릅]", "이름", text)
    text = re.sub(r"성\s*명", "이름", text)
    text = re.sub(r"지\s*[역여억익엽]", "지역", text)
    text = re.sub(r"권\s*역", "지역", text)

    # 구분자 정규화
    text = text.replace("：", ":").replace("；", ":").replace("·", ":")
    text = re.sub(r"(송장번호|이름|지역)\s*[\.。,，ㆍ\-]\s*", r"\1:", text)

    # 라벨 뒤에 공백 없이 바로 값이 붙은 경우 콜론 삽입
    # 예: "송장번호058" → "송장번호:058", "이름이승환" → "이름:이승환"
    text = re.sub(r"(송장번호)(\d)", r"\1:\2", text)
    text = re.sub(r"(이름)([가-힣])", r"\1:\2", text)
    text = re.sub(r"(지역)([가-힣])", r"\1:\2", text)

    # 라벨 뒤 공백도 콜론 처리
    text = re.sub(r"(송장번호|이름|지역)\s+(?=[0-9가-힣])", r"\1:", text)

    # 콜론 주변 공백 정리
    text = re.sub(r"\s*:\s*", ":", text)

    def extract(labels, stop_labels):
        label_pat = "|".join(re.escape(l) for l in labels)
        stop_pat = "|".join(re.escape(l) for l in stop_labels)
        m = re.search(rf"(?:{label_pat}):?\s*(.*?)(?=(?:{stop_pat}):?|$)", text)
        return m.group(1).strip(" :-_/|'\"") if m else ""

    invoice_raw = extract(["송장번호"], ["이름", "지역"])
    name_raw = extract(["이름"], ["송장번호", "지역"])
    region_raw = extract(["지역"], ["송장번호", "이름"])

    if not region_raw:
        region_match = re.search(
            r"(서울|부산|인천|대구|광주|대전|울산|세종|경기|강원|충북|충남|전북|전남|경북|경남|제주)",
            text
        )

        if region_match:
            region_raw = region_match.group(1)

    # OCR이 지역 라벨을 이상하게 읽었을 때 보정
    region_ocr_fix = {
        # 경기
        "우조구보우": "경기",
        "경7": "경기",
        "경71": "경기",
        "겨기": "경기",
        "경기E": "경기",
        "경기C": "경기",
        "경71도": "경기",
        "경기도0": "경기",
        "경기E도": "경기",

        "부신": "부산",
        "부싼": "부산",

        "인전": "인천",

        # 서울
        "서움": "서울",
        "시울": "서울",
        "서율": "서울",
        "서을": "서울",
        "서운": "서울",
        "서은": "서울",
        "시을": "서울",
        "서울E": "서울",
        "서울C": "서울",
    }

    for wrong, fixed in region_ocr_fix.items():
        if wrong in text:
            region_raw = fixed
            break


    # 송장번호: 숫자만 추출 + OCR 오인식 보정
    invoice_clean = invoice_raw
    invoice_clean = invoice_clean.replace("O", "0").replace("o", "0")
    invoice_clean = invoice_clean.replace("I", "1").replace("l", "1").replace("|", "1")
    invoice_clean = invoice_clean.replace("S", "5").replace("s", "5")
    invoice_clean = invoice_clean.replace("B", "8")
    invoice_clean = re.sub(r"[^0-9]", "", invoice_clean)

    # 라벨에서 못 뽑았으면 전체 텍스트에서 숫자 덩어리 찾기
    if not invoice_clean:
        digit_match = re.search(r"\d{2,}", text)
        if digit_match:
            invoice_clean = digit_match.group(0)

    invoice_no = invoice_clean if invoice_clean else "-"

    # 이름: 한글 2~5자
    name_match = re.search(r"[가-힣]{2,5}", name_raw)
    name = name_match.group(0) if name_match else "-"

    # 이름을 라벨에서 못 뽑았으면 전체에서 찾기 (라벨 키워드 제외)
    if name == "-":
        cleaned = re.sub(r"(송장번호|이름|지역|경기도?|서울|부산|인천|대구|광주|대전|울산|세종|강원|충[남북]|전[남북]|경[남북]|제주)", "", text)
        name_match2 = re.search(r"[가-힣]{2,5}", cleaned)
        if name_match2:
            name = name_match2.group(0)

    # 지역: 매칭
    region, region_code = match_region(region_raw + " " + text)

    field_count = sum(1 for v in [invoice_no, name, region] if v != "-")

    return {
        "invoice_no": invoice_no,
        "name": name,
        "region": region if region else "-",
        "region_code": region_code if region_code else "-",
        "field_count": field_count,
        "valid": field_count >= OCR_REQUIRED_MIN_FIELDS,
    }


REGION_MAP = [
    ("서울", "서울"), ("경기", "경기"), ("인천", "인천"),
    ("부산", "부산"), ("대구", "대구"), ("광주", "광주"),
    ("대전", "대전"), ("울산", "울산"), ("세종", "세종"),
    ("강원", "강원"), ("충북", "충북"), ("충남", "충남"),
    ("전북", "전북"), ("전남", "전남"), ("경북", "경북"),
    ("경남", "경남"), ("제주", "제주"),
]


# def match_region(text):
#     """텍스트에서 지역명 매칭"""
#     for keyword, code in REGION_MAP:
#         if keyword in text:
#             return keyword, code
#     return "", ""


def match_region(text):
    region_alias = {
        "경기": "경기도",
        "서울": "서울",
        "부산": "부산",
        "인천": "인천",
        "대구": "대구",
        "광주": "광주",
        "대전": "대전",
        "울산": "울산",
        "세종": "세종",
        "강원": "강원도",
        "충북": "충청북도",
        "충남": "충청남도",
        "전북": "전라북도",
        "전남": "전라남도",
        "경북": "경상북도",
        "경남": "경상남도",
        "제주": "제주도",
    }

    for short_name, full_name in region_alias.items():
        if short_name in text:
            return full_name, short_name

    return "-", "-"


# =========================
# 인식 파이프라인
# =========================

def run_recognition(frame, package_id):
    """
    QR → OCR → DEFECT 순서로 인식 시도.
    결과를 MQTT로 발행.
    """
    global vision_phase, last_result

    vision_phase = "RECOGNIZING"
    capture_path = save_image(CAPTURE_SAVE_DIR, f"capture_{package_id}", frame)
    print(f"\n[RECOGNITION] 시작: package_id={package_id}")

    # --- 1단계: QR 시도 ---
    print("[RECOGNITION] QR 인식 시도...")
    qr_data = try_qr(frame) # <- 주석처리
    # qr_data = None  # ← 강제 실패

    if qr_data:
        sort_code = f"{qr_data['region']}_{qr_data['package_type']}"
        result_data = {
            "type": "NORMAL",
            "package_id": package_id,
            "scan_type": "QR",
            "invoice_no": qr_data["invoice_no"],
            "name": qr_data.get("name", "-"),
            "region": qr_data["region"],
            "package_type": qr_data["package_type"],
            "sort_code": sort_code,
            "capture_image": capture_path,
            "timestamp": round(time.time(), 3),
            "time_text": now_text(),
        }
        safe_publish(VISION_SCAN_RESULT, result_data)

        last_result = {
            "scan_type": "QR", "package_id": package_id,
            "invoice_no": qr_data["invoice_no"], "region": qr_data["region"],
            "package_type": qr_data["package_type"], "sort_code": sort_code,
            "message": f"QR 정상 / {qr_data['invoice_no']}"
        }
        print(f"[RECOGNITION] QR 성공: {sort_code}")
        vision_phase = "IDLE"
        return

    # --- 2단계: OCR 시도 ---
    print("[RECOGNITION] QR 실패 → OCR 인식 시도...")
    ox1, oy1, ox2, oy2 = OCR_FIXED_ROI
    ocr_frame = frame[oy1:oy2, ox1:ox2].copy()

    ocr_data = try_ocr(ocr_frame)
    # ocr_data = try_ocr(frame)

    if ocr_data:
        sort_code = f"{ocr_data['region_code']}_{ocr_data.get('package_type', 'BOX')}"
        result_data = {
            "type": "NORMAL",
            "package_id": package_id,
            "scan_type": "OCR",
            "invoice_no": ocr_data["invoice_no"],
            "name": ocr_data.get("name", "-"),
            "region": ocr_data["region"],
            "package_type": ocr_data.get("package_type", "-"),
            "sort_code": sort_code,
            "confidence": ocr_data["confidence"],
            "raw_text": ocr_data.get("raw_text", ""),
            "capture_image": capture_path,
            "timestamp": round(time.time(), 3),
            "time_text": now_text(),
        }
        safe_publish(VISION_SCAN_RESULT, result_data)

        last_result = {
            "scan_type": "OCR", "package_id": package_id,
            "invoice_no": ocr_data["invoice_no"], "region": ocr_data["region"],
            "package_type": ocr_data.get("package_type", "-"), "sort_code": sort_code,
            "message": f"OCR 정상 / {ocr_data['invoice_no']}"
        }
        print(f"[RECOGNITION] OCR 성공: {sort_code}")
        vision_phase = "IDLE"
        return

    # --- 3단계: 불량 판정 ---
    print("[RECOGNITION] QR/OCR 모두 실패 → 불량 판정")
    defect_path = save_image(DEFECT_SAVE_DIR, f"defect_{package_id}", frame)

    defect_data = {
        "type": "DEFECT",
        "event_type": "분류실패",
        "package_id": package_id,
        "reason": "QR_OCR_BOTH_FAIL",
        "image_path": defect_path,
        "capture_image": capture_path,
        "timestamp": round(time.time(), 3),
        "time_text": now_text(),
    }
    safe_publish(VISION_FAIL, defect_data)
    safe_publish(BLACKBOX_EVENT, defect_data)

    last_result = {
        "scan_type": "DEFECT", "package_id": package_id,
        "invoice_no": "-", "region": "-",
        "package_type": "-", "sort_code": "-",
        "message": "불량 / QR·OCR 모두 실패"
    }
    vision_phase = "IDLE"


# =========================
# Vision Agent (MQTT)
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
            "status": "ONLINE",
            "device_id": self.device_id,
            "device_type": self.device_type,
            "stream_url": STREAM_URL,
        })

    def handle_command(self, data):
        cmd = str(data.get("command", "")).strip().upper()
        if cmd in ("START_SCAN", "SCAN", "START"):
            package_id = data.get("package_id", make_package_id())
            print(f"[MQTT] 수동 스캔 요청: {package_id}")
            frame = get_latest_frame()
            if frame is not None:
                threading.Thread(target=run_recognition, args=(frame, package_id), daemon=True).start()
            else:
                print("[MQTT] 프레임 없음 - 스캔 불가")


agent = VisionAgent(BROKER_HOST)


def get_latest_frame():
    with frame_lock:
        if latest_frame is None:
            return None
        return latest_frame.copy()


# =========================
# MJPEG 스트리밍
# =========================

def generate_mjpeg():
    interval = 1.0 / STREAM_FPS
    while True:
        frame = get_latest_frame()
        if frame is None:
            time.sleep(0.05)
            continue
        _, jpeg = cv2.imencode('.jpg', frame, [cv2.IMWRITE_JPEG_QUALITY, STREAM_JPEG_QUALITY])
        yield (b'--frame\r\n'
               b'Content-Type: image/jpeg\r\n\r\n' + jpeg.tobytes() + b'\r\n')
        time.sleep(interval)


@app.route('/stream')
def stream():
    return Response(generate_mjpeg(), mimetype='multipart/x-mixed-replace; boundary=frame')


@app.route('/status')
def status():
    return json.dumps({"phase": vision_phase, "last_result": last_result}, ensure_ascii=False)


# =========================
# 카메라 루프 (메인)
# =========================

def camera_loop():
    global latest_frame, vision_phase

    cap = cv2.VideoCapture(CAMERA_INDEX)
    if not cap.isOpened():
        print(f"[ERROR] 카메라 열기 실패 (index={CAMERA_INDEX})")
        return

    cap.set(cv2.CAP_PROP_FRAME_WIDTH, CAMERA_WIDTH)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CAMERA_HEIGHT)

    w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    print(f"[CAMERA] 시작: {w}x{h}")

    stable_count = 0
    last_capture_time = 0.0
    vision_phase = "IDLE"

    while True:
        ret, frame = cap.read()
        if not ret or frame is None:
            time.sleep(0.01)
            continue

        with frame_lock:
            latest_frame = frame.copy()

        # --- 상자 감지 ---
        display = frame.copy()
        rx1, ry1, rx2, ry2 = ROI_BOX
        now = time.time()

        detected, green_ratio = detect_box_in_roi(frame)

        if detected:
            stable_count += 1
        else:
            stable_count = 0

        # ROI 테두리
        if detected:
            roi_color = (0, 255, 0)   # 초록: 상자 감지됨
            roi_label = f"BOX DETECTED ({stable_count}/{BOX_STABLE_FRAMES}) green={green_ratio:.2f}"
        else:
            roi_color = (0, 255, 255)  # 노랑: 대기 중
            roi_label = f"WAITING BOX green={green_ratio:.2f}"

        cv2.rectangle(display, (rx1, ry1), (rx2, ry2), roi_color, 2)
        cv2.putText(display, roi_label, (rx1, ry1 - 10),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, roi_color, 2)

        # --- 캡쳐 조건: 안정적 감지 + 쿨다운 ---
        if (stable_count >= BOX_STABLE_FRAMES
                and now - last_capture_time >= COOLDOWN_SECONDS
                and vision_phase == "IDLE"):

            last_capture_time = now
            stable_count = 0
            package_id = make_package_id()

            print(f"\n[CAMERA] 상자 감지 완료 → 캡쳐 & 인식 시작: {package_id}")

            # 인식은 별도 스레드에서 실행 (카메라 루프 멈추지 않게)
            capture_frame = frame.copy()
            threading.Thread(
                target=run_recognition,
                args=(capture_frame, package_id),
                daemon=True
            ).start()

        # --- 하단 상태 패널 ---
        draw_status_panel(display)

        # imshow (로컬 디버그용, 없으면 주석 처리 가능)
        cv2.imshow("Vision Agent", display)
        if cv2.waitKey(1) & 0xFF == ord('q'):
            break

    cap.release()
    cv2.destroyAllWindows()


def draw_status_panel(frame):
    """하단에 현재 상태 표시"""
    h, w = frame.shape[:2]
    y_start = h - 80

    overlay = frame.copy()
    cv2.rectangle(overlay, (0, y_start), (w, h), (0, 0, 0), -1)
    cv2.addWeighted(overlay, 0.6, frame, 0.4, 0, frame)

    lines = [
        f"PHASE: {vision_phase}  |  SCAN: {last_result.get('scan_type', '-')}  |  PKG: {last_result.get('package_id', '-')}",
        f"INV: {last_result.get('invoice_no', '-')}  |  REGION: {last_result.get('region', '-')}  |  MSG: {last_result.get('message', '-')}",
    ]

    y = y_start + 25
    for line in lines:
        color = (255, 255, 255)
        if "DEFECT" in line or "불량" in line:
            color = (0, 0, 255)
        elif "QR" in line:
            color = (0, 255, 0)
        elif "OCR" in line:
            color = (255, 180, 0)

        cv2.putText(frame, line, (10, y), cv2.FONT_HERSHEY_SIMPLEX, 0.45, color, 1, cv2.LINE_AA)
        y += 22


# =========================
# 메인
# =========================

def main():
    # MQTT 연결 (별도 스레드)
    mqtt_thread = threading.Thread(target=agent.connect, daemon=True)
    mqtt_thread.start()
    print(f"[MQTT] 브로커 연결 시도: {BROKER_HOST}")

    # Flask 스트리밍 서버 (별도 스레드)
    flask_thread = threading.Thread(
        target=lambda: app.run(host=STREAM_HOST, port=STREAM_PORT, threaded=True),
        daemon=True
    )
    flask_thread.start()
    print(f"[STREAM] MJPEG 서버 시작: http://{STREAM_HOST}:{STREAM_PORT}/stream")

    # 카메라 루프 (메인 스레드)
    time.sleep(1)  # MQTT/Flask 초기화 대기
    camera_loop()


if __name__ == "__main__":
    main()
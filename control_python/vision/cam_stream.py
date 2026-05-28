import os
import sys
import cv2
import time
import json
import threading
import traceback
from datetime import datetime
from flask import Flask, Response
import collections
import re

# =========================
# 경로 설정
# =========================
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.abspath(os.path.join(BASE_DIR, ".."))

if PROJECT_DIR not in sys.path:
    sys.path.insert(0, PROJECT_DIR)

from common.agent_base import AgentBase
from common.topics import VISION_COMMAND, VISION_SCAN_RESULT, VISION_FAIL
from vision.qr_reader import detect_qr
from vision.ocr_reader import detect_ocr


# =========================
# 설정
# =========================
BROKER_HOST = "192.168.0.21"
DEVICE_ID = "vision"

STREAM_HOST = "0.0.0.0"
STREAM_PORT = 8081
STREAM_URL = "http://192.168.0.6:8081/stream"

CAMERA_INDEX = 1
SCAN_TIMEOUT = 15

# 박스 감지 기준
BOX_GREEN_THRESHOLD = 0.60

# OCR 조건
OCR_REQUIRED_MIN_FIELDS = 2

# 녹화 버퍼
PRE_BUFFER_SIZE = 120
pre_buffer = collections.deque(maxlen=PRE_BUFFER_SIZE)

app = Flask(__name__)

cap = None
latest_frame = None
stream_frame = None
frame_lock = threading.Lock()

scan_requested = False
scan_running = False
current_package_id = None
show_ocr_area = False
ocr_box = None


# =========================
# 시간 / 저장
# =========================
def now_text():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def save_image(directory, prefix, frame):
    if frame is None:
        return ""

    os.makedirs(directory, exist_ok=True)
    filename = f"{prefix}_{datetime.now():%Y%m%d_%H%M%S_%f}.jpg"
    path = os.path.join(directory, filename)
    cv2.imwrite(path, frame)
    return path


def safe_publish(topic, data):
    try:
        agent.publish_event(topic, data)
        print(f"[MQTT] publish topic={topic}")
    except Exception as e:
        print("[MQTT] publish 실패:", e)


# =========================
# QR 파싱
# =========================
def parse_qr_text(qr_text):
    qr_text = qr_text.strip()

    if qr_text.startswith("{"):
        data = json.loads(qr_text)

        region = data.get("region", "UNKNOWN")
        invoice_no = data.get("invoice_no", data.get("trackingNumber", "UNKNOWN"))
        package_type = data.get("package_type", "BOX")
        name = data.get("name", "-")

        return {
            "invoice_no": invoice_no,
            "name": name,
            "region": region,
            "package_type": package_type,
            "raw_text": qr_text,
        }

    parts = qr_text.split("|")

    region = parts[0].strip() if len(parts) > 0 and parts[0].strip() else "UNKNOWN"
    invoice_no = parts[1].strip() if len(parts) > 1 and parts[1].strip() else "UNKNOWN"
    package_type = parts[2].strip() if len(parts) > 2 and parts[2].strip() else "BOX"
    name = parts[3].strip() if len(parts) > 3 and parts[3].strip() else "-"

    return {
        "invoice_no": invoice_no,
        "name": name,
        "region": region,
        "package_type": package_type,
        "raw_text": qr_text,
    }


# =========================
# 박스 감지
# =========================
def detect_box_by_green(frame):
    h, w = frame.shape[:2]

    cx1, cx2 = int(w * 0.20), int(w * 0.80)
    cy1, cy2 = int(h * 0.20), int(h * 0.80)

    center = frame[cy1:cy2, cx1:cx2]

    hsv = cv2.cvtColor(center, cv2.COLOR_BGR2HSV)
    green_mask = cv2.inRange(hsv, (40, 60, 30), (80, 255, 180))

    green_ratio = cv2.countNonZero(green_mask) / (center.shape[0] * center.shape[1])
    detected = green_ratio < BOX_GREEN_THRESHOLD

    return detected, green_ratio


# =========================
# 글자 영역 추출
# =========================
def find_text_area(frame):
    h, w = frame.shape[:2]

    mx1, my1 = int(w * 0.15), int(h * 0.15)
    mx2, my2 = int(w * 0.85), int(h * 0.85)

    roi = frame[my1:my2, mx1:mx2].copy()
    rh, rw = roi.shape[:2]

    gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
    blur = cv2.GaussianBlur(gray, (3, 3), 0)

    binary = cv2.adaptiveThreshold(
        blur,
        255,
        cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        cv2.THRESH_BINARY_INV,
        31,
        10
    )

    kernel_open = cv2.getStructuringElement(cv2.MORPH_RECT, (2, 2))
    binary = cv2.morphologyEx(binary, cv2.MORPH_OPEN, kernel_open)

    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (20, 3))
    dilated = cv2.dilate(binary, kernel, iterations=1)

    contours, _ = cv2.findContours(
        dilated,
        cv2.RETR_EXTERNAL,
        cv2.CHAIN_APPROX_SIMPLE
    )

    candidates = []

    for cnt in contours:
        x, y, bw, bh = cv2.boundingRect(cnt)
        area = bw * bh

        if area < 300:
            continue
        if bw < 30 or bh < 8:
            continue
        if bh > rh * 0.3:
            continue
        if bw > rw * 0.8:
            continue
        if bw < bh * 1.5:
            continue

        candidates.append((x, y, x + bw, y + bh))

    if not candidates:
        return None

    x1 = max(min(c[0] for c in candidates) - 10 + mx1, 0)
    y1 = max(min(c[1] for c in candidates) - 10 + my1, 0)
    x2 = min(max(c[2] for c in candidates) + 10 + mx1, w)
    y2 = min(max(c[3] for c in candidates) + 10 + my1, h)

    return x1, y1, x2, y2


def crop_text_label_area(frame):
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)

    _, th = cv2.threshold(gray, 160, 255, cv2.THRESH_BINARY)

    contours, _ = cv2.findContours(
        th,
        cv2.RETR_EXTERNAL,
        cv2.CHAIN_APPROX_SIMPLE
    )

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

    x1, y1, x2, y2, _ = max(candidates, key=lambda c: c[4])

    margin = 8
    x1 = max(x1 - margin, 0)
    y1 = max(y1 - margin, 0)
    x2 = min(x2 + margin, w)
    y2 = min(y2 + margin, h)

    return frame[y1:y2, x1:x2].copy()


# =========================
# OCR 파싱
# =========================
REGION_MAP = [
    ("서울", "서울"), ("경기", "경기"), ("인천", "인천"),
    ("부산", "부산"), ("대구", "대구"), ("광주", "광주"),
    ("대전", "대전"), ("울산", "울산"), ("세종", "세종"),
    ("강원", "강원"), ("충북", "충북"), ("충남", "충남"),
    ("전북", "전북"), ("전남", "전남"), ("경북", "경북"),
    ("경남", "경남"), ("제주", "제주"),
]


# def match_region(text):
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


def parse_ocr_fields(text):
    text = re.sub(r"[승숭중]\s*장\s*번\s*[호오흐]", "송장번호", text)
    text = re.sub(r"송\s*장\s*번\s*[호오흐]", "송장번호", text)
    text = re.sub(r"운\s*송\s*장\s*번\s*[호오흐]", "송장번호", text)
    text = re.sub(r"이\s*[름듬금릅]", "이름", text)
    text = re.sub(r"성\s*명", "이름", text)
    text = re.sub(r"지\s*[역여억익엽]", "지역", text)
    text = re.sub(r"권\s*역", "지역", text)

    text = text.replace("：", ":").replace("；", ":").replace("·", ":")
    text = re.sub(r"(송장번호|이름|지역)\s*[\.。,，ㆍ\-]\s*", r"\1:", text)

    text = re.sub(r"(송장번호)(\d)", r"\1:\2", text)
    text = re.sub(r"(이름)([가-힣])", r"\1:\2", text)
    text = re.sub(r"(지역)([가-힣])", r"\1:\2", text)

    text = re.sub(r"(송장번호|이름|지역)\s+(?=[0-9가-힣])", r"\1:", text)
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


    invoice_clean = invoice_raw
    invoice_clean = invoice_clean.replace("O", "0").replace("o", "0")
    invoice_clean = invoice_clean.replace("I", "1").replace("l", "1").replace("|", "1")
    invoice_clean = invoice_clean.replace("S", "5").replace("s", "5")
    invoice_clean = invoice_clean.replace("B", "8")
    invoice_clean = re.sub(r"[^0-9]", "", invoice_clean)

    if not invoice_clean:
        digit_match = re.search(r"\d{2,}", text)
        if digit_match:
            invoice_clean = digit_match.group(0)

    invoice_no = invoice_clean if invoice_clean else "-"

    name_match = re.search(r"[가-힣]{2,5}", name_raw)
    name = name_match.group(0) if name_match else "-"

    if name == "-":
        cleaned = re.sub(
            r"(송장번호|이름|지역|경기도?|서울|부산|인천|대구|광주|대전|울산|세종|강원|충[남북]|전[남북]|경[남북]|제주)",
            "",
            text
        )
        name_match2 = re.search(r"[가-힣]{2,5}", cleaned)
        if name_match2:
            name = name_match2.group(0)

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


def make_ocr_result(fields, confidence, text, angle):
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


def try_ocr(frame):
    try:
        up = crop_text_label_area(frame)

        rotations = [
            (0, None),
            (180, cv2.ROTATE_180),
        ]

        LABEL_KEYWORDS = ["송장", "번호", "이름", "성명", "지역", "권역"]

        best_with_label = None
        best_without_label = None

        for angle, rot_code in rotations:
            rotated = up if rot_code is None else cv2.rotate(up, rot_code)

            result = detect_ocr(rotated)

            if result is None or not isinstance(result, dict):
                continue

            text = result.get("text", "").strip()
            confidence = float(result.get("confidence", 0.0))

            has_label = any(kw in text for kw in LABEL_KEYWORDS)
            fields = parse_ocr_fields(text)
            fc = fields.get("field_count", 0)

            if has_label:
                if fields.get("valid", False):
                    print(
                        f"[OCR] 성공 angle={angle} "
                        f"invoice={fields['invoice_no']} "
                        f"name={fields['name']} "
                        f"region={fields['region']}"
                    )
                    return make_ocr_result(fields, confidence, text, angle)

                if best_with_label is None or fc > best_with_label[0]:
                    best_with_label = (fc, confidence, fields, text, angle)
            else:
                if best_without_label is None or fc > best_without_label[0]:
                    best_without_label = (fc, confidence, fields, text, angle)

        if best_with_label and best_with_label[0] >= 1:
            _, confidence, fields, text, angle = best_with_label
            return make_ocr_result(fields, confidence, text, angle)

        if best_without_label and best_without_label[0] >= 1:
            _, confidence, fields, text, angle = best_without_label
            return make_ocr_result(fields, confidence, text, angle)

        print("[OCR] 실패")
        return None

    except Exception as e:
        print("[OCR] 예외:", e)
        traceback.print_exc()
        return None


# =========================
# Vision Agent
# =========================
class VisionStreamAgent(AgentBase):
    def __init__(self, broker_host):
        super().__init__(
            broker_host,
            DEVICE_ID,
            "VISION",
            VISION_COMMAND,
            "parcel/vision/result"
        )

    def _on_connect(self, client, userdata, flags, rc):
        super()._on_connect(client, userdata, flags, rc)

        self.publish_event(f"parcel/{self.device_id}/status", {
            "status": "ONLINE",
            "device_id": self.device_id,
            "device_type": self.device_type,
            "stream_url": STREAM_URL
        })

    def handle_command(self, data):
        global scan_requested, current_package_id

        cmd = str(data.get("command", "")).strip().upper()

        if cmd == "START_SCAN":
            current_package_id = data.get("package_id", "UNKNOWN")
            scan_requested = True
            print("[VISION] START_SCAN 요청:", current_package_id)


agent = VisionStreamAgent(BROKER_HOST)


# =========================
# 스캔 작업
# =========================
def scan_worker(package_id):
    global scan_running, show_ocr_area, ocr_box

    scan_running = True
    show_ocr_area = False
    ocr_box = None

    print("[VISION] 박스 감지 대기:", package_id)

    while True:
        with frame_lock:
            if latest_frame is None:
                time.sleep(0.05)
                continue
            frame = latest_frame.copy()

        detected, green_ratio = detect_box_by_green(frame)

        if detected:
            print(f"[VISION] 박스 감지 완료 green={green_ratio:.2f}")
            break

        time.sleep(0.05)

    show_ocr_area = True

    start_time = time.time()
    qr_sent = False
    ocr_sent = False
    last_ocr_time = 0

    while time.time() - start_time < SCAN_TIMEOUT:
        with frame_lock:
            if latest_frame is None:
                time.sleep(0.03)
                continue
            frame = latest_frame.copy()

        # =========================
        # QR 먼저 시도
        # =========================
        if not qr_sent:
            qr_text, bbox = detect_qr(frame)

            if qr_text:
                print("[QR] 인식 성공:", qr_text)

                try:
                    qr = parse_qr_text(qr_text)
                    sort_code = f"{qr['region']}_{qr['package_type']}"

                    qr_data = {
                        "package_id": package_id,
                        "scan_type": "QR",
                        "invoice_no": qr["invoice_no"],
                        "name": qr.get("name", "-"),
                        "region": qr["region"],
                        "package_type": qr["package_type"],
                        "sort_code": sort_code,
                        "raw_text": qr.get("raw_text", ""),
                        "timestamp": round(time.time(), 3),
                        "time_text": now_text(),
                    }

                    print(f"[DEBUG] 토픽: {VISION_SCAN_RESULT}")
                    print(f"[DEBUG] 데이터: {qr_data}")

                    safe_publish(VISION_SCAN_RESULT, qr_data)
                    qr_sent = True
                    show_ocr_area = False
                    scan_running = False
                    return

                except Exception as e:
                    print("[QR] 파싱 실패:", e)

                    fail_data = {
                        "type": "QR_FAIL",
                        "package_id": package_id,
                        "reason": "QR_PARSE_ERROR",
                        "timestamp": round(time.time(), 3),
                        "time_text": now_text(),
                    }

                    safe_publish(VISION_FAIL, fail_data)
                    qr_sent = True

            else:
                print("[QR] 탐색 중...")

        # =========================
        # QR 실패 상태에서 OCR 시도
        # =========================
        if not ocr_sent and time.time() - last_ocr_time >= 1.0:
            last_ocr_time = time.time()

            if ocr_box is None:
                print("[OCR] 글자 영역 못 찾음 - 대기 중...")
                continue

            x1, y1, x2, y2 = ocr_box
            ocr_area = frame[y1:y2, x1:x2].copy()

            print("[OCR] 실행 영역:", ocr_box)
            ocr_result = try_ocr(ocr_area)

            if ocr_result:
                sort_code = f"{ocr_result['region_code']}_BOX"

                ocr_data = {
                    "package_id": package_id,
                    "scan_type": "OCR",
                    "invoice_no": ocr_result["invoice_no"],
                    "name": ocr_result.get("name", "-"),
                    "region": ocr_result["region"],
                    "package_type": "-",
                    "sort_code": f"{ocr_result['region_code']}_BOX",
                    "confidence": ocr_result["confidence"],
                    "raw_text": ocr_result["raw_text"],
                    "ocr_angle": ocr_result.get("ocr_angle", 0),
                    "timestamp": round(time.time(), 3),
                    "time_text": now_text(),
                }

                print(f"[DEBUG] 토픽: {VISION_SCAN_RESULT}")
                print(f"[DEBUG] 데이터: {ocr_data}")

                safe_publish(VISION_SCAN_RESULT, ocr_data)

                ocr_sent = True
                show_ocr_area = False
                scan_running = False
                return

        time.sleep(0.03)

    print("[VISION] 스캔 종료 - QR/OCR 실패")

    fail_data = {
        "type": "SCAN_FAIL",
        "package_id": package_id,
        "reason": "QR_OCR_BOTH_FAIL",
        "timestamp": round(time.time(), 3),
        "time_text": now_text(),
    }

    safe_publish(VISION_FAIL, fail_data)

    show_ocr_area = False
    scan_running = False


# =========================
# 카메라 루프
# =========================
def camera_loop():
    global cap, latest_frame, stream_frame
    global scan_requested, scan_running, show_ocr_area, ocr_box

    cap = cv2.VideoCapture(CAMERA_INDEX, cv2.CAP_DSHOW)

    if not cap.isOpened():
        print("[CAMERA] 카메라 열기 실패")
        return

    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)

    print("[CAMERA] 카메라 시작")

    while True:
        ret, frame = cap.read()

        if not ret or frame is None:
            continue

        display_frame = frame.copy()

        # 박스 감지 표시용
        detected, green_ratio = detect_box_by_green(frame)
        h, w = frame.shape[:2]
        cx1, cx2 = int(w * 0.20), int(w * 0.80)
        cy1, cy2 = int(h * 0.20), int(h * 0.80)

        color = (0, 255, 0) if detected else (0, 255, 255)
        label = f"BOX green={green_ratio:.2f}"

        cv2.rectangle(display_frame, (cx1, cy1), (cx2, cy2), color, 2)
        cv2.putText(
            display_frame,
            label,
            (cx1, max(cy1 - 10, 0)),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.6,
            color,
            2
        )

        # OCR 영역 추적
        if scan_running and show_ocr_area:
            text_box = find_text_area(frame)
            if text_box is not None:
                ocr_box = text_box

        if show_ocr_area and ocr_box is not None:
            x1, y1, x2, y2 = ocr_box

            cv2.rectangle(display_frame, (x1, y1), (x2, y2), (255, 0, 0), 2)
            cv2.putText(
                display_frame,
                "OCR AREA",
                (x1, max(y1 - 10, 0)),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.6,
                (255, 0, 0),
                2
            )

        # QR 박스 표시
        qr_text, bbox = detect_qr(frame)

        if bbox is not None:
            try:
                points = bbox[0]

                for i in range(len(points)):
                    pt1 = tuple(points[i])
                    pt2 = tuple(points[(i + 1) % len(points)])
                    cv2.line(display_frame, pt1, pt2, (0, 255, 0), 2)

                cv2.putText(
                    display_frame,
                    "QR Detected",
                    tuple(points[0]),
                    cv2.FONT_HERSHEY_SIMPLEX,
                    0.7,
                    (0, 255, 0),
                    2
                )
            except Exception:
                pass

        pre_buffer.append(frame.copy())

        with frame_lock:
            latest_frame = frame.copy()
            stream_frame = display_frame.copy()

        if scan_requested and not scan_running:
            scan_requested = False

            threading.Thread(
                target=scan_worker,
                args=(current_package_id,),
                daemon=True
            ).start()

        time.sleep(0.01)


# =========================
# Flask 스트리밍
# =========================
def generate():
    global stream_frame

    while True:
        with frame_lock:
            if stream_frame is None:
                time.sleep(0.03)
                continue

            frame = stream_frame.copy()

        encode_param = [int(cv2.IMWRITE_JPEG_QUALITY), 70]
        ret, jpeg = cv2.imencode(".jpg", frame, encode_param)

        if not ret:
            continue

        yield (
            b"--frame\r\n"
            b"Content-Type: image/jpeg\r\n\r\n" +
            jpeg.tobytes() +
            b"\r\n"
        )

        time.sleep(0.03)


@app.route("/stream")
def stream():
    return Response(
        generate(),
        mimetype="multipart/x-mixed-replace; boundary=frame"
    )


# =========================
# main
# =========================
if __name__ == "__main__":
    threading.Thread(target=camera_loop, daemon=True).start()

    threading.Thread(
        target=lambda: app.run(host=STREAM_HOST, port=STREAM_PORT, threaded=True),
        daemon=True
    ).start()

    print("[FLASK] stream server start")
    print("[MQTT] agent connect start")

    try:
        agent.connect()

    except KeyboardInterrupt:
        print("\n[SYSTEM] 프로그램 종료")

        if cap is not None:
            cap.release()

        cv2.destroyAllWindows()
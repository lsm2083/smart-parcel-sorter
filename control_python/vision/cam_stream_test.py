import os
import cv2
import time
import json
import threading
import collections

# 30fps * 4초 = 120프레임
PRE_BUFFER_SIZE = 120
pre_buffer = collections.deque(maxlen=PRE_BUFFER_SIZE)

CAMERA_INDEX = 1
SCAN_TIMEOUT = 15

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
# 더미 MQTT (테스트용)
# =========================
class DummyAgent:
    def publish_event(self, topic, data):
        print(f"\n{'='*50}")
        print(f"[MQTT 전송 시뮬레이션]")
        print(f"  토픽: {topic}")
        print(f"  데이터: {json.dumps(data, ensure_ascii=False, indent=2)}")
        print(f"{'='*50}\n")

agent = DummyAgent()

VISION_SCAN_RESULT = "parcel/vision/scan_result"
VISION_FAIL = "parcel/vision/fail"

# =========================
# OCR (easyocr)
# =========================
import easyocr
reader = easyocr.Reader(["ko", "en"], gpu=False)

def detect_ocr(frame):
    h, w = frame.shape[:2]
    max_width = 500
    if w > max_width:
        scale = max_width / w
        frame = cv2.resize(frame, None, fx=scale, fy=scale)

    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    blur = cv2.GaussianBlur(gray, (5, 5), 0)
    thresh = cv2.adaptiveThreshold(blur, 255,
        cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        cv2.THRESH_BINARY, 21, 5)

    results = reader.readtext(thresh, detail=1, paragraph=False, batch_size=1)

    texts = []
    confidences = []
    for bbox, text, confidence in results:
        if confidence < 0.25:
            continue
        texts.append(text)
        confidences.append(confidence)

    full_text = " ".join(texts)
    avg_confidence = sum(confidences) / len(confidences) if confidences else 0.0

    return {"text": full_text, "confidence": avg_confidence, "raw": results}

# =========================
# QR
# =========================
def detect_qr(frame):
    try:
        from pyzbar.pyzbar import decode
        decoded = decode(frame)
        for obj in decoded:
            try:
                data = obj.data.decode("utf-8")
            except:
                data = obj.data.decode("cp949")
            points = obj.polygon
            bbox = [[p.x, p.y] for p in points]
            return data, [bbox]
    except:
        pass
    return None, None

# =========================
# 글자 영역 탐지
# =========================
def find_text_area(frame):
    h, w = frame.shape[:2]
    mx1, my1 = int(w * 0.15), int(h * 0.15)
    mx2, my2 = int(w * 0.85), int(h * 0.85)
    roi = frame[my1:my2, mx1:mx2].copy()
    rh, rw = roi.shape[:2]
    gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
    blur = cv2.GaussianBlur(gray, (3, 3), 0)
    binary = cv2.adaptiveThreshold(blur, 255,
        cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        cv2.THRESH_BINARY_INV, 31, 10)
    kernel_open = cv2.getStructuringElement(cv2.MORPH_RECT, (2, 2))
    binary = cv2.morphologyEx(binary, cv2.MORPH_OPEN, kernel_open)
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (20, 3))
    dilated = cv2.dilate(binary, kernel, iterations=1)
    contours, _ = cv2.findContours(dilated, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    candidates = []
    for cnt in contours:
        x, y, bw, bh = cv2.boundingRect(cnt)
        area = bw * bh
        if area < 300: continue
        if bw < 30 or bh < 8: continue
        if bh > rh * 0.3: continue
        if bw > rw * 0.8: continue
        if bw < bh * 1.5: continue
        candidates.append((x, y, x + bw, y + bh))
    if not candidates:
        return None
    x1 = max(min(c[0] for c in candidates) - 10 + mx1, 0)
    y1 = max(min(c[1] for c in candidates) - 10 + my1, 0)
    x2 = min(max(c[2] for c in candidates) + 10 + mx1, w)
    y2 = min(max(c[3] for c in candidates) + 10 + my1, h)
    return x1, y1, x2, y2

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
        return region, invoice_no, package_type
    parts = qr_text.split("|")
    region = parts[0].strip() if len(parts) > 0 and parts[0].strip() else "UNKNOWN"
    invoice_no = parts[1].strip() if len(parts) > 1 and parts[1].strip() else "UNKNOWN"
    package_type = parts[2].strip() if len(parts) > 2 and parts[2].strip() else "BOX"
    return region, invoice_no, package_type

# =========================
# 스캔 작업
# =========================
def scan_worker(package_id):
    global scan_running, show_ocr_area, ocr_box

    scan_running = True
    show_ocr_area = False
    ocr_box = None

    print(f"\n[VISION] 스캔 대기 시작: package_id={package_id}")
    print("[VISION] 박스 감지 대기 중...")

    # 박스 감지될 때까지 대기
    while True:
        with frame_lock:
            if latest_frame is None:
                time.sleep(0.05)
                continue
            frame = latest_frame.copy()

        h, w = frame.shape[:2]
        cx1, cx2 = int(w * 0.2), int(w * 0.8)
        cy1, cy2 = int(h * 0.2), int(h * 0.8)
        center = frame[cy1:cy2, cx1:cx2]

        hsv = cv2.cvtColor(center, cv2.COLOR_BGR2HSV)
        green_mask = cv2.inRange(hsv, (40, 60, 30), (80, 255, 180))
        green_ratio = cv2.countNonZero(green_mask) / (center.shape[0] * center.shape[1])

        if green_ratio < 0.6:
            print("[VISION] 박스 감지! 스캔 시작")
            break

        time.sleep(0.05)

    show_ocr_area = True
    time.sleep(0.3)

    start_time = time.time()
    qr_sent = False
    ocr_sent = False
    last_ocr_time = 0

    while time.time() - start_time < SCAN_TIMEOUT:
        with frame_lock:
            if latest_frame is None:
                continue
            frame = latest_frame.copy()

        h, w, _ = frame.shape

        # =========================
        # QR 인식
        # =========================
        if not qr_sent:
            qr_text, bbox = detect_qr(frame)
            if qr_text:
                print("[QR] 인식 성공:", qr_text)
                try:
                    region, invoice_no, package_type = parse_qr_text(qr_text)
                    sort_code = f"{region}_{package_type}"
                    qr_data = {
                        "package_id": package_id,
                        "scan_type": "QR",
                        "invoice_no": invoice_no,
                        "region": region,
                        "package_type": package_type,
                        "sort_code": sort_code
                    }
                    agent.publish_event(VISION_SCAN_RESULT, qr_data)
                    qr_sent = True
                except Exception as e:
                    print("[QR] 파싱 실패:", e)
                    agent.publish_event(VISION_FAIL, {
                        "type": "QR_FAIL",
                        "package_id": package_id,
                        "reason": "QR_PARSE_ERROR"
                    })
                    qr_sent = True
            else:
                print("[QR] 탐색 중...")

        # =========================
        # OCR 인식
        # =========================
        if not ocr_sent and time.time() - last_ocr_time >= 1.0:
            last_ocr_time = time.time()

            if ocr_box is None:
                print("[OCR] 글자 영역 못 찾음 - 대기 중...")
                continue

            x1, y1, x2, y2 = ocr_box
            print("[OCR] 글자 영역:", ocr_box)
            ocr_area = frame[y1:y2, x1:x2].copy()

            print("[OCR] 실행 중...")
            ocr_result = detect_ocr(ocr_area)
            ocr_text = ocr_result["text"]
            ocr_confidence = ocr_result["confidence"]

            raw = ocr_result.get("raw", [])
            if raw:
                xs, ys = [], []
                for bbox, text, conf in raw:
                    for px, py in bbox:
                        xs.append(int(px + x1))
                        ys.append(int(py + y1))
                if xs and ys:
                    ocr_box = (
                        max(min(xs)-10, 0),
                        max(min(ys)-10, 0),
                        min(max(xs)+10, w),
                        min(max(ys)+10, h)
                    )
                    print("[OCR] 자동 AREA:", ocr_box)

            ocr_text = ocr_text.replace("\n", " ").strip()
            region = "-"
            name = "-"

            for r in ["부산", "서울", "대구", "인천"]:
                if r in ocr_text:
                    region = r
                    break

            parts = ocr_text.split()
            if len(parts) >= 2:
                name = parts[-1]

            print(f"[OCR] 텍스트: {ocr_text}")
            print(f"[OCR] 지역: {region} / 이름: {name}")
            print(f"[OCR] 신뢰도: {ocr_confidence:.3f}")

            if ocr_text and ocr_confidence >= 0.25:
                save_dir = r"C:\모블 최종 프로젝트 자료\ocr_인식"
                os.makedirs(save_dir, exist_ok=True)
                save_path = os.path.join(save_dir, f"ocr_clip_{package_id}_{int(time.time())}.avi")
                fourcc = cv2.VideoWriter_fourcc(*'XVID')
                writer = cv2.VideoWriter(save_path, fourcc, 30.0, (640, 480))
                for f in list(pre_buffer):
                    writer.write(f)
                post_count = 0
                while post_count < 30:
                    with frame_lock:
                        if latest_frame is not None:
                            writer.write(latest_frame.copy())
                            post_count += 1
                    time.sleep(1/30)
                writer.release()
                print(f"[OCR] 클립 저장: {save_path}")

                agent.publish_event(VISION_SCAN_RESULT, {
                    "package_id": package_id,
                    "scan_type": "OCR",
                    "invoice_no": ocr_text,
                    "region": region,
                    "package_type": "-",
                    "sort_code": "-"
                })
                ocr_sent = True

        if qr_sent and ocr_sent:
            break

        time.sleep(0.03)

    if not qr_sent:
        agent.publish_event(VISION_FAIL, {
            "type": "QR_FAIL",
            "package_id": package_id,
            "reason": "NO_QR_FOUND"
        })

    show_ocr_area = False
    scan_running = False
    print("[VISION] 스캔 완료\n[안내] 다시 스캔하려면 스페이스바를 누르세요.")

# =========================
# 카메라 루프
# =========================
def camera_loop():
    global cap, latest_frame, stream_frame, scan_requested, scan_running, ocr_box

    cap = cv2.VideoCapture(CAMERA_INDEX, cv2.CAP_DSHOW)
    if not cap.isOpened():
        print("[CAMERA] 카메라 열기 실패 - 인덱스 0으로 재시도")
        cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)
        if not cap.isOpened():
            print("[CAMERA] 카메라 열기 최종 실패")
            return

    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    print("[CAMERA] 카메라 시작")

    while True:
        ret, frame = cap.read()
        if not ret or frame is None:
            continue

        display_frame = frame.copy()

        if show_ocr_area and ocr_box is not None:
            x1, y1, x2, y2 = ocr_box
            cv2.rectangle(display_frame, (x1, y1), (x2, y2), (255, 0, 0), 2)
            cv2.putText(display_frame, "OCR AREA", (x1, max(y1-10, 0)),
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 0, 0), 2)

        qr_text, bbox = detect_qr(display_frame)
        if bbox is not None:
            points = bbox[0]
            for i in range(len(points)):
                pt1 = tuple(points[i])
                pt2 = tuple(points[(i+1) % len(points)])
                cv2.line(display_frame, pt1, pt2, (0, 255, 0), 2)
            cv2.putText(display_frame, "QR Detected", tuple(points[0]),
                cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)

        if scan_running and show_ocr_area:
            text_box = find_text_area(frame)
            if text_box is not None:
                ocr_box = text_box

        # 박스 감지 대기 중일 때 감지 영역 표시
        if scan_running and not show_ocr_area:
            h, w, _ = display_frame.shape
            cx1, cx2 = int(w * 0.2), int(w * 0.8)
            cy1, cy2 = int(h * 0.2), int(h * 0.8)
            cv2.rectangle(display_frame, (cx1, cy1), (cx2, cy2), (0, 255, 255), 1)
            cv2.putText(display_frame, "WAITING BOX...", (cx1, cy1-10),
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)

        # 상태 표시
        if scan_running:
            status = "SCANNING..." if show_ocr_area else "WAITING BOX..."
            color = (0, 0, 255)
        else:
            status = "READY - Press SPACE to scan"
            color = (0, 255, 0)
        cv2.putText(display_frame, status, (10, 30),
            cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)

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
# main
# =========================
if __name__ == "__main__":
    print("=" * 50)
    print("  OCR 테스트 모드 (MQTT 없음)")
    print("  스페이스바: 스캔 시작")
    print("  ESC: 종료")
    print("=" * 50)

    threading.Thread(target=camera_loop, daemon=True).start()
    time.sleep(1.0)

    package_counter = 1000

    try:
        while True:
            with frame_lock:
                if stream_frame is None:
                    time.sleep(0.03)
                    continue
                frame = stream_frame.copy()

            cv2.imshow("OCR Test", frame)
            key = cv2.waitKey(1)

            if key == 27:  # ESC
                break
            elif key == 32:  # 스페이스바
                if not scan_running:
                    current_package_id = package_counter
                    package_counter += 1
                    scan_requested = True
                    print(f"\n[TEST] 스캔 요청 - package_id: {current_package_id}")
                else:
                    print("[TEST] 이미 스캔 중입니다.")

    except KeyboardInterrupt:
        print("\n[SYSTEM] 종료 중...")

    finally:
        if cap is not None:
            cap.release()
        cv2.destroyAllWindows()

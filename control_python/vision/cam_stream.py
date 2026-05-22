import os
import sys
import cv2
import time
import threading
from flask import Flask, Response

sys.path.append(os.path.join(os.path.dirname(__file__), '..', 'common'))

from agent_base import AgentBase
from topics import VISION_COMMAND, VISION_SCAN_RESULT, VISION_FAIL
from qr_reader import detect_qr
from ocr_reader import detect_ocr


BROKER_HOST = "192.168.0.21"
DEVICE_ID = "vision"
STREAM_HOST = "0.0.0.0"
STREAM_PORT = 8081
CAMERA_INDEX = 1

app = Flask(__name__)

cap = None
latest_frame = None
frame_lock = threading.Lock()

scan_requested = False
scan_running = False
current_package_id = None


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
            "stream_url": "http://192.168.0.6:8081/stream"
        })

    def handle_command(self, data):
        global scan_requested, current_package_id

        cmd = data.get("command")

        if cmd == "START_SCAN":
            current_package_id = data.get("package_id", "UNKNOWN")
            scan_requested = True
            print("[VISION] START_SCAN 요청:", current_package_id)


agent = VisionStreamAgent(BROKER_HOST)


def scan_worker(package_id):
    global scan_running

    scan_running = True
    print("[VISION] 스캔 시작:", package_id)

    start_time = time.time()
    timeout = 7

    qr_sent = False
    ocr_sent = False
    last_ocr_time = 0

    while time.time() - start_time < timeout:
        with frame_lock:
            if latest_frame is None:
                continue
            frame = latest_frame.copy()

        h, w, _ = frame.shape

        x1 = int(w * 0.20)
        x2 = int(w * 0.80)
        y1 = int(h * 0.175)
        y2 = int(h * 0.825)

        # -------------------------
        # QR 독립 인식
        # -------------------------
        if not qr_sent:
            qr_text, bbox = detect_qr(frame)

            if qr_text:
                print("[QR] 인식 성공:", qr_text)

                parts = qr_text.split("|")
                region = parts[0]
                invoice_no = parts[1] if len(parts) > 1 else ""

                qr_data = {
                    "package_id": package_id,
                    "scan_type": "QR",
                    "invoice_no": invoice_no,
                    "region": region,
                    "package_type": "BOX",
                    "sort_code": f"{region}_BOX"
                }

                agent.publish_event(VISION_SCAN_RESULT, qr_data)
                print("[MQTT] QR 결과 전송 완료")

                qr_sent = True

        # -------------------------
        # OCR 독립 인식
        # 너무 자주 돌리면 느리니까 1초마다 시도
        # -------------------------
        if not ocr_sent and time.time() - last_ocr_time >= 1.0:
            last_ocr_time = time.time()

            ocr_area = frame[y1:y2, x1:x2].copy()

            print("[OCR] 실행")
            ocr_result = detect_ocr(ocr_area)

            ocr_text = ocr_result["text"]
            ocr_confidence = ocr_result["confidence"]

            print("[OCR] 결과:", ocr_text)
            print("[OCR] 신뢰도:", ocr_confidence)

            if ocr_text and ocr_confidence >= 0.25:
                agent.publish_event(VISION_SCAN_RESULT, {
                    "package_id": package_id,
                    "scan_type": "OCR",
                    "ocr_text": ocr_text,
                    "ocr_confidence": ocr_confidence
                })

                print("[MQTT] OCR 결과 전송 완료")
                ocr_sent = True

        # QR/OCR 둘 다 끝나면 종료
        if qr_sent and ocr_sent:
            scan_running = False
            return

        time.sleep(0.03)

    print("[VISION] 스캔 종료")

    if not qr_sent:
        agent.publish_event(VISION_FAIL, {
            "type": "SCAN_FAIL",
            "package_id": package_id,
            "reason": "QR_FAILED"
        })

    scan_running = False


def camera_loop():
    global cap, latest_frame, scan_requested, scan_running

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

        if scan_running:
            h, w, _ = display_frame.shape

            x1 = int(w * 0.20)
            x2 = int(w * 0.80)
            y1 = int(h * 0.175)
            y2 = int(h * 0.825)

            cv2.rectangle(display_frame, (x1, y1), (x2, y2), (255, 0, 0), 2)
            cv2.putText(display_frame, "OCR AREA", (x1, y1 - 10),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 0, 0), 2)

            qr_text, bbox = detect_qr(display_frame)

            if bbox is not None:
                points = bbox[0]

                for i in range(len(points)):
                    pt1 = tuple(points[i])
                    pt2 = tuple(points[(i + 1) % len(points)])
                    cv2.line(display_frame, pt1, pt2, (0, 255, 0), 2)

                cv2.putText(display_frame, "QR Detected", tuple(points[0]),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)

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


def generate():
    global latest_frame

    while True:
        with frame_lock:
            if latest_frame is None:
                continue
            frame = latest_frame.copy()

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


if __name__ == "__main__":
    threading.Thread(target=camera_loop, daemon=True).start()

    threading.Thread(
        target=lambda: app.run(host=STREAM_HOST, port=STREAM_PORT, threaded=True),
        daemon=True
    ).start()

    print("[FLASK] stream server start")
    print("[MQTT] agent connect start")

    agent.connect()
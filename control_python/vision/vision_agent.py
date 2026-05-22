import sys
import json
import time
import cv2
import os
import base64
sys.path.append(os.path.join(os.path.dirname(__file__), '..', 'common'))


sys.path.append('../common')

from agent_base import AgentBase
from topics import VISION_COMMAND, VISION_SCAN_RESULT, VISION_FAIL

from qr_reader import detect_qr
from ocr_reader import detect_ocr


class VisionAgent(AgentBase):
    def __init__(self, broker_host):
        super().__init__(
            broker_host,
            'vision',
            'VISION',
            VISION_COMMAND,
            'parcel/vision/result'
        )

    def _on_connect(self, client, userdata, flags, rc):
        super()._on_connect(client, userdata, flags, rc)
        # stream_url 추가 발행
        self.publish_event(f"parcel/{self.device_id}/status", {
            "status": "ONLINE",
            "device_id": self.device_id,
            "device_type": self.device_type,
            "stream_url": "http://192.168.0.6:8081/stream"
        })


    def handle_command(self, data):
        cmd = data["command"]

        if cmd == "START_SCAN":
            package_id = data["package_id"]

            print(f"\n[VISION] START_SCAN package_id={package_id}")

            result = self.scan_package()

            if result:
                self.publish_event(VISION_SCAN_RESULT, {
                    "package_id": package_id,
                    "invoice_no": result.get("invoice_no"),
                    "region": result.get("region"),
                    "package_type": result.get("package_type"),
                    "sort_code": result.get("sort_code"),
                    "ocr_text": result.get("ocr_text"),
                    "ocr_confidence": result.get("ocr_confidence"),
                    "scan_method": result.get("scan_method")
                })
            else:
                self.publish_event(VISION_FAIL, {
                    "type": "SCAN_FAIL",
                    "package_id": package_id,
                    "reason": "QR_AND_OCR_FAILED"
                })

    def scan_package(self):
        stream_url = "http://192.168.0.6:8081/stream"
        cap = cv2.VideoCapture(stream_url)

        if not cap.isOpened():
            print("[VISION] 카메라 열기 실패")
            return None

        cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)

        start_time = time.time()
        timeout = 7

        qr_data = None
        qr_detect_time = None

        last_frame_send = 0

        while time.time() - start_time < timeout:
            ret, frame = cap.read()

            if not ret or frame is None:
                continue

            # -----------------------------
            # 프레임 MQTT 전송 (5fps)
            # -----------------------------
            if time.time() - last_frame_send >= 0.2:

                encode_param = [int(cv2.IMWRITE_JPEG_QUALITY), 50]

                _, buffer = cv2.imencode(
                    '.jpg',
                    display_frame,
                    encode_param
                )

                frame_base64 = base64.b64encode(buffer).decode('utf-8')

                self.publish_event(
                    "parcel/vision/frame",
                    {
                        "image": frame_base64
                    }
                )

                last_frame_send = time.time()

            h, w, _ = frame.shape

            # cam_stream에서 안정화한 OCR AREA
            x1 = int(w * 0.20)
            x2 = int(w * 0.80)
            y1 = int(h * 0.175)
            y2 = int(h * 0.825)

            qr_text, bbox = detect_qr(frame)

            display_frame = frame.copy()

            # QR 인식됐을 때만 QR 박스 표시
            if bbox is not None:
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

            # QR 인식 후 OCR 대기 중일 때만 OCR AREA 표시
            if qr_data and qr_detect_time:
                cv2.rectangle(
                    display_frame,
                    (x1, y1),
                    (x2, y2),
                    (255, 0, 0),
                    2
                )

                cv2.putText(
                    display_frame,
                    "OCR AREA",
                    (x1, y1 - 10),
                    cv2.FONT_HERSHEY_SIMPLEX,
                    0.6,
                    (255, 0, 0),
                    2
                )

            # QR 첫 인식
            if qr_text and qr_data is None:
                print("[VISION] QR 인식 성공:", qr_text)

                try:
                    qr_data = json.loads(qr_text)
                    qr_detect_time = time.time()

                except json.JSONDecodeError:
                    print("[VISION] QR JSON 파싱 실패")
                    qr_data = None

            # QR 인식 후 2초 뒤 현재 화면 OCR
            if qr_data and qr_detect_time:
                if time.time() - qr_detect_time >= 2.0:
                    ocr_area = frame[y1:y2, x1:x2].copy()

                    print("[VISION] OCR 실행")
                    ocr_result = detect_ocr(ocr_area)

                    ocr_text = ocr_result["text"]
                    ocr_confidence = ocr_result["confidence"]

                    print("[VISION] OCR 결과:", ocr_text)
                    print("[VISION] OCR 신뢰도:", ocr_confidence)

                    cap.release()

                    qr_data["ocr_text"] = ocr_text
                    qr_data["ocr_confidence"] = ocr_confidence
                    qr_data["scan_method"] = "QR+OCR"

                    return qr_data

        cap.release()

        print("[VISION] 스캔 실패")
        return None


if __name__ == "__main__":
    agent = VisionAgent("192.168.0.21")
    agent.connect()
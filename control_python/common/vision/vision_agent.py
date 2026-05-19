import sys
import json
import time
import cv2

sys.path.append('../common')

from agent_base import AgentBase
from topics import VISION_COMMAND, VISION_SCAN_RESULT, VISION_FAIL

from qr_reader import detect_qr
from ocr_reader import detect_ocr


class VisionAgent(AgentBase):
    def __init__(self, broker_host):
        super().__init__(
            broker_host,
            'vision_agent_01',
            'VISION',
            VISION_COMMAND,
            'parcel/vision/result'
        )

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
        cap = cv2.VideoCapture(1, cv2.CAP_DSHOW)

        if not cap.isOpened():
            print("[VISION] 카메라 열기 실패")
            return None

        cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)

        start_time = time.time()
        timeout = 7

        qr_data = None
        qr_detect_time = None

        while time.time() - start_time < timeout:
            ret, frame = cap.read()

            if not ret or frame is None:
                continue

            h, w, _ = frame.shape

            # cam_stream에서 안정화한 OCR AREA
            x1 = int(w * 0.20)
            x2 = int(w * 0.80)
            y1 = int(h * 0.175)
            y2 = int(h * 0.825)

            qr_text, bbox = detect_qr(frame)

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
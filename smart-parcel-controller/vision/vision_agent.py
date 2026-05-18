import sys
import json
import time
import cv2

sys.path.append('../common')

from agent_base import AgentBase
from topics import (
    VISION_COMMAND,
    VISION_SCAN_RESULT,
    VISION_FAIL
)

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

    # -------------------------------------------------
    # Flask / MQTT 명령 처리
    # -------------------------------------------------
    def handle_command(self, data):

        cmd = data['command']

        if cmd == 'START_SCAN':

            package_id = data['package_id']

            print(f'\n[VISION] 스캔 시작')
            print(f'package_id = {package_id}')

            # -----------------------------
            # 1차 QR 인식
            # -----------------------------
            qr_data = self.scan_qr()

            if qr_data:

                print('[VISION] QR 인식 성공')

                self.publish_event(
                    VISION_SCAN_RESULT,
                    {
                        "package_id": package_id,
                        "invoice_no": qr_data['invoice_no'],
                        "region": qr_data['region'],
                        "package_type": qr_data['package_type'],
                        "sort_code": qr_data['sort_code'],
                        "scan_method": "QR"
                    }
                )

                return

            # -----------------------------
            # QR 실패
            # -----------------------------
            print('[VISION] QR 인식 실패')

            self.publish_event(
                VISION_FAIL,
                {
                    "type": "QR_FAIL",
                    "package_id": package_id,
                    "reason": "NO_QR_FOUND"
                }
            )

            # -----------------------------
            # 2차 OCR 인식
            # -----------------------------
            ocr_data = self.scan_ocr()

            if ocr_data:

                print('[VISION] OCR 인식 성공')

                self.publish_event(
                    VISION_SCAN_RESULT,
                    {
                        "package_id": package_id,
                        "sort_code": ocr_data['sort_code'],
                        "scan_method": "OCR"
                    }
                )

                return

            # -----------------------------
            # OCR 실패
            # -----------------------------
            print('[VISION] OCR 인식 실패')

            self.publish_event(
                VISION_FAIL,
                {
                    "type": "OCR_FAIL",
                    "package_id": package_id,
                    "confidence": 0.0
                }
            )

    # -------------------------------------------------
    # QR 인식
    # -------------------------------------------------
    def scan_qr(self):

        cap = cv2.VideoCapture(1, cv2.CAP_DSHOW)

        if not cap.isOpened():

            print('[VISION] 카메라 열기 실패')
            return None

        start_time = time.time()
        timeout = 5

        while time.time() - start_time < timeout:

            ret, frame = cap.read()

            if not ret or frame is None:
                continue

            qr_text, bbox = detect_qr(frame)

            if qr_text:

                print('[VISION] QR 원본 데이터')
                print(qr_text)

                cap.release()

                try:

                    qr_data = json.loads(qr_text)

                    return qr_data

                except json.JSONDecodeError:

                    print('[VISION] QR JSON 파싱 실패')
                    return None

        cap.release()

        return None

    # -------------------------------------------------
    # OCR 인식
    # -------------------------------------------------
    def scan_ocr(self):

        cap = cv2.VideoCapture(1, cv2.CAP_DSHOW)

        if not cap.isOpened():

            print('[VISION] 카메라 열기 실패')
            return None

        ret, frame = cap.read()

        cap.release()

        if not ret or frame is None:
            return None

        result = detect_ocr(frame)

        text = result['text']
        confidence = result['confidence']

        print('[VISION] OCR 결과')
        print(text)

        print('[VISION] OCR 신뢰도')
        print(confidence)

        if confidence < 0.4:
            return None

        sort_code = self.classify_region(text)

        if sort_code is None:
            return None

        return {
            "sort_code": sort_code,
            "confidence": confidence
        }

    # -------------------------------------------------
    # 지역 분류
    # -------------------------------------------------
    def classify_region(self, text):

        if '서울' in text:
            return 'SEOUL_BOX'

        elif '경기' in text:
            return 'GYEONGGI_BOX'

        elif '부산' in text:
            return 'BUSAN_BOX'

        elif '대구' in text:
            return 'DAEGU_BOX'

        elif '광주' in text:
            return 'GWANGJU_BOX'

        return None


# -------------------------------------------------
# 실행
# -------------------------------------------------
if __name__ == '__main__':

    agent = VisionAgent('192.168.0.24')

    agent.connect()
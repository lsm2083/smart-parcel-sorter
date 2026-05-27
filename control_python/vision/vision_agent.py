import sys
import json
import time
import cv2
import os
import base64
import numpy as np

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.abspath(os.path.join(BASE_DIR, ".."))
COMMON_DIR = os.path.join(PROJECT_DIR, "common")
AGENTS_DIR = os.path.join(PROJECT_DIR, "agents")

for path in [PROJECT_DIR, COMMON_DIR, AGENTS_DIR]:
    if path not in sys.path:
        sys.path.insert(0, path)

from common.agent_base import AgentBase
from common.topics import VISION_COMMAND, VISION_SCAN_RESULT, VISION_FAIL

from vision.qr_reader import detect_qr
from vision.ocr_reader import detect_ocr


# =========================
# 설정
# =========================

BROKER_HOST = "192.168.0.21"

# 로봇 카메라 스트리밍 주소
# 로봇 IP가 192.168.0.17이고 camera.py가 5001로 송출 중이면 아래 주소 사용
VISION_STREAM_URL = "http://192.168.0.6:8081/stream"

VISION_FRAME_TOPIC = "parcel/vision/frame"

SCAN_TIMEOUT = 7
OCR_WAIT_SECONDS = 2.0
FRAME_SEND_INTERVAL = 0.2


# =========================
# 박스 / 아이스박스 / 비닐 불량 검출기
# =========================

class PackageDefectDetector:
    def __init__(self):
        self.min_area = 8000

        self.corner_angle_min = 65
        self.corner_angle_max = 115

        self.brown_ratio_threshold = 0.10
        self.white_ratio_threshold = 0.16
        self.gray_ratio_threshold = 0.14

        self.lid_gap_dark_ratio_threshold = 0.08
        self.vinyl_edge_ratio_threshold = 0.18

    def normalize_package_type(self, value):
        if value is None:
            return None

        value = str(value).strip().upper()

        if value in ["BOX", "NORMAL_BOX", "BASIC_BOX", "PAPER_BOX", "박스", "기본박스", "일반박스"]:
            return "BOX"

        if value in ["ICEBOX", "ICE_BOX", "ICE", "STYROFOAM", "STYROFOAM_BOX", "아이스박스", "스티로폼"]:
            return "ICEBOX"

        if value in ["VINYL", "VINYL_BOX", "PLASTIC_BAG", "BAG", "비닐", "비닐박스", "비닐택배"]:
            return "VINYL"

        if value in ["DEFECT", "불량"]:
            return "DEFECT"

        return value

    def detect(self, frame, qr_package_type=None):
        qr_package_type = self.normalize_package_type(qr_package_type)

        result = {
            "is_defect": False,
            "package_type": "UNKNOWN",
            "vision_package_type": "UNKNOWN",
            "qr_package_type": qr_package_type,
            "defect_type": "NONE",
            "defect_reason": "정상",
            "score": 0.0
        }

        detected_type = self.detect_package_type_by_color(frame)
        result["vision_package_type"] = detected_type

        if detected_type == "UNKNOWN" and qr_package_type is not None:
            detected_type = qr_package_type

        result["package_type"] = detected_type

        if qr_package_type is not None:
            if detected_type != "UNKNOWN" and qr_package_type != detected_type:
                result["is_defect"] = True
                result["package_type"] = qr_package_type
                result["defect_type"] = "TYPE_MISMATCH"
                result["defect_reason"] = f"QR 타입({qr_package_type})과 카메라 타입({detected_type})이 다름"
                result["score"] = 1.0
                return result

        if detected_type == "BOX":
            return self.detect_box_defect(frame, result)

        if detected_type == "ICEBOX":
            return self.detect_icebox_defect(frame, result)

        if detected_type == "VINYL":
            return self.detect_vinyl_defect(frame, result)

        result["is_defect"] = True
        result["defect_type"] = "UNKNOWN_PACKAGE"
        result["defect_reason"] = "택배 타입을 판별하지 못함"
        result["score"] = 0.8
        return result

    def detect_package_type_by_color(self, frame):
        hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)

        brown_lower = np.array([5, 40, 40])
        brown_upper = np.array([30, 255, 230])

        white_lower = np.array([0, 0, 155])
        white_upper = np.array([180, 75, 255])

        gray_lower = np.array([0, 0, 70])
        gray_upper = np.array([180, 65, 230])

        brown_mask = cv2.inRange(hsv, brown_lower, brown_upper)
        white_mask = cv2.inRange(hsv, white_lower, white_upper)
        gray_mask = cv2.inRange(hsv, gray_lower, gray_upper)

        total = frame.shape[0] * frame.shape[1]

        brown_ratio = cv2.countNonZero(brown_mask) / total
        white_ratio = cv2.countNonZero(white_mask) / total
        gray_ratio = cv2.countNonZero(gray_mask) / total

        if white_ratio > self.white_ratio_threshold:
            return "ICEBOX"

        if brown_ratio > self.brown_ratio_threshold:
            return "BOX"

        if gray_ratio > self.gray_ratio_threshold:
            return "VINYL"

        return "UNKNOWN"

    def detect_box_defect(self, frame, result):
        contour = self.find_main_contour(frame)

        if contour is None:
            result["is_defect"] = True
            result["defect_type"] = "BOX_CONTOUR_FAIL"
            result["defect_reason"] = "기본 박스 외곽선을 찾지 못함"
            result["score"] = 0.8
            return result

        corners = self.get_box_corners(contour)

        if corners is None:
            result["is_defect"] = True
            result["defect_type"] = "BOX_SHAPE_DAMAGE"
            result["defect_reason"] = "기본 박스가 사각형 형태가 아님"
            result["score"] = 0.9
            return result

        if not self.check_corner_angles(corners):
            result["is_defect"] = True
            result["defect_type"] = "BOX_CORNER_DAMAGE"
            result["defect_reason"] = "기본 박스 모서리 각도가 비정상"
            result["score"] = 0.85
            return result

        result["is_defect"] = False
        result["defect_type"] = "NONE"
        result["defect_reason"] = "기본 박스 정상"
        result["score"] = 0.0
        return result

    def detect_icebox_defect(self, frame, result):
        contour = self.find_main_contour(frame)

        if contour is None:
            result["is_defect"] = True
            result["defect_type"] = "ICEBOX_CONTOUR_FAIL"
            result["defect_reason"] = "아이스박스 외곽선을 찾지 못함"
            result["score"] = 0.8
            return result

        corners = self.get_box_corners(contour)

        if corners is None:
            result["is_defect"] = True
            result["defect_type"] = "ICEBOX_SHAPE_DAMAGE"
            result["defect_reason"] = "아이스박스 외곽이 사각형이 아님"
            result["score"] = 0.9
            return result

        if not self.check_corner_angles(corners):
            result["is_defect"] = True
            result["defect_type"] = "ICEBOX_CORNER_DAMAGE"
            result["defect_reason"] = "아이스박스 모서리 깨짐 또는 변형 의심"
            result["score"] = 0.9
            return result

        if self.detect_lid_gap(frame):
            result["is_defect"] = True
            result["defect_type"] = "ICEBOX_LID_OPEN"
            result["defect_reason"] = "아이스박스 뚜껑 벌어짐 의심"
            result["score"] = 0.85
            return result

        result["is_defect"] = False
        result["defect_type"] = "NONE"
        result["defect_reason"] = "아이스박스 정상"
        result["score"] = 0.0
        return result

    def detect_vinyl_defect(self, frame, result):
        contour = self.find_main_contour(frame)

        if contour is None:
            result["is_defect"] = True
            result["defect_type"] = "VINYL_CONTOUR_FAIL"
            result["defect_reason"] = "비닐 택배 외곽선을 찾지 못함"
            result["score"] = 0.7
            return result

        area = cv2.contourArea(contour)

        if area < self.min_area:
            result["is_defect"] = True
            result["defect_type"] = "VINYL_SIZE_ABNORMAL"
            result["defect_reason"] = "비닐 택배 크기가 너무 작거나 인식 면적 부족"
            result["score"] = 0.75
            return result

        if self.detect_torn_vinyl(frame):
            result["is_defect"] = True
            result["defect_type"] = "VINYL_TORN"
            result["defect_reason"] = "비닐 찢어짐 또는 심한 구김 의심"
            result["score"] = 0.8
            return result

        result["is_defect"] = False
        result["defect_type"] = "NONE"
        result["defect_reason"] = "비닐 택배 정상"
        result["score"] = 0.0
        return result

    def find_main_contour(self, frame):
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        blur = cv2.GaussianBlur(gray, (5, 5), 0)
        edges = cv2.Canny(blur, 50, 150)

        kernel = np.ones((3, 3), np.uint8)
        edges = cv2.dilate(edges, kernel, iterations=1)

        contours, _ = cv2.findContours(edges, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

        if len(contours) == 0:
            return None

        contours = sorted(contours, key=cv2.contourArea, reverse=True)
        main_contour = contours[0]

        if cv2.contourArea(main_contour) < self.min_area:
            return None

        return main_contour

    def get_box_corners(self, contour):
        peri = cv2.arcLength(contour, True)

        for epsilon_ratio in [0.02, 0.03, 0.04, 0.05, 0.06]:
            approx = cv2.approxPolyDP(contour, epsilon_ratio * peri, True)

            if len(approx) == 4:
                corners = approx.reshape(4, 2)
                return self.sort_corners(corners)

        return None

    def sort_corners(self, corners):
        corners = np.array(corners)

        s = corners.sum(axis=1)
        diff = np.diff(corners, axis=1)

        top_left = corners[np.argmin(s)]
        bottom_right = corners[np.argmax(s)]
        top_right = corners[np.argmin(diff)]
        bottom_left = corners[np.argmax(diff)]

        return np.array([top_left, top_right, bottom_right, bottom_left])

    def check_corner_angles(self, corners):
        for i in range(4):
            p1 = corners[i]
            p2 = corners[(i + 1) % 4]
            p0 = corners[(i - 1) % 4]

            angle = self.calculate_angle(p0, p1, p2)

            if angle < self.corner_angle_min or angle > self.corner_angle_max:
                return False

        return True

    def calculate_angle(self, p0, p1, p2):
        v1 = p0 - p1
        v2 = p2 - p1

        dot = np.dot(v1, v2)
        norm = np.linalg.norm(v1) * np.linalg.norm(v2)

        if norm == 0:
            return 0

        cos_value = dot / norm
        cos_value = np.clip(cos_value, -1.0, 1.0)

        angle = np.degrees(np.arccos(cos_value))
        return angle

    def detect_lid_gap(self, frame):
        h, w = frame.shape[:2]

        roi_y1 = int(h * 0.25)
        roi_y2 = int(h * 0.55)
        roi_x1 = int(w * 0.20)
        roi_x2 = int(w * 0.80)

        roi = frame[roi_y1:roi_y2, roi_x1:roi_x2]

        if roi.size == 0:
            return False

        gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
        dark_mask = cv2.inRange(gray, 0, 70)

        dark_ratio = cv2.countNonZero(dark_mask) / (roi.shape[0] * roi.shape[1])

        if dark_ratio > self.lid_gap_dark_ratio_threshold:
            return True

        return False

    def detect_torn_vinyl(self, frame):
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        edges = cv2.Canny(gray, 80, 180)

        edge_ratio = cv2.countNonZero(edges) / (frame.shape[0] * frame.shape[1])

        if edge_ratio > self.vinyl_edge_ratio_threshold:
            return True

        return False

    def draw_result(self, frame, result):
        output = frame.copy()

        is_defect = result.get("is_defect", False)
        color = (0, 0, 255) if is_defect else (0, 255, 0)

        text1 = f"TYPE: {result.get('package_type')}"
        text2 = f"DEFECT: {is_defect}"
        text3 = f"CODE: {result.get('defect_type')}"

        cv2.putText(output, text1, (20, 35), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)
        cv2.putText(output, text2, (20, 70), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)
        cv2.putText(output, text3, (20, 105), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)

        return output


class VisionAgent(AgentBase):
    def __init__(self, broker_host):
        super().__init__(
            broker_host,
            "vision",
            "VISION",
            VISION_COMMAND,
            "parcel/vision/result"
        )

        self.defect_detector = PackageDefectDetector()
        self.stream_url = VISION_STREAM_URL

    def _on_connect(self, client, userdata, flags, rc):
        super()._on_connect(client, userdata, flags, rc)

        self.publish_event(f"parcel/{self.device_id}/status", {
            "status": "ONLINE",
            "device_id": self.device_id,
            "device_type": self.device_type,
            "stream_url": self.stream_url
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
                    "scan_method": result.get("scan_method"),

                    "is_defect": result.get("is_defect"),
                    "defect_type": result.get("defect_type"),
                    "defect_reason": result.get("defect_reason"),
                    "defect_score": result.get("defect_score"),

                    "qr_package_type": result.get("qr_package_type"),
                    "vision_package_type": result.get("vision_package_type")
                })
            else:
                self.publish_event(VISION_FAIL, {
                    "type": "SCAN_FAIL",
                    "package_id": package_id,
                    "reason": "QR_AND_OCR_FAILED"
                })

    def scan_package(self):
        cap = cv2.VideoCapture(self.stream_url)

        if not cap.isOpened():
            print("[VISION] 카메라 열기 실패:", self.stream_url)
            return None

        cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)

        start_time = time.time()

        qr_data = None
        qr_detect_time = None
        last_frame_send = 0
        last_defect_result = None

        while time.time() - start_time < SCAN_TIMEOUT:
            ret, frame = cap.read()

            if not ret or frame is None:
                continue

            display_frame = frame.copy()

            h, w, _ = frame.shape

            x1 = int(w * 0.20)
            x2 = int(w * 0.80)
            y1 = int(h * 0.175)
            y2 = int(h * 0.825)

            qr_text, bbox = detect_qr(frame)

            qr_package_type = None
            if qr_data is not None:
                qr_package_type = qr_data.get("package_type")

            defect_result = self.defect_detector.detect(
                frame,
                qr_package_type=qr_package_type
            )
            last_defect_result = defect_result

            display_frame = self.defect_detector.draw_result(display_frame, defect_result)

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

            if time.time() - last_frame_send >= FRAME_SEND_INTERVAL:
                encode_param = [int(cv2.IMWRITE_JPEG_QUALITY), 50]

                success, buffer = cv2.imencode(
                    ".jpg",
                    display_frame,
                    encode_param
                )

                if success:
                    frame_base64 = base64.b64encode(buffer).decode("utf-8")

                    self.publish_event(
                        VISION_FRAME_TOPIC,
                        {
                            "image": frame_base64,
                            "is_defect": defect_result.get("is_defect"),
                            "defect_type": defect_result.get("defect_type"),
                            "package_type": defect_result.get("package_type")
                        }
                    )

                last_frame_send = time.time()

            if qr_text and qr_data is None:
                print("[VISION] QR 인식 성공:", qr_text)

                try:
                    qr_data = json.loads(qr_text)
                    qr_detect_time = time.time()

                    if "package_type" in qr_data:
                        qr_data["package_type"] = self.defect_detector.normalize_package_type(
                            qr_data.get("package_type")
                        )

                except json.JSONDecodeError:
                    print("[VISION] QR JSON 파싱 실패")
                    qr_data = None

            if qr_data and qr_detect_time:
                if time.time() - qr_detect_time >= OCR_WAIT_SECONDS:
                    ocr_area = frame[y1:y2, x1:x2].copy()

                    print("[VISION] OCR 실행")
                    ocr_result = detect_ocr(ocr_area)

                    ocr_text = ocr_result["text"]
                    ocr_confidence = ocr_result["confidence"]

                    print("[VISION] OCR 결과:", ocr_text)
                    print("[VISION] OCR 신뢰도:", ocr_confidence)

                    final_defect_result = self.defect_detector.detect(
                        frame,
                        qr_package_type=qr_data.get("package_type")
                    )

                    is_defect = final_defect_result.get("is_defect", False)

                    qr_data["ocr_text"] = ocr_text
                    qr_data["ocr_confidence"] = ocr_confidence
                    qr_data["scan_method"] = "QR+OCR+DEFECT_CHECK"

                    qr_data["is_defect"] = is_defect
                    qr_data["defect_type"] = final_defect_result.get("defect_type")
                    qr_data["defect_reason"] = final_defect_result.get("defect_reason")
                    qr_data["defect_score"] = final_defect_result.get("score")

                    qr_data["qr_package_type"] = final_defect_result.get("qr_package_type")
                    qr_data["vision_package_type"] = final_defect_result.get("vision_package_type")

                    if final_defect_result.get("package_type") != "UNKNOWN":
                        qr_data["package_type"] = final_defect_result.get("package_type")

                    if is_defect:
                        qr_data["sort_code"] = "DEFECT"
                        print("[VISION] 불량 검출:", final_defect_result.get("defect_type"))
                        print("[VISION] 불량 사유:", final_defect_result.get("defect_reason"))
                    else:
                        if not qr_data.get("sort_code"):
                            region = qr_data.get("region")
                            package_type = qr_data.get("package_type")

                            if region and package_type:
                                qr_data["sort_code"] = f"{region}_{package_type}"

                        print("[VISION] 정상 택배:", qr_data.get("sort_code"))

                    cap.release()
                    return qr_data

        cap.release()

        print("[VISION] 스캔 실패")

        if last_defect_result:
            print("[VISION] 마지막 불량 검출 상태:", last_defect_result)

        return None


if __name__ == "__main__":
    agent = VisionAgent(BROKER_HOST)
    agent.connect()
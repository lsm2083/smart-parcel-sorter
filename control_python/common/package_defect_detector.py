import cv2
import numpy as np


class PackageDefectDetector:
    def __init__(self):
        self.min_area = 8000
        self.corner_angle_min = 65
        self.corner_angle_max = 115
        self.type_confidence_threshold = 0.55

    def detect(self, frame, qr_package_type=None):
        result = {
            "is_defect": False,
            "package_type": "UNKNOWN",
            "defect_type": "NONE",
            "defect_reason": "정상",
            "score": 0.0
        }

        detected_type = self.detect_package_type_by_color(frame)
        result["package_type"] = detected_type

        if qr_package_type is not None:
            if qr_package_type != detected_type and detected_type != "UNKNOWN":
                result["is_defect"] = True
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
        brown_upper = np.array([30, 255, 220])

        white_lower = np.array([0, 0, 150])
        white_upper = np.array([180, 70, 255])

        gray_lower = np.array([0, 0, 70])
        gray_upper = np.array([180, 60, 220])

        brown_mask = cv2.inRange(hsv, brown_lower, brown_upper)
        white_mask = cv2.inRange(hsv, white_lower, white_upper)
        gray_mask = cv2.inRange(hsv, gray_lower, gray_upper)

        brown_ratio = cv2.countNonZero(brown_mask) / (frame.shape[0] * frame.shape[1])
        white_ratio = cv2.countNonZero(white_mask) / (frame.shape[0] * frame.shape[1])
        gray_ratio = cv2.countNonZero(gray_mask) / (frame.shape[0] * frame.shape[1])

        if brown_ratio > 0.12:
            return "BOX"

        if white_ratio > 0.18:
            return "ICEBOX"

        if gray_ratio > 0.15:
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
        approx = cv2.approxPolyDP(contour, 0.03 * peri, True)

        if len(approx) != 4:
            return None

        corners = approx.reshape(4, 2)
        corners = self.sort_corners(corners)
        return corners

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

        gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
        dark_mask = cv2.inRange(gray, 0, 70)

        dark_ratio = cv2.countNonZero(dark_mask) / (roi.shape[0] * roi.shape[1])

        if dark_ratio > 0.08:
            return True

        return False

    def detect_torn_vinyl(self, frame):
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        edges = cv2.Canny(gray, 80, 180)

        edge_ratio = cv2.countNonZero(edges) / (frame.shape[0] * frame.shape[1])

        if edge_ratio > 0.18:
            return True

        return False

    def draw_result(self, frame, result):
        output = frame.copy()

        text1 = f"TYPE: {result['package_type']}"
        text2 = f"DEFECT: {result['is_defect']}"
        text3 = f"REASON: {result['defect_type']}"

        color = (0, 0, 255) if result["is_defect"] else (0, 255, 0)

        cv2.putText(output, text1, (20, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.8, color, 2)
        cv2.putText(output, text2, (20, 80), cv2.FONT_HERSHEY_SIMPLEX, 0.8, color, 2)
        cv2.putText(output, text3, (20, 120), cv2.FONT_HERSHEY_SIMPLEX, 0.8, color, 2)

        return output
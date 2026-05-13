import cv2
import time
from qr_reader import detect_qr
from ocr_reader import detect_ocr


def start_camera():
    cap = cv2.VideoCapture(1, cv2.CAP_DSHOW)

    if not cap.isOpened():
        print("카메라를 열 수 없습니다.")
        return

    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)

    window_name = "Camera Stream"
    processed_qr = set()

    while True:
        ret, frame = cap.read()

        if not ret or frame is None:
            print("프레임을 읽을 수 없습니다.")
            continue

        # QR 인식
        qr_data, bbox = detect_qr(frame)

        # -----------------
        # OCR 영역 항상 표시
        # -----------------
        h, w, _ = frame.shape

        # OCR 영역: 화면 아래쪽 글자 영역만
        x1 = int(w * 0.20)
        x2 = int(w * 0.75)

        y1 = int(h * 0.55)
        y2 = int(h * 0.90)

        cv2.rectangle(
            frame,
            (x1, y1),
            (x2, y2),
            (255, 0, 0),
            2
        )

        cv2.putText(
            frame,
            "OCR AREA",
            (x1, y1 - 10),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.6,
            (255, 0, 0),
            2
        )

        # -----------------
        # QR 박스 표시
        # -----------------
        if bbox is not None:
            points = bbox[0]

            for i in range(len(bbox[0])):
                pt1 = tuple(bbox[0][i])
                pt2 = tuple(bbox[0][(i + 1) % len(bbox[0])])
                cv2.line(frame, pt1, pt2, (0, 255, 0), 2)

            cv2.putText(
                frame,
                "QR Detected",
                tuple(points[0]),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.7,
                (0, 255, 0),
                2
            )

        # -----------------
        # QR 처음 인식 시 자동 OCR
        # -----------------
        if qr_data and qr_data not in processed_qr:
            processed_qr.add(qr_data)

            print("QR 인식 성공:", qr_data)

            

            ocr_area = frame[y1:y2, x1:x2]

            print("\n===== 자동 OCR 실행 =====")
            result = detect_ocr(ocr_area)
            print("OCR 결과:", result["text"])
            print("OCR 신뢰도:", result["confidence"])
            print("========================\n")

        cv2.imshow(window_name, frame)

        key = cv2.waitKey(1) & 0xFF

        if key == ord("q"):
            print("카메라 종료")
            break

        if key == ord("r"):
            processed_qr.clear()
            print("처리 기록 초기화")

    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    start_camera()
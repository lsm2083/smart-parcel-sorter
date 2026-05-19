import cv2
import time
import threading
from qr_reader import detect_qr
from ocr_reader import detect_ocr


def run_ocr_thread(ocr_area):
    print("\n===== 자동 OCR 실행 =====")
    result = detect_ocr(ocr_area)
    print("OCR 결과:", result["text"])
    print("OCR 신뢰도:", result["confidence"])
    print("========================\n")


def start_camera():
    cap = cv2.VideoCapture(1, cv2.CAP_DSHOW)

    if not cap.isOpened():
        print("카메라를 열 수 없습니다.")
        return

    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)

    window_name = "Camera Stream"
    processed_qr = set()

    pending_ocr = False
    ocr_start_time = 0

    while True:
        ret, frame = cap.read()

        if not ret or frame is None:
            continue

        qr_data, bbox = detect_qr(frame)

        h, w, _ = frame.shape

        x1 = int(w * 0.20)
        x2 = int(w * 0.80)
        y1 = int(h * 0.175)
        y2 = int(h * 0.825)

        cv2.rectangle(frame, (x1, y1), (x2, y2), (255, 0, 0), 2)

        if qr_data and qr_data not in processed_qr:
            processed_qr.add(qr_data)
            print("QR 인식 성공:", qr_data)

            pending_ocr = True
            ocr_start_time = time.time()

        if pending_ocr and time.time() - ocr_start_time >= 2.0:
            pending_ocr = False

            # 이 frame이 진짜 1초 뒤 현재 화면
            ocr_area = frame[y1:y2, x1:x2].copy()

            threading.Thread(
                target=run_ocr_thread,
                args=(ocr_area,),
                daemon=True
            ).start()

        cv2.imshow(window_name, frame)

        key = cv2.waitKey(1) & 0xFF

        if key == ord("q"):
            break

        if key == ord("r"):
            processed_qr.clear()
            pending_ocr = False
            print("처리 기록 초기화")

    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    start_camera()
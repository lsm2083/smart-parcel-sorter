import cv2

detector = cv2.QRCodeDetector()


def detect_qr(frame):
    if frame is None:
        return None, None

    if frame.size == 0:
        return None, None

    try:
        data, bbox, _ = detector.detectAndDecode(frame)

        if data:
            return data, bbox

        return None, None

    except cv2.error as e:
        print("QR 인식 중 오류:", e)
        return None, None
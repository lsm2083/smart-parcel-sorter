import easyocr
import cv2

reader = easyocr.Reader(["ko", "en"], gpu=False)


def detect_ocr(frame):
    # 1. 확대
    img = cv2.resize(frame, None, fx=2.0, fy=2.0)

    # 2. 흑백 변환
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)

    # 3. 노이즈 제거
    blur = cv2.GaussianBlur(gray, (3, 3), 0)

    # 4. 대비 증가
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    enhanced = clahe.apply(blur)

    # 5. 이진화
    thresh = cv2.adaptiveThreshold(
        enhanced,
        255,
        cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        cv2.THRESH_BINARY,
        31,
        10
    )

    results = reader.readtext(thresh)

    texts = []
    confidences = []

    for bbox, text, confidence in results:
        if confidence < 0.2:
            continue

        texts.append(text)
        confidences.append(confidence)

    full_text = " ".join(texts)

    avg_confidence = 0.0
    if confidences:
        avg_confidence = sum(confidences) / len(confidences)

    return {
        "text": full_text,
        "confidence": avg_confidence,
        "raw": results
    }
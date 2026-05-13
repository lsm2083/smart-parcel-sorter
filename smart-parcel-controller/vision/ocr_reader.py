import easyocr
import cv2

reader = easyocr.Reader(["ko", "en"], gpu=False)


def detect_ocr(frame):
    # 1. 크기 확대
    resized = cv2.resize(frame, None, fx=2.0, fy=2.0)

    # 2. 흑백 변환
    gray = cv2.cvtColor(resized, cv2.COLOR_BGR2GRAY)

    # 3. 노이즈 제거
    blur = cv2.GaussianBlur(gray, (3, 3), 0)

    # 4. 글자 선명하게 이진화
    thresh = cv2.threshold(
        blur,
        0,
        255,
        cv2.THRESH_BINARY + cv2.THRESH_OTSU
    )[1]

    # 5. OCR 실행
    results = reader.readtext(resized)

    texts = []
    confidences = []

    for bbox, text, confidence in results:
        # 너무 낮은 신뢰도는 버림
        if confidence < 0.3:
            continue

        texts.append(text)
        confidences.append(confidence)

    full_text = "\n".join(texts)

    avg_confidence = 0.0
    if confidences:
        avg_confidence = sum(confidences) / len(confidences)

    return {
        "text": full_text,
        "confidence": avg_confidence,
        "raw": results
    }

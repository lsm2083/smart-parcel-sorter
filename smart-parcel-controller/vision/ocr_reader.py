import easyocr
import cv2

# 이건 반드시 함수 밖에 있어야 함
reader = easyocr.Reader(["ko", "en"], gpu=False)


def detect_ocr(frame):
    # OCR 영역이 너무 크면 줄이기
    h, w = frame.shape[:2]

    # 너무 큰 이미지만 축소
    max_width = 500
    if w > max_width:
        scale = max_width / w
        frame = cv2.resize(frame, None, fx=scale, fy=scale)

    # 흑백 변환만 간단히
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)

    # OCR 실행
    results = reader.readtext(
        gray,
        detail=1,
        paragraph=False,
        batch_size=1
    )

    texts = []
    confidences = []

    for bbox, text, confidence in results:
        if confidence < 0.25:
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
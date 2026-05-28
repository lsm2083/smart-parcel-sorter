import threading
import cv2
import easyocr
import time

# 반드시 함수 밖에서 1회만 생성합니다.
# 함수 안에서 Reader를 만들면 OCR 실행마다 모델을 다시 로딩해서 매우 느려집니다.
# reader = easyocr.Reader(["ko", "en"], gpu=False)
reader = None

def _load_reader():
    global reader
    reader = easyocr.Reader(["ko", "en"], gpu=False)
    print("[OCR] 모델 로딩 완료")

threading.Thread(target=_load_reader, daemon=True).start()

def _to_easyocr_image(image):
    if image is None:
        return None

    if len(image.shape) == 2:
        return image

    if len(image.shape) == 3 and image.shape[2] == 4:
        image = cv2.cvtColor(image, cv2.COLOR_BGRA2BGR)

    return image


def _resize_if_too_large(image, max_width=900):
    h, w = image.shape[:2]

    if w <= max_width:
        return image

    scale = max_width / float(w)
    return cv2.resize(image, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)


def _sort_ocr_results(results):
    def key_func(item):
        bbox, text, confidence = item
        xs = [p[0] for p in bbox]
        ys = [p[1] for p in bbox]
        return (sum(ys) / len(ys), sum(xs) / len(xs))

    return sorted(results, key=key_func)


def _normalize_piece(text):
    text = str(text).strip()
    text = text.replace("\n", " ").replace("\r", " ")
    return " ".join(text.split())


def detect_ocr(frame):
    global reader

    if reader is None:
        print("[OCR] 모델 로딩 중... 대기")
        while reader is None:
            time.sleep(0.1)

    if frame is None or frame.size == 0:
        return {
            "text": "",
            "confidence": 0.0,
            "raw": [],
            "message": "EMPTY_FRAME"
        }

    image = _to_easyocr_image(frame)

    if image is None or image.size == 0:
        return {
            "text": "",
            "confidence": 0.0,
            "raw": [],
            "message": "EMPTY_IMAGE"
        }

    # cam_stream_test.py에서 이미 crop/확대를 수행하므로 여기서는 과한 전처리를 하지 않습니다.
    # blur/adaptiveThreshold를 넣으면 한글이 뭉개져서 '서울 -> 서움' 같은 오인식이 늘 수 있습니다.
    image = _resize_if_too_large(image, max_width=600)

    try:
        results = reader.readtext(
            # image,
            # detail=1,
            # paragraph=False,
            # batch_size=1,
            # workers=0,
            # decoder="greedy",
            # width_ths=1.0,
            # height_ths=0.7,
            # ycenter_ths=0.7,
            # text_threshold=0.30,
            # low_text=0.15,
            # link_threshold=0.35,
            # add_margin=0.04,
            # canvas_size=1280,
            # mag_ratio=1.0,
            # contrast_ths=0.10,
            # adjust_contrast=0.50

            # 최적화
            image,
            detail=1,
            paragraph=False,
            batch_size=1,
            workers=0,
            decoder="greedy",
            canvas_size=960,
            mag_ratio=1.0
        )
    except Exception as e:
        return {
            "text": "",
            "confidence": 0.0,
            "raw": [],
            "message": f"EASYOCR_EXCEPTION: {e}"
        }

    texts = []
    confidences = []

    for bbox, text, confidence in _sort_ocr_results(results):
        text = _normalize_piece(text)

        if not text:
            continue

        confidence = float(confidence)

        # 라벨 글자는 confidence가 낮게 나오는 경우가 많아서 낮게 둡니다.
        # 최종 성공 여부는 cam_stream_test.py의 파싱 결과로 판단합니다.
        if confidence < 0.05:
            continue

        texts.append(text)
        confidences.append(confidence)

    full_text = " ".join(texts).strip()

    avg_confidence = 0.0
    if confidences:
        avg_confidence = sum(confidences) / len(confidences)

    return {
        "text": full_text,
        "confidence": avg_confidence,
        "raw": results,
        "message": "OK" if full_text else "NO_TEXT"
    }

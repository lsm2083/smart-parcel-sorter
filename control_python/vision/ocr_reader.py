"""
이미지에서 OCR 텍스트 추출. EasyOCR 사용 (한글+영어).

전처리 전략:
  - USE_PREPROCESSING=True: 원본도 시도, 전처리(그레이+이진화)도 시도, 더 좋은 쪽 채택
  - USE_PREPROCESSING=False: 원본만 시도 (속도 2배 빠름)
"""
import cv2
import easyocr
import numpy as np


# 전처리 활성화 여부. False면 원본만 OCR → 속도 2배 빠름.
USE_PREPROCESSING = False


_reader = None


def _get_reader():
    global _reader
    if _reader is None:
        print("[OCR] 모델 로딩 중...")
        _reader = easyocr.Reader(['ko', 'en'], gpu=False)
        print("[OCR] 모델 로딩 완료")
    return _reader


def _preprocess_for_ocr(image):
    """
    OCR용 전처리: 그레이스케일 → 대비 강화 → 적응형 이진화.
    검정 글자 + 흰 배경 라벨에 최적화.
    """
    if image is None or image.size == 0:
        return None

    # 그레이스케일
    if len(image.shape) == 3:
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    else:
        gray = image.copy()

    # CLAHE: 국부 대비 강화 (조명 균일하지 않을 때 효과적)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    enhanced = clahe.apply(gray)

    # 적응형 이진화 (OTSU보다 조명 변화에 강함)
    binary = cv2.adaptiveThreshold(
        enhanced, 255,
        cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        cv2.THRESH_BINARY,
        blockSize=31,   # 31x31 윈도우. 글자 크기보다 살짝 크게
        C=10            # 평균에서 빼는 값. 클수록 더 엄격
    )

    # 살짝 노이즈 제거 (글자가 깨지지 않을 정도로만)
    kernel = np.ones((2, 2), np.uint8)
    binary = cv2.morphologyEx(binary, cv2.MORPH_CLOSE, kernel)

    return binary


def _run_ocr(reader, image):
    """단일 이미지로 OCR 시도. 결과 dict 또는 None."""
    try:
        # 속도 옵션:
        #  - canvas_size: 내부 처리 해상도 상한 (작을수록 빠름)
        #  - mag_ratio: 확대 배율 (1.0이면 확대 안 함, 빠름)
        #  - text_threshold/low_text: 검출 민감도 (라벨은 글자 크고 선명하니 높여도 됨)
        results = reader.readtext(
            image,
            canvas_size=480,
            mag_ratio=1.0,
            text_threshold=0.6,
            low_text=0.4,
        )
    except Exception as e:
        print(f"[OCR] 인식 오류: {e}")
        return None

    if not results:
        return None

    texts = []
    confs = []
    for _, text, conf in results:
        text = text.strip()
        if text:
            texts.append(text)
            confs.append(conf)

    if not texts:
        return None

    return {
        "text": " ".join(texts),
        "confidence": sum(confs) / len(confs),
    }


def detect_ocr(image):
    """
    이미지(BGR/Gray numpy array) → OCR 결과 dict.
    USE_PREPROCESSING=False면 원본만, True면 원본+이진화 둘 다 시도.
    """
    if image is None or image.size == 0:
        return None

    reader = _get_reader()

    # 원본 시도
    raw_result = _run_ocr(reader, image)

    if not USE_PREPROCESSING:
        return raw_result

    # 전처리 후 시도
    preprocessed = _preprocess_for_ocr(image)
    pp_result = _run_ocr(reader, preprocessed) if preprocessed is not None else None

    # 더 좋은 결과 선택 (text 길이 + confidence 가중)
    def _score(r):
        if r is None:
            return -1.0
        return len(r["text"]) * r["confidence"]

    if _score(pp_result) > _score(raw_result):
        return pp_result
    return raw_result
import qrcode
import json


# QR 안에 넣을 데이터
data = {
    "invoice_no": "202605120001",
    "region": "서울",
    "package_type": "BOX",
    "sort_code": "SEOUL_BOX"
}

# JSON 문자열 변환
qr_text = json.dumps(data, ensure_ascii=False)

# QR 생성
img = qrcode.make(qr_text)

# 저장
img.save("test_invoice_qr.png")

print("QR 생성 완료")
print(qr_text)
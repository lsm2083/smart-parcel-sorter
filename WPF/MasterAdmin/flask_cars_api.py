"""
승민이 Flask 서버에 추가할 자동차 상태 API 예시
-----------------------------------------------------
라파카(로봇팔)가 분류박스를 채울 때 → POST /api/cars/<car_id>/slot
아두이노 자동차가 출발할 때         → POST /api/cars/<car_id>/depart
C# 총괄페이지가 2초마다 폴링        → GET  /api/cars/status
"""

from flask import Flask, jsonify, request
from datetime import datetime

app = Flask(__name__)

# ── 인메모리 상태 (DB가 있으면 아래를 DB 조회로 교체) ──────────────────
cars = {
    "car_1": {
        "car_id":       "car_1",
        "car_name":     "자동차 1호",
        "status":       "출발전",   # "출발전" | "출발중"
        "filled_slots": 0,
        "total_slots":  4,          # 분류박스 총 칸 수 (프로젝트에 맞게 수정)
        "last_updated": datetime.now().isoformat(),
    },
    "car_2": {
        "car_id":       "car_2",
        "car_name":     "자동차 2호",
        "status":       "출발전",
        "filled_slots": 0,
        "total_slots":  4,
        "last_updated": datetime.now().isoformat(),
    },
    # 자동차 추가 시 여기에 계속 추가
}


# ── GET /api/cars/status ───────────────────────────────────────────────
# C# 총괄페이지가 2초마다 호출 → 전체 자동차 상태 리스트 반환
@app.route("/api/cars/status", methods=["GET"])
def get_cars_status():
    return jsonify(list(cars.values()))


# ── POST /api/cars/<car_id>/slot ───────────────────────────────────────
# 라파카(로봇팔)가 분류박스 한 칸 채울 때 호출
# Body: { "filled_slots": 3 }  또는 increment 방식
@app.route("/api/cars/<car_id>/slot", methods=["POST"])
def update_slot(car_id):
    if car_id not in cars:
        return jsonify({"error": "차량 없음"}), 404

    data = request.get_json(silent=True) or {}
    car  = cars[car_id]

    # 방법 A: filled_slots 직접 지정
    if "filled_slots" in data:
        car["filled_slots"] = int(data["filled_slots"])
    else:
        # 방법 B: 1씩 증가 (라파카가 박스 넣을 때마다 호출)
        car["filled_slots"] = min(car["filled_slots"] + 1, car["total_slots"])

    car["last_updated"] = datetime.now().isoformat()

    # 분류박스가 다 채워지면 자동으로 "출발중" 으로 바꿔도 됨
    # (아두이노가 직접 출발 신호를 보내는 구조면 아래 줄 삭제)
    # if car["filled_slots"] >= car["total_slots"]:
    #     car["status"] = "출발중"

    return jsonify(car)


# ── POST /api/cars/<car_id>/depart ─────────────────────────────────────
# 아두이노 자동차가 실제 출발 감지 시 Flask로 상태 전송
# Body: { "status": "출발중" }
@app.route("/api/cars/<car_id>/depart", methods=["POST"])
def depart_car(car_id):
    if car_id not in cars:
        return jsonify({"error": "차량 없음"}), 404

    data   = request.get_json(silent=True) or {}
    status = data.get("status", "출발중")  # "출발중" or "출발전"

    car             = cars[car_id]
    car["status"]   = status
    car["last_updated"] = datetime.now().isoformat()

    # 출발 완료 후 분류박스 초기화 (필요 시)
    if status == "출발전":
        car["filled_slots"] = 0

    return jsonify(car)


# ── POST /api/cars/<car_id>/reset ──────────────────────────────────────
# 자동차가 복귀하거나 리셋 시 상태 초기화
@app.route("/api/cars/<car_id>/reset", methods=["POST"])
def reset_car(car_id):
    if car_id not in cars:
        return jsonify({"error": "차량 없음"}), 404

    car = cars[car_id]
    car["status"]       = "출발전"
    car["filled_slots"] = 0
    car["last_updated"] = datetime.now().isoformat()

    return jsonify(car)


if __name__ == "__main__":
    app.run(debug=True, port=5000)

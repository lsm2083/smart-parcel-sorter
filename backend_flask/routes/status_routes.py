from flask import Blueprint, jsonify
from database.db import get_db

status_bp = Blueprint('status', __name__)


@status_bp.route('/status')
def get_status():
    conn = get_db()
    cur = conn.cursor()

    # 장비 상태 조회
    cur.execute("SELECT device_id, status FROM device_status")
    devices = {row['device_id']: row['status'] for row in cur.fetchall()}

    def label(device_id):
        s = devices.get(device_id, 'DISCONNECTED')
        return {'ONLINE': '작동중', 'DISCONNECTED': '오프라인'}.get(s, s)

    # 오늘 통계
    cur.execute(
        "SELECT COUNT(*) AS cnt FROM sort_logs WHERE DATE(completed_at)=CURDATE() AND sort_result='SORT_DONE'"
    )
    today_sorted = cur.fetchone()['cnt']

    cur.execute(
        "SELECT COUNT(*) AS cnt FROM error_logs WHERE DATE(created_at)=CURDATE()"
    )
    today_error = cur.fetchone()['cnt']

    cur.execute(
        "SELECT COUNT(*) AS cnt FROM sort_logs WHERE DATE(completed_at)=CURDATE()"
    )
    total_today = cur.fetchone()['cnt']
    success_rate = round((today_sorted / total_today * 100), 1) if total_today > 0 else 0.0

    conn.close()

    return jsonify({
        "conveyorStatus":   label('conveyor_agent_01'),
        "conveyorSpeed":    1.3,
        "robotArmStatus":   label('robot_agent_01'),
        "ocrCamStatus":     label('vision_agent_01'),
        "qrCamStatus":      label('vision_agent_01'),
        "emergencyStop":    False,
        "inputUnitStatus":  "대기",
        "todaySortedCount": today_sorted,
        "todayErrorCount":  today_error,
        "successRate":      success_rate,
    })


@status_bp.route('/devices')
def get_devices():
    conn = get_db()
    cur = conn.cursor()
    cur.execute("SELECT * FROM device_status")
    rows = cur.fetchall()
    conn.close()
    return jsonify({'devices': list(rows)})
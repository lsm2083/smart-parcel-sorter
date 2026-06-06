"""
blackbox_routes.py — 블랙박스 이벤트 이력 조회 (REST API)

두 가지 용도:
1. WPF 총괄 관제 탭: 과거 불량 이력 테이블 (이미지 URL 포함)
2. WPF 분류 로그: 특정 package_id의 불량 이미지 조회

WebSocket(blackbox_event_added)은 실시간 푸시용,
이 REST API는 과거 이력 조회 + 페이지 새로고침 시 복원용.
"""

from flask import Blueprint, request, jsonify
from database.db import get_db

blackbox_bp = Blueprint('blackbox', __name__)

# Flask 서버 base URL (WPF가 이미지 접근할 때 사용)
BASE_URL = "http://192.168.0.21:5000"


@blackbox_bp.route('/blackbox/events', methods=['GET'])
def get_blackbox_events():
    """
    블랙박스 이벤트 전체 이력 조회.
    Vision 실패 + YOLO 박스 불량 다 포함.

    쿼리 파라미터:
      - limit: 최대 건수 (기본 50)
      - event_type: 필터 (예: 'Vision실패', '박스불량')
      - package_id: 특정 박스만

    응답 예시:
    [
      {
        "id": 5,
        "event_type": "Vision실패",
        "camera_id": "qr_camera",
        "package_id": 12,
        "image_path": "blackbox/defect/vision_fail_xxx.jpg",
        "image_url": "http://192.168.0.21:5000/storage/blackbox/defect/vision_fail_xxx.jpg",
        "severity": "오류",
        "description": "스캔 실패 — SCAN_TIMEOUT",
        "created_at": "2026-06-04 17:20:10"
      },
      ...
    ]
    """
    limit = request.args.get('limit', 50, type=int)
    event_type = request.args.get('event_type', None)
    package_id = request.args.get('package_id', None, type=int)

    conn = get_db()
    try:
        cur = conn.cursor()

        query = """
            SELECT be.id, be.event_type, be.camera_id, be.package_id,
                   be.image_path, be.severity, be.description, be.created_at,
                   p.invoice_no, p.sort_code, p.region
            FROM blackbox_events be
            LEFT JOIN packages p ON p.id = be.package_id
            WHERE 1=1
        """
        params = []

        if event_type:
            query += " AND be.event_type = %s"
            params.append(event_type)

        if package_id:
            query += " AND be.package_id = %s"
            params.append(package_id)

        query += " ORDER BY be.id DESC LIMIT %s"
        params.append(limit)

        cur.execute(query, params)
        rows = cur.fetchall()
    finally:
        conn.close()

    result = []
    for r in rows:
        img_path = r['image_path']
        img_url = f"{BASE_URL}/storage/{img_path}" if img_path else None

        result.append({
            "id":              r['id'],
            "event_type":      r['event_type'],
            "camera_id":       r['camera_id'] or '',
            "package_id":      r['package_id'],
            "invoice_no":      r['invoice_no'] or '',
            "tracking_number": r['invoice_no'] or '',
            "sort_code":       r['sort_code'] or '',
            "region":          r['region'] or '',
            "image_path":      img_path or '',
            "image_url":       img_url,
            "severity":        r['severity'] or '',
            "description":     r['description'] or '',
            "created_at":      str(r['created_at'] or '')[:19],
        })

    return jsonify(result)


@blackbox_bp.route('/blackbox/events/<int:event_id>', methods=['GET'])
def get_blackbox_event_detail(event_id):
    """
    특정 블랙박스 이벤트 상세 조회.
    WPF에서 이미지 클릭 시 상세 정보 표시용.
    """
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("""
            SELECT be.id, be.event_type, be.camera_id, be.package_id,
                   be.image_path, be.severity, be.description, be.created_at,
                   p.invoice_no, p.sort_code, p.region, p.package_type,
                   p.recipient_name, p.status
            FROM blackbox_events be
            LEFT JOIN packages p ON p.id = be.package_id
            WHERE be.id = %s
        """, (event_id,))
        r = cur.fetchone()
    finally:
        conn.close()

    if not r:
        return jsonify({"error": "이벤트 없음"}), 404

    img_path = r['image_path']
    img_url = f"{BASE_URL}/storage/{img_path}" if img_path else None

    return jsonify({
        "id":              r['id'],
        "event_type":      r['event_type'],
        "camera_id":       r['camera_id'] or '',
        "package_id":      r['package_id'],
        "invoice_no":      r['invoice_no'] or '',
        "tracking_number": r['invoice_no'] or '',
        "sort_code":       r['sort_code'] or '',
        "region":          r['region'] or '',
        "package_type":    r.get('package_type', ''),
        "recipient_name":  r.get('recipient_name', ''),
        "status":          r.get('status', ''),
        "image_path":      img_path or '',
        "image_url":       img_url,
        "severity":        r['severity'] or '',
        "description":     r['description'] or '',
        "created_at":      str(r['created_at'] or '')[:19],
    })


@blackbox_bp.route('/blackbox/events/summary', methods=['GET'])
def get_blackbox_summary():
    """
    블랙박스 이벤트 요약 통계.
    WPF 총괄 관제 탭 상단 카드에 표시.
    """
    conn = get_db()
    try:
        cur = conn.cursor()

        # 오늘 이벤트 수
        cur.execute("""
            SELECT COUNT(*) as cnt FROM blackbox_events
            WHERE DATE(created_at) = CURDATE()
        """)
        today_count = cur.fetchone()['cnt']

        # 유형별 카운트
        cur.execute("""
            SELECT event_type, COUNT(*) as cnt
            FROM blackbox_events
            WHERE DATE(created_at) = CURDATE()
            GROUP BY event_type
        """)
        by_type = {r['event_type']: r['cnt'] for r in cur.fetchall()}

        # 최근 이벤트
        cur.execute("""
            SELECT be.id, be.event_type, be.image_path, be.created_at,
                   p.invoice_no
            FROM blackbox_events be
            LEFT JOIN packages p ON p.id = be.package_id
            ORDER BY be.id DESC LIMIT 5
        """)
        recent = []
        for r in cur.fetchall():
            img_path = r['image_path']
            recent.append({
                "id":         r['id'],
                "event_type": r['event_type'],
                "invoice_no": r['invoice_no'] or '',
                "image_url":  f"{BASE_URL}/storage/{img_path}" if img_path else None,
                "created_at": str(r['created_at'] or '')[:19],
            })
    finally:
        conn.close()

    return jsonify({
        "today_total":    today_count,
        "by_type":        by_type,
        "recent_events":  recent,
    })
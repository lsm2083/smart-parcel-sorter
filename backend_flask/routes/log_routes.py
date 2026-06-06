from flask import Blueprint, jsonify
from database.db import get_db

log_bp = Blueprint('logs', __name__)


@log_bp.route('/logs/sort')
def get_sort_logs():
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("""
            SELECT sl.*, p.region
            FROM sort_logs sl
            LEFT JOIN packages p ON sl.package_id = p.id
            ORDER BY sl.completed_at DESC LIMIT 100
        """)
        rows = cur.fetchall()
    finally:
        conn.close()

    result = []
    for r in rows:
        sort_result = r['sort_result'] or ''
        status = '정상' if sort_result == 'SORT_DONE' else '오류'
        result.append({
            "id":              r['id'],
            "timestamp":       str(r['completed_at'] or '')[:19],
            "trackingNumber":  r['invoice_no'] or '',
            "recognitionType": r['scan_method'] or 'QR',
            "region":          r.get('region', '') or '',
            "status":          status,
            "errorType":       '-' if sort_result == 'SORT_DONE' else sort_result,
            "processingTime":  round((r['duration_ms'] or 0) / 1000.0, 2),
            "confidence":      0.0,
        })
    return jsonify(result)


@log_bp.route('/logs/shipping')
def get_shipping_logs():
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("SELECT * FROM shipping_logs ORDER BY created_at DESC LIMIT 100")
        rows = cur.fetchall()
    finally:
        conn.close()

    result = []
    for r in rows:
        result.append({
            "id":             r['id'],
            "timestamp":      str(r['created_at'] or '')[:19],
            "trackingNumber": r['invoice_no'] or '',
            "region":         r['region'] or '',
            "destination":    r['destination'] or '',
            "status":         r['status'] or '출고대기',
            "slotNumber":     r['slot_number'] or 0,
        })
    return jsonify(result)


@log_bp.route('/blackbox/events')
def get_blackbox_events():
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("""
            SELECT be.*, p.invoice_no
            FROM blackbox_events be
            LEFT JOIN packages p ON be.package_id = p.id
            ORDER BY be.created_at DESC LIMIT 100
        """)
        rows = cur.fetchall()
    finally:
        conn.close()

    result = []
    for r in rows:
        event_type = r['event_type'] or ''
        result.append({
            "id":             r['id'],
            "timestamp":      str(r['created_at'] or '')[:19],
            "eventType":      event_type,
            "description":    r['description'] or '',
            "imagePath":      r['image_path'] or '',
            "saveFolder":     f"blackbox/{event_type}/",
            "severity":       r['severity'] or '오류',
            "trackingNumber": r.get('invoice_no', '') or '',
        })
    return jsonify(result)


@log_bp.route('/logs/login')
def get_login_records():
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("SELECT * FROM login_records ORDER BY created_at DESC LIMIT 100")
        rows = cur.fetchall()
    finally:
        conn.close()

    result = []
    for r in rows:
        result.append({
            "id":        r['id'],
            "timestamp": str(r['created_at'] or '')[:19],
            "userId":    r['user_id'] or '',
            "userName":  r['user_name'] or '',
            "role":      r['role'] or 'OPERATOR',
            "ipAddress": r['ip_address'] or '',
            "action":    r['action'] or '로그인',
            "success":   bool(r['success']),
        })
    return jsonify(result)


@log_bp.route('/logs/error')
def get_error_logs():
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("SELECT * FROM error_logs ORDER BY created_at DESC LIMIT 50")
        rows = cur.fetchall()
        return jsonify({'logs': list(rows)})
    finally:
        conn.close()
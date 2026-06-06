from database.db import get_db
from sockets.wpf_events import emit_blackbox_event_added

_EVENT_LABEL = {
    'PICK_FAIL':       ('분류실패', '오류'),
    'DROP_FAIL':       ('분류실패', '오류'),
    'ROBOT_PICK_FAIL': ('분류실패', '오류'),
    'ROBOT_DROP_FAIL': ('분류실패', '오류'),
    'CONVEYOR_JAM':    ('박스걸림', '경고'),
    'OCR_READ_FAIL':   ('OCR실패', '경고'),
    'QR_READ_FAIL':    ('OCR실패', '경고'),
}


def handle_snapshot(data):
    event_type_raw = data.get('event_type', 'UNKNOWN')
    image_path = data.get('image_path', '')
    package_id = data.get('package_id')
    event_label, severity = _EVENT_LABEL.get(event_type_raw, (event_type_raw, '오류'))

    description = data.get('description', '')
    if not description:
        description = _build_description(event_type_raw, data)

    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("""
            INSERT INTO blackbox_events (event_type, camera_id, package_id, image_path, severity, description)
            VALUES (%s, %s, %s, %s, %s, %s)
        """, (event_label, data.get('camera_id'), package_id, image_path, severity, description))
        row_id = cur.lastrowid

        invoice_no = ''
        if package_id:
            cur.execute("SELECT invoice_no FROM packages WHERE id=%s", (package_id,))
            pkg = cur.fetchone()
            if pkg:
                invoice_no = pkg['invoice_no'] or ''

        conn.commit()
    finally:
        conn.close()

    from datetime import datetime
    emit_blackbox_event_added({
        "id": row_id,
        "timestamp": datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S"),
        "eventType": event_label,
        "description": description,
        "imagePath": image_path,
        "saveFolder": f"blackbox/{event_type_raw.lower()}/",
        "severity": severity,
        "trackingNumber": invoice_no,
    })


def _build_description(event_type, data):
    region = data.get('region', '')
    if event_type in ('PICK_FAIL', 'DROP_FAIL', 'ROBOT_PICK_FAIL', 'ROBOT_DROP_FAIL'):
        return f"로봇팔 분류 실패 — 권역: {region}" if region else "로봇팔 분류 실패"
    if event_type == 'CONVEYOR_JAM':
        return "컨베이어 걸림 감지"
    if event_type in ('OCR_READ_FAIL', 'QR_READ_FAIL'):
        return f"인식 실패 — {data.get('reason', '')}"
    return event_type
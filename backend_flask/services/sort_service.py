from database.db import get_db
from sockets.wpf_events import (
    emit_sort_completed, emit_robot_status,
    emit_sorting_log_added, emit_shipping_log_added,
)
from mqtt.command_publish import publish_blackbox_snapshot, publish_robot_home


def _get_destination_info(sort_code):
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute(
            "SELECT region, destination_name FROM sort_destinations WHERE sort_code=%s",
            (sort_code,)
        )
        row = cur.fetchone()
    finally:
        conn.close()
    if row:
        return row['region'], row['destination_name']
    return '기타', '기타'


def handle_sort_result(data):
    pid = data['package_id']
    sort_code = data.get('sort_code', '')
    result = data['result']
    region, destination = _get_destination_info(sort_code)

    if result == 'SORT_DONE':
        sort_log_id, ship_log_id, invoice_no = _process_sort_done(pid, sort_code, data, region, destination)

        emit_sort_completed(pid, sort_code)
        emit_robot_status('ROBOT_SORT_DONE', pid)
        publish_robot_home()

        emit_sorting_log_added({
            "id": sort_log_id,
            "timestamp": _now(),
            "trackingNumber": invoice_no,
            "recognitionType": data.get('scan_method', 'QR'),
            "region": region,
            "status": "정상",
            "errorType": "-",
            "processingTime": round((data.get('duration_ms', 0)) / 1000.0, 2),
            "confidence": data.get('confidence', 0.0),
        })

        emit_shipping_log_added({
            "id": ship_log_id,
            "timestamp": _now(),
            "trackingNumber": invoice_no,
            "region": region,
            "destination": destination,
            "status": "출고대기",
            "slotNumber": data.get('slot_number', 0),
        })

    elif result in ('PICK_FAIL', 'DROP_FAIL'):
        sort_log_id, invoice_no = _process_sort_fail(pid, sort_code, result, data)

        publish_blackbox_snapshot(reason=result)

        emit_sorting_log_added({
            "id": sort_log_id,
            "timestamp": _now(),
            "trackingNumber": invoice_no,
            "recognitionType": data.get('scan_method', 'QR'),
            "region": region,
            "status": "오류",
            "errorType": f'ROBOT_{result}',
            "processingTime": round((data.get('duration_ms', 0)) / 1000.0, 2),
            "confidence": data.get('confidence', 0.0),
        })


def _process_sort_done(pid, sort_code, data, region, destination):
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute(
            "UPDATE packages SET status='SORT_DONE', completed_at=NOW() WHERE id=%s",
            (pid,)
        )

        cur.execute("SELECT invoice_no FROM packages WHERE id=%s", (pid,))
        pkg = cur.fetchone()
        invoice_no = pkg['invoice_no'] if pkg else ''

        cur.execute("""
            INSERT INTO sort_logs (package_id, invoice_no, sort_code, scan_method, sort_result, duration_ms)
            VALUES (%s, %s, %s, %s, 'SORT_DONE', %s)
        """, (pid, invoice_no, sort_code, data.get('scan_method', 'QR'), data.get('duration_ms', 0)))
        sort_log_id = cur.lastrowid

        cur.execute("""
            INSERT INTO shipping_logs (invoice_no, region, destination, status, slot_number)
            VALUES (%s, %s, %s, '출고대기', %s)
        """, (invoice_no, region, destination, data.get('slot_number', 0)))
        ship_log_id = cur.lastrowid

        conn.commit()
        return sort_log_id, ship_log_id, invoice_no
    finally:
        conn.close()


def _process_sort_fail(pid, sort_code, result, data):
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("UPDATE packages SET status='ERROR' WHERE id=%s", (pid,))
        cur.execute(
            "INSERT INTO error_logs (error_code, device_id, package_id) VALUES (%s,%s,%s)",
            (f'ROBOT_{result}', 'robot', pid)
        )

        cur.execute("SELECT invoice_no FROM packages WHERE id=%s", (pid,))
        pkg = cur.fetchone()
        invoice_no = pkg['invoice_no'] if pkg else ''

        cur.execute("""
            INSERT INTO sort_logs (package_id, invoice_no, sort_code, sort_result, duration_ms)
            VALUES (%s, %s, %s, %s, %s)
        """, (pid, invoice_no, sort_code, result, data.get('duration_ms', 0)))
        sort_log_id = cur.lastrowid

        conn.commit()
        return sort_log_id, invoice_no
    finally:
        conn.close()


def _now():
    from datetime import datetime
    return datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S")
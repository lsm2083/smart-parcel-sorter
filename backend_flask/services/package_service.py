from database.db import get_db
from mqtt.command_publish import (
    publish_actuator_forward, publish_actuator_backward,
    publish_conveyor_start, publish_vision_scan,
    publish_sort, publish_emergency_stop_all,
)
from sockets.wpf_events import (
    emit_package_detected, emit_package_scanned, emit_package_classified,
    emit_emergency_stop,
)

_current_package_id = None


def _set_status(package_id, status):
    conn = get_db()
    cur = conn.cursor()
    cur.execute("UPDATE packages SET status=%s, updated_at=NOW() WHERE id=%s",
                (status, package_id))
    conn.commit()
    conn.close()


def _get_destination(sort_code):
    conn = get_db()
    cur = conn.cursor()
    cur.execute("SELECT destination_name FROM sort_destinations WHERE sort_code=%s", (sort_code,))
    row = cur.fetchone()
    conn.close()
    return row['destination_name'] if row else sort_code


# ── 센서 이벤트

def handle_sensor_event(data):
    global _current_package_id
    event = data.get('event')

    if event == 'PACKAGE_DETECTED':
        conn = get_db()
        cur = conn.cursor()
        cur.execute("INSERT INTO packages (status) VALUES ('INPUT_DETECTED')")
        _current_package_id = cur.lastrowid
        conn.commit()
        conn.close()
        emit_package_detected(_current_package_id)
        publish_actuator_forward()

    elif event == 'SCAN_POSITION_ARRIVED':
        _set_status(_current_package_id, 'SCAN_POSITION')
        publish_vision_scan(_current_package_id)

    elif event == 'PHYSICAL_ESTOP':
        publish_emergency_stop_all()
        emit_emergency_stop(source='PHYSICAL_BUTTON')


# ── 명령 결과

def handle_command_result(data):
    cmd = data.get('command')
    status = data.get('status')

    if cmd == 'ACTUATOR_FORWARD' and status == 'DONE':
        _set_status(_current_package_id, 'publish_DONE')
        publish_actuator_backward()

    elif cmd == 'ACTUATOR_BACKWARD' and status == 'DONE':
        publish_conveyor_start()
        _set_status(_current_package_id, 'MOVING')


# ── 스캔 결과

def handle_scan_result(data):
    pid = data['package_id']
    sort_code = data['sort_code']
    scan_method = data.get('scan_method', 'QR')
    invoice_no = data.get('invoice_no', '')
    destination = _get_destination(sort_code)

    conn = get_db()
    cur = conn.cursor()
    cur.execute("""
        UPDATE packages
        SET invoice_no=%s, region=%s, package_type=%s, sort_code=%s,
            qr_raw=%s, status='CLASSIFIED', scanned_at=NOW()
        WHERE id=%s
    """, (invoice_no, data.get('region', ''), data.get('package_type', ''),
          sort_code, data.get('qr_raw', ''), pid))
    conn.commit()
    conn.close()

    emit_package_scanned(pid, invoice_no, sort_code, scan_method)
    emit_package_classified(pid, sort_code, destination)
    publish_sort(sort_code, pid)


# ── QR / OCR 실패

def handle_qr_fail(data):
    conn = get_db()
    cur = conn.cursor()
    cur.execute(
        "INSERT INTO error_logs (error_code, device_id, package_id, message) VALUES (%s,%s,%s,%s)",
        ('QR_READ_FAIL', 'vision_agent_01', data.get('package_id'), data.get('reason', ''))
    )
    conn.commit()
    conn.close()


def handle_ocr_fail(data):
    pid = data['package_id']
    _set_status(pid, 'DEFECT')

    conn = get_db()
    cur = conn.cursor()
    cur.execute(
        "INSERT INTO error_logs (error_code, device_id, package_id, message) VALUES (%s,%s,%s,%s)",
        ('OCR_READ_FAIL', 'vision_agent_01', pid, data.get('reason', ''))
    )
    conn.commit()
    conn.close()
    publish_sort('DEFECT', pid)


# ── 지게차 결과

def handle_forklift_result(data):
    from sockets.wpf_events import emit_forklift_status
    conn = get_db()
    cur = conn.cursor()
    cur.execute(
        "UPDATE forklift_jobs SET status=%s, completed_at=NOW(), duration_s=%s WHERE id=%s",
        (data['result'], data.get('duration_s'), data['job_id'])
    )
    conn.commit()
    conn.close()
    emit_forklift_status(data['result'], data['job_id'])
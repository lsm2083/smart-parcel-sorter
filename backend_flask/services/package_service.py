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
        # 액추에이터 없으므로 바로 MOVING 상태로
        _set_status(_current_package_id, 'MOVING')

    elif event == 'SCAN_POSITION_ARRIVED':
        if _current_package_id:
            conn = get_db()
            cur = conn.cursor()
            cur.execute("SELECT status FROM packages WHERE id=%s", (_current_package_id,))
            row = cur.fetchone()
            conn.close()
            if row and row['status'] not in ('SCANNING', 'CLASSIFIED', 'SORT_DONE'):
                _set_status(_current_package_id, 'SCAN_POSITION')
                publish_vision_scan(_current_package_id)
                print(f"[SCAN] START_SCAN 발행: package_id={_current_package_id}")

    elif event == 'PHYSICAL_ESTOP':
        publish_emergency_stop_all()
        emit_emergency_stop(source='PHYSICAL_BUTTON')

    elif event == 'ESTOP_RELEASED':
        from sockets.wpf_events import emit_emergency_reset
        emit_emergency_reset()


# ── 명령 결과

def handle_command_result(data):
    cmd = data.get('command')
    status = data.get('status')

    if cmd == 'ACTUATOR_FORWARD' and status == 'DONE':
        _set_status(_current_package_id, 'PUSH_DONE')
        publish_actuator_backward()

    elif cmd == 'ACTUATOR_BACKWARD' and status == 'DONE':
        publish_conveyor_start()
        _set_status(_current_package_id, 'MOVING')


# ── 스캔 결과

def handle_scan_result(data):
    pid = data.get('package_id')
    scan_method = data.get('scan_method', data.get('scan_type', 'QR'))
    invoice_no = data.get('invoice_no', '')
    region = data.get('region', '')
    package_type = data.get('package_type', 'BOX')
    sort_code = data.get('sort_code', f'{region}_{package_type}')
    try:
        destination = _get_destination(sort_code)
    except Exception:
        destination = sort_code

    conn = get_db()
    cur = conn.cursor()
    try:
        cur.execute("""
            UPDATE packages
            SET invoice_no=%s, region=%s, package_type=%s, sort_code=%s,
                qr_raw=%s, status='CLASSIFIED', scanned_at=NOW()
            WHERE id=%s
        """, (invoice_no, data.get('region', ''), data.get('package_type', ''),
              sort_code, data.get('qr_raw', ''), pid))

        # sort_logs에도 기록
        cur.execute("""
            INSERT INTO sort_logs (package_id, invoice_no, sort_code, scan_method, sort_result, completed_at)
            VALUES (%s, %s, %s, %s, 'SORT_DONE', NOW())
        """, (pid, invoice_no, sort_code, scan_method))

        conn.commit()
    except Exception as e:
        print(f"[DB] 저장 실패: {e}")
        cur.execute("""
            UPDATE packages
            SET invoice_no=%s, region=%s, package_type=%s,
                status='CLASSIFIED', scanned_at=NOW()
            WHERE id=%s
        """, (invoice_no, data.get('region', ''), data.get('package_type', ''), pid))
        conn.commit()
    conn.close()

    emit_package_scanned(pid, invoice_no, sort_code, scan_method)
    emit_package_classified(pid, sort_code, destination)
    publish_sort(sort_code, pid)

    from app import socketio
    socketio.emit('sorting_log_added', {
        'status': '정상',
        'errorType': '-',
        'package_id': pid,
        'recognitionType': scan_method,
        'trackingNumber': invoice_no,
        'region': data.get('region', ''),
        'confidence': 100
    })

    # 기존 코드 마지막에 추가
    from app import socketio
    socketio.emit('sorting_log_added', {
        'status': '정상',
        'errorType': '-',
        'package_id': pid,
        'recognitionType': scan_method,
        'trackingNumber': invoice_no,
        'region': data.get('region', ''),
        'confidence': 100
    })

# ── QR / OCR 실패

def handle_qr_fail(data):
    conn = get_db()
    cur = conn.cursor()
    cur.execute(
        "INSERT INTO error_logs (error_code, device_id, package_id, message) VALUES (%s,%s,%s,%s)",
        ('QR_READ_FAIL', 'vision', data.get('package_id'), data.get('reason', ''))
    )
    conn.commit()
    conn.close()
    # WPF 로그 전송
    from app import socketio
    socketio.emit('sorting_log_added', {
        'status': '불량',
        'errorType': 'QR인식실패',
        'package_id': data.get('package_id'),
        'recognitionType': 'QR',
        'trackingNumber': '',
        'region': '',
        'confidence': 0
    })


def handle_ocr_fail(data):
    pid = data['package_id']
    _set_status(pid, 'DEFECT')

    conn = get_db()
    cur = conn.cursor()
    cur.execute(
        "INSERT INTO error_logs (error_code, device_id, package_id, message) VALUES (%s,%s,%s,%s)",
        ('OCR_READ_FAIL', 'vision', pid, data.get('reason', ''))
    )
    conn.commit()
    conn.close()
    publish_sort('DEFECT', pid)
    # WPF 로그 전송
    from app import socketio
    socketio.emit('sorting_log_added', {
        'status': '불량',
        'errorType': 'OCR인식실패',
        'package_id': pid,
        'recognitionType': 'OCR',
        'trackingNumber': '',
        'region': '',
        'confidence': data.get('confidence', 0)
    })


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
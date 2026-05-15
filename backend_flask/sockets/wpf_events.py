from app import socketio
from datetime import datetime


def _now():
    return datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S")


# ── 물품 흐름 이벤트

def emit_package_detected(package_id):
    socketio.emit('package_detected', {
        'package_id': package_id, 'detected_at': _now()
    })

def emit_package_scanned(package_id, invoice_no, sort_code, scan_method):
    socketio.emit('package_scanned', {
        'package_id': package_id, 'invoice_no': invoice_no,
        'sort_code': sort_code, 'scan_method': scan_method
    })

def emit_package_classified(package_id, sort_code, destination):
    socketio.emit('package_classified', {
        'package_id': package_id, 'sort_code': sort_code,
        'destination': destination
    })


# ── WPF 실시간 로그 추가

def emit_sorting_log_added(log: dict):
    socketio.emit('sorting_log_added', log)

def emit_shipping_log_added(log: dict):
    socketio.emit('shipping_log_added', log)

def emit_blackbox_event_added(event: dict):
    socketio.emit('blackbox_event_added', event)


# ── 장비 상태

def emit_device_status(data: dict):
    socketio.emit('device_status', data)

def emit_conveyor_status(motor, actuator, speed=0):
    socketio.emit('conveyor_status_changed', {
        'motor': motor, 'actuator': actuator, 'speed': speed
    })

def emit_robot_status(status, package_id=None):
    socketio.emit('robot_status_changed', {
        'status': status, 'package_id': package_id
    })

def emit_sort_completed(package_id, sort_code):
    socketio.emit('sort_completed', {
        'package_id': package_id, 'sort_code': sort_code,
        'completed_at': _now()
    })

def emit_forklift_status(status, job_id=None):
    socketio.emit('forklift_status_changed', {
        'status': status, 'job_id': job_id
    })


# ── 장비 연결/해제

def emit_device_connected(device_id, device_type):
    socketio.emit('device_connected', {
        'device_id': device_id, 'device_type': device_type
    })

def emit_device_disconnected(device_id):
    socketio.emit('device_disconnected', {
        'device_id': device_id, 'disconnected_at': _now()
    })


# ── 비상정지

def emit_emergency_stop(source='UNKNOWN'):
    socketio.emit('emergency_stop', {
        'source': source, 'triggered_at': _now(),
        'isEmergency': True,
    })

def emit_emergency_reset():
    socketio.emit('emergency_stop', {
        'reset_at': _now(), 'isEmergency': False,
    })
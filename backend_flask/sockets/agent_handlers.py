from flask import request
from flask_socketio import join_room
from app import socketio

_sid_map = {}


@socketio.on('connect')
def on_connect():
    device_id = request.args.get('device_id')
    device_type = request.args.get('device_type')
    if not device_id:
        return

    _sid_map[request.sid] = device_id
    join_room(device_id)

    from services.device_service import set_online
    set_online(device_id, device_type or 'UNKNOWN', request.remote_addr)

    from sockets.wpf_events import emit_device_connected
    emit_device_connected(device_id, device_type or 'UNKNOWN')
    print(f"[연결] {device_id}")


@socketio.on('disconnect')
def on_disconnect():
    device_id = _sid_map.pop(request.sid, None)
    if not device_id:
        return

    from services.device_service import set_disconnected
    set_disconnected(device_id)

    from sockets.wpf_events import emit_device_disconnected
    emit_device_disconnected(device_id)
    print(f"[끊김] {device_id}")


@socketio.on('command_result')
def on_command_result(data):
    from services.package_service import handle_command_result
    handle_command_result(data)


@socketio.on('sensor_event')
def on_sensor_event(data):
    from services.package_service import handle_sensor_event
    handle_sensor_event(data)


@socketio.on('scan_result')
def on_scan_result(data):
    from services.package_service import handle_scan_result
    handle_scan_result(data)


@socketio.on('qr_fail')
def on_qr_fail(data):
    from services.package_service import handle_qr_fail
    handle_qr_fail(data)


@socketio.on('ocr_fail')
def on_ocr_fail(data):
    from services.package_service import handle_ocr_fail
    handle_ocr_fail(data)


@socketio.on('sort_result')
def on_sort_result(data):
    from services.sort_service import handle_sort_result
    handle_sort_result(data)


@socketio.on('blackbox_snapshot')
def on_blackbox_snapshot(data):
    from services.blackbox_service import handle_snapshot
    handle_snapshot(data)


@socketio.on('forklift_result')
def on_forklift_result(data):
    from services.package_service import handle_forklift_result
    handle_forklift_result(data)
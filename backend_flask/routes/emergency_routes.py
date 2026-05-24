from flask import Blueprint, request, jsonify
from sockets.wpf_events import emit_emergency_stop, emit_emergency_reset

emergency_bp = Blueprint('emergency', __name__)


@emergency_bp.route('/emergency/stop', methods=['POST'])
def emergency_stop():
    from mqtt.command_publish import publish_emergency_stop_all
    from routes.status_routes import update_conveyor_realtime, update_emergency
    publish_emergency_stop_all()
    update_conveyor_realtime(0, '비상정지')
    update_emergency(True)
    source = 'WPF'
    if request.json:
        source = request.json.get('source', 'WPF')
    emit_emergency_stop(source=source)
    return jsonify({'result': 'ok', 'system_status': 'EMERGENCY_STOP'})


@emergency_bp.route('/emergency/reset', methods=['POST'])
def emergency_reset():
    from mqtt.command_publish import publish_robot_home
    from routes.status_routes import update_conveyor_realtime, update_emergency
    publish_robot_home()
    update_conveyor_realtime(0, '정지중')
    update_emergency(False)
    emit_emergency_reset()
    return jsonify({'result': 'ok', 'system_status': 'STOPPED'})


@emergency_bp.route('/system/start', methods=['POST'])
def system_start():
    from mqtt.command_publish import publish_conveyor_start
    from routes.status_routes import update_conveyor_realtime, update_emergency
    publish_conveyor_start()
    update_conveyor_realtime(180, '작동중')
    update_emergency(False)
    return jsonify({'result': 'ok', 'system_status': 'RUNNING'})


@emergency_bp.route('/system/stop', methods=['POST'])
def system_stop():
    from mqtt.command_publish import publish_conveyor_stop
    from routes.status_routes import update_conveyor_realtime
    publish_conveyor_stop()
    update_conveyor_realtime(0, '정지중')
    return jsonify({'result': 'ok', 'system_status': 'STOPPED'})


@emergency_bp.route('/conveyor/command', methods=['POST'])
def conveyor_command():
    data = request.get_json() or {}
    command = data.get('command')
    speed = data.get('speed', 180)

    from routes.status_routes import update_conveyor_realtime, update_emergency

    if command == 'CONVEYOR_START':
        from mqtt.command_publish import publish_conveyor_start
        publish_conveyor_start(speed)
        update_conveyor_realtime(speed, '작동중')
        update_emergency(False)
    elif command == 'CONVEYOR_STOP':
        from mqtt.command_publish import publish_conveyor_stop
        publish_conveyor_stop()
        update_conveyor_realtime(0, '정지중')
    elif command == 'EMERGENCY_STOP':
        from mqtt.command_publish import publish_emergency_stop_all
        publish_emergency_stop_all()
        update_conveyor_realtime(0, '비상정지')
        update_emergency(True)

    return jsonify({'status': 'ok', 'command': command})
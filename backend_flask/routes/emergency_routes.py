from flask import Blueprint, request, jsonify
from mqtt.command_publish import publish_emergency_stop_all, publish_robot_home, publish_conveyor_start, publish_conveyor_stop
from sockets.wpf_events import emit_emergency_stop, emit_emergency_reset

emergency_bp = Blueprint('emergency', __name__)


@emergency_bp.route('/emergency/stop', methods=['POST'])
def emergency_stop():
    publish_emergency_stop_all()
    source = 'WPF'
    if request.json:
        source = request.json.get('source', 'WPF')
    emit_emergency_stop(source=source)
    return jsonify({'result': 'ok', 'system_status': 'EMERGENCY_STOP'})


@emergency_bp.route('/emergency/reset', methods=['POST'])
def emergency_reset():
    publish_robot_home()
    emit_emergency_reset()
    return jsonify({'result': 'ok', 'system_status': 'STOPPED'})


@emergency_bp.route('/system/start', methods=['POST'])
def system_start():
    from sockets.command_push import publish_conveyor_start
    publish_conveyor_start()
    return jsonify({'result': 'ok', 'system_status': 'RUNNING'})


@emergency_bp.route('/system/stop', methods=['POST'])
def system_stop():
    from sockets.command_push import publish_conveyor_stop
    publish_conveyor_stop()
    return jsonify({'result': 'ok', 'system_status': 'STOPPED'})
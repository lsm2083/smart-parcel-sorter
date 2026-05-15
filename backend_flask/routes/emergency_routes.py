from flask import Blueprint, request, jsonify
from sockets.command_push import push_emergency_stop_all, push_robot_home
from sockets.wpf_events import emit_emergency_stop, emit_emergency_reset

emergency_bp = Blueprint('emergency', __name__)


@emergency_bp.route('/emergency/stop', methods=['POST'])
def emergency_stop():
    push_emergency_stop_all()
    source = 'WPF'
    if request.json:
        source = request.json.get('source', 'WPF')
    emit_emergency_stop(source=source)
    return jsonify({'result': 'ok', 'system_status': 'EMERGENCY_STOP'})


@emergency_bp.route('/emergency/reset', methods=['POST'])
def emergency_reset():
    push_robot_home()
    emit_emergency_reset()
    return jsonify({'result': 'ok', 'system_status': 'STOPPED'})


@emergency_bp.route('/system/start', methods=['POST'])
def system_start():
    from sockets.command_push import push_conveyor_start
    push_conveyor_start()
    return jsonify({'result': 'ok', 'system_status': 'RUNNING'})


@emergency_bp.route('/system/stop', methods=['POST'])
def system_stop():
    from sockets.command_push import push_conveyor_stop
    push_conveyor_stop()
    return jsonify({'result': 'ok', 'system_status': 'STOPPED'})
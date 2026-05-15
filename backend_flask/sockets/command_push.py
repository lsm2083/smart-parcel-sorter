import json
from app import socketio
from database.db import get_db


def push_command(device_id, command, **kwargs):
    payload = {"command": command, **kwargs}

    conn = get_db()
    cur = conn.cursor()
    cur.execute(
        "INSERT INTO command_logs (device_id, command, payload, status) VALUES (%s,%s,%s,%s)",
        (device_id, command, json.dumps(payload, ensure_ascii=False), 'SENT')
    )
    conn.commit()
    conn.close()

    socketio.emit('command', payload, room=device_id)


# ── 단축 함수

def push_actuator_forward(timeout=6000):
    push_command('conveyor_agent_01', 'ACTUATOR_FORWARD', timeout=timeout)

def push_actuator_backward(timeout=6000):
    push_command('conveyor_agent_01', 'ACTUATOR_BACKWARD', timeout=timeout)

def push_conveyor_start(speed=180):
    push_command('conveyor_agent_01', 'CONVEYOR_START', speed=speed)

def push_conveyor_stop():
    push_command('conveyor_agent_01', 'CONVEYOR_STOP')

def push_vision_scan(package_id):
    push_command('vision_agent_01', 'START_SCAN', package_id=package_id)

def push_sort(sort_code, package_id):
    push_command('robot_agent_01', 'SORT', sort_code=sort_code, package_id=package_id)

def push_robot_home():
    push_command('robot_agent_01', 'HOME')

def push_blackbox_snapshot(reason):
    push_command('blackbox_agent_01', 'SAVE_SNAPSHOT', reason=reason)

def push_forklift_move(device_id, from_pos, to_pos):
    push_command(device_id, 'MOVE_PALLET', **{"from": from_pos, "to": to_pos})

def push_emergency_stop_all():
    for did in ['conveyor_agent_01', 'robot_agent_01', 'vision_agent_01',
                'blackbox_agent_01', 'forklift_box_01', 'forklift_vinyl_01']:
        push_command(did, 'EMERGENCY_STOP')
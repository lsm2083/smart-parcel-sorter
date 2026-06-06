import json
import paho.mqtt.client as paho_mqtt
from database.db import get_db
from mqtt.topics import *

_publisher = None


def get_publisher():
    global _publisher
    if _publisher is None:
        _publisher = paho_mqtt.Client(client_id="flask_publisher")
        _publisher.connect('127.0.0.1', 1883)
        _publisher.loop_start()
    return _publisher


def publish_command(topic, command, **kwargs):
    payload = {"command": command, **kwargs}
    device_id = topic.split('/')[1]
    print(f"[CMD] 발행: {topic} → {command}")

    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute(
            "INSERT INTO command_logs (device_id, command, payload, status) VALUES (%s,%s,%s,%s)",
            (device_id, command, json.dumps(payload, ensure_ascii=False), 'SENT')
        )
        conn.commit()
    finally:
        conn.close()

    get_publisher().publish(topic, json.dumps(payload, ensure_ascii=False))


def publish_actuator_forward(timeout=6000):
    publish_command(CONVEYOR_COMMAND, 'ACTUATOR_FORWARD', timeout=timeout)

def publish_actuator_backward(timeout=6000):
    publish_command(CONVEYOR_COMMAND, 'ACTUATOR_BACKWARD', timeout=timeout)

def publish_conveyor_start(speed=180):
    publish_command(CONVEYOR_COMMAND, 'CONVEYOR_START', speed=speed)

def publish_conveyor_stop():
    publish_command(CONVEYOR_COMMAND, 'CONVEYOR_STOP')

def publish_vision_scan(package_id):
    publish_command(VISION_COMMAND, 'START_SCAN', package_id=package_id)

def publish_sort(sort_code, package_id, box=None):
    publish_command(ROBOT_COMMAND, 'SORT',
                    sort_code=sort_code, box=box, package_id=package_id)

def publish_robot_home():
    publish_command(ROBOT_COMMAND, 'HOME')

def publish_blackbox_snapshot(reason):
    publish_command(BLACKBOX_COMMAND, 'SAVE_SNAPSHOT', reason=reason)

def publish_forklift_move(from_pos, to_pos):
    publish_command(FORKLIFT_COMMAND, 'MOVE_PALLET',
                    **{"from": from_pos, "to": to_pos})

def publish_emergency_stop_all():
    get_publisher().publish(
        SYSTEM_EMERGENCY,
        json.dumps({"command": "EMERGENCY_STOP"})
    )
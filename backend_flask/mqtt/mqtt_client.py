import json
import eventlet
from mqtt.topics import *


def register_mqtt_handlers(mqtt_client, app):

    @mqtt_client.on_connect()
    def on_broker_connect(client, userdata, flags, rc):
        print(f"[MQTT] 브로커 연결됨 (rc={rc})")
        mqtt_client.subscribe(CONVEYOR_SENSOR)
        mqtt_client.subscribe(CONVEYOR_RESULT)
        mqtt_client.subscribe(CONVEYOR_STATUS)
        mqtt_client.subscribe(VISION_SCAN_RESULT)
        mqtt_client.subscribe(VISION_FAIL)
        mqtt_client.subscribe(ROBOT_RESULT)
        mqtt_client.subscribe(ROBOT_STATUS)
        mqtt_client.subscribe(BLACKBOX_EVENT)
        mqtt_client.subscribe(FORKLIFT_RESULT)
        mqtt_client.subscribe(FORKLIFT_STATUS)
        mqtt_client.subscribe(DEVICE_STATUS)
        mqtt_client.subscribe('parcel/blackbox/frame/blackbox_field')
        mqtt_client.subscribe('parcel/blackbox/frame/blackbox_shipping')
        print("[MQTT] 모든 Topic 구독 완료")

    @mqtt_client.on_message()
    def on_message(client, userdata, message):
        topic = message.topic
        payload = message.payload.decode()
        eventlet.spawn(_process_message, app, topic, payload)


def _process_message(app, topic, payload):
    if not topic.startswith('parcel/blackbox/frame/'):
        print(f"[MQTT] 수신: {topic}")

    try:
        data = json.loads(payload)
    except Exception:
        return

    try:
        with app.app_context():
            if topic == CONVEYOR_SENSOR:
                print(f"[SENSOR] event={data.get('event')}")
                from services.package_service import handle_sensor_event
                handle_sensor_event(data)
                from app import socketio
                event = data.get('event', '')
                if event == 'PHYSICAL_ESTOP':
                    socketio.emit('physical_estop', {})
                elif event == 'ESTOP_RELEASED':
                    socketio.emit('estop_released', {})

            elif topic == CONVEYOR_RESULT:
                from services.package_service import handle_command_result
                handle_command_result(data)

            elif topic == CONVEYOR_STATUS:
                if data.get('status') in ('ONLINE', 'DISCONNECTED'):
                    # 장비 연결 상태 처리
                    device_id = data.get('device_id', 'conveyor')
                    if data.get('status') == 'ONLINE':
                        from routes.status_routes import update_conveyor_connected
                        update_conveyor_connected()
                        from services.device_service import set_online
                        set_online(device_id, data.get('device_type', 'CONVEYOR'),
                                   data.get('ip_address', ''), data.get('stream_url'))
                        from sockets.wpf_events import emit_device_connected
                        emit_device_connected(device_id, data.get('device_type', 'CONVEYOR'))
                    else:
                        from services.device_service import set_disconnected
                        set_disconnected(device_id)
                        from sockets.wpf_events import emit_device_disconnected
                        emit_device_disconnected(device_id)
                else:
                    from routes.status_routes import update_conveyor_realtime
                    update_conveyor_realtime(data.get('speed', 0), data.get('status', '대기'))
                    # print(f"[STATUS] speed={data.get('speed')}, motor={data.get('motor')}, status={data.get('status')}")
                    from sockets.wpf_events import emit_conveyor_status
                    emit_conveyor_status(data.get('motor'), data.get('actuator'), data.get('speed', 0))
                    from app import socketio
                    # print(f"[WPF전송] device_status emit: speed={data.get('speed', 0)}")
                    socketio.emit('device_status', {
                        'conveyorSpeed': data.get('speed', 0),
                        'conveyorStatus': data.get('status', '대기')
                    })

            elif topic == VISION_SCAN_RESULT:
                from services.package_service import handle_scan_result
                handle_scan_result(data)

            elif topic == VISION_FAIL:
                ftype = data.get('type')
                if ftype == 'QR_OCR_MISMATCH':
                    from services.package_service import handle_mismatch
                    handle_mismatch(data)
                elif ftype == 'OCR_FAIL':
                    from services.package_service import handle_scan_fail
                    handle_scan_fail(data)
                elif ftype == 'SCAN_FAILED':
                    from services.package_service import handle_scan_failed_event
                    handle_scan_failed_event(data)

            elif topic == ROBOT_RESULT:
                from services.sort_service import handle_sort_result
                handle_sort_result(data)

            elif topic == ROBOT_STATUS:
                from sockets.wpf_events import emit_robot_status
                emit_robot_status(data.get('status'), data.get('package_id'))

            elif topic == BLACKBOX_EVENT:
                from services.blackbox_service import handle_snapshot
                handle_snapshot(data)

            elif topic == FORKLIFT_RESULT:
                from services.package_service import handle_forklift_result
                handle_forklift_result(data)

            elif topic.startswith('parcel/blackbox/frame/'):
                camera_id = topic.split('/')[-1]
                from app import socketio
                socketio.emit('blackbox_frame_' + camera_id, data)

            elif '/status' in topic:
                status = data.get('status')
                device_id = data.get('device_id', topic.split('/')[1])
                if status == 'DISCONNECTED':
                    from services.device_service import set_disconnected
                    set_disconnected(device_id)
                    from sockets.wpf_events import emit_device_disconnected
                    emit_device_disconnected(device_id)
                elif status == 'ONLINE':
                    from services.device_service import set_online
                    set_online(device_id, data.get('device_type', ''),
                               data.get('ip_address', ''), data.get('stream_url'))
                    from sockets.wpf_events import emit_device_connected
                    emit_device_connected(device_id, data.get('device_type', ''))

    except Exception as e:
        import traceback
        print(f"[MQTT] 처리 오류: {e}")
        traceback.print_exc()
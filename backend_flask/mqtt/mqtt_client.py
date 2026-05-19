import json
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
        print("[MQTT] 모든 Topic 구독 완료")

    @mqtt_client.on_message()
    def on_message(client, userdata, message):
        topic = message.topic
        print(f"[MQTT] 수신: {topic}")
        try:
            data = json.loads(message.payload.decode())
        except Exception:
            return

        try:
            with app.app_context():
                if topic == CONVEYOR_SENSOR:
                    from services.package_service import handle_sensor_event
                    handle_sensor_event(data)

                elif topic == CONVEYOR_RESULT:
                    from services.package_service import handle_command_result
                    handle_command_result(data)

                elif topic == CONVEYOR_STATUS:
                    from sockets.wpf_events import emit_conveyor_status
                    emit_conveyor_status(data.get('motor'), data.get('actuator'), data.get('speed', 0))

                elif topic == VISION_SCAN_RESULT:
                    from services.package_service import handle_scan_result
                    handle_scan_result(data)

                elif topic == VISION_FAIL:
                    if data.get('type') == 'QR_FAIL':
                        from services.package_service import handle_qr_fail
                        handle_qr_fail(data)
                    elif data.get('type') == 'OCR_FAIL':
                        from services.package_service import handle_ocr_fail
                        handle_ocr_fail(data)

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
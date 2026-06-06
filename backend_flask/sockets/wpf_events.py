"""
wpf_events.py — Flask → WPF WebSocket 이벤트 발행 헬퍼

중요 변경 (v6.0):
- MQTT 콜백 스레드에서 emit 하면 eventlet이 못 잡아서 클라이언트로 안 나가는 문제.
- emit 을 socketio.start_background_task 로 감싸서 eventlet 컨텍스트에서 실행.
- HTTP 핸들러(eventlet 워커)에서 호출하든 MQTT 콜백(별도 스레드)에서 호출하든
  둘 다 정상 전송됨.

진단 로그 추가:
- [SAFE_EMIT] 요청: {event}        → _safe_emit 진입
- [SAFE_EMIT] 백그라운드 태스크 등록: {event}
- [SAFE_EMIT] 실제 전송 완료: {event}  → start_background_task 안에서 emit 실제 실행됨
- [SAFE_EMIT] 전송 실패: {event} {error}
"""

from app import socketio
from datetime import datetime


def _now():
    return datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S")


def _safe_emit(event_name, payload):
    """
    어느 스레드에서 호출되든 안전하게 emit.
    MQTT 콜백 등 eventlet 외부 스레드에서 호출돼도 start_background_task 로
    eventlet 그린스레드에 던져서 실제 전송을 보장한다.

    람다 늦은 바인딩 방지: 디폴트 인자로 event_name/payload 고정.
    """
    print(f"[SAFE_EMIT] 요청: {event_name}")

    def _do(en=event_name, pl=payload):
        try:
            socketio.emit(en, pl)
            print(f"[SAFE_EMIT] 실제 전송 완료: {en}")
        except Exception as e:
            print(f"[SAFE_EMIT] 전송 실패: {en} {e}")

    try:
        socketio.start_background_task(_do)
        print(f"[SAFE_EMIT] 백그라운드 태스크 등록: {event_name}")
    except Exception as e:
        print(f"[SAFE_EMIT] 백그라운드 태스크 등록 실패: {event_name} {e}")
        # 백그라운드 태스크 등록 자체가 실패하면 직접 emit 시도 (폴백)
        try:
            socketio.emit(event_name, payload)
            print(f"[SAFE_EMIT] 폴백 직접 전송 완료: {event_name}")
        except Exception as e2:
            print(f"[SAFE_EMIT] 폴백 직접 전송도 실패: {event_name} {e2}")


# ── 물품 흐름 이벤트

def emit_package_detected(package_id):
    _safe_emit('package_detected', {
        'package_id': package_id, 'detected_at': _now()
    })

def emit_package_scanned(package_id, invoice_no, sort_code, scan_method):
    _safe_emit('package_scanned', {
        'package_id': package_id, 'invoice_no': invoice_no,
        'sort_code': sort_code, 'scan_method': scan_method
    })

def emit_package_classified(package_id, sort_code, destination):
    _safe_emit('package_classified', {
        'package_id': package_id, 'sort_code': sort_code,
        'destination': destination
    })


# ── WPF 실시간 로그 추가

def emit_sorting_log_added(log: dict):
    _safe_emit('sorting_log_added', log)

def emit_shipping_log_added(log: dict):
    _safe_emit('shipping_log_added', log)

def emit_blackbox_event_added(event: dict):
    _safe_emit('blackbox_event_added', event)


# ── 장비 상태

def emit_device_status(data: dict):
    _safe_emit('device_status', data)

def emit_conveyor_status(motor, actuator, speed=0):
    _safe_emit('conveyor_status_changed', {
        'motor': motor, 'actuator': actuator, 'speed': speed
    })
    # WPF device_status 형식으로도 발행
    status_label = '작동중' if motor == 'ON' else '정지'
    _safe_emit('device_status', {
        'conveyorSpeed': speed,
        'conveyorStatus': status_label
    })

def emit_robot_status(status, package_id=None):
    _safe_emit('robot_status_changed', {
        'status': status, 'package_id': package_id
    })

def emit_sort_completed(package_id, sort_code):
    _safe_emit('sort_completed', {
        'package_id': package_id, 'sort_code': sort_code,
        'completed_at': _now()
    })

def emit_forklift_status(status, job_id=None):
    _safe_emit('forklift_status_changed', {
        'status': status, 'job_id': job_id
    })


# ── 장비 연결/해제

def emit_device_connected(device_id, device_type):
    _safe_emit('device_connected', {
        'device_id': device_id, 'device_type': device_type
    })

def emit_device_disconnected(device_id):
    _safe_emit('device_disconnected', {
        'device_id': device_id, 'disconnected_at': _now()
    })


# ── 비상정지

def emit_emergency_stop(source='UNKNOWN'):
    _safe_emit('emergency_stop', {
        'source': source, 'triggered_at': _now(),
        'isEmergency': True,
    })

def emit_emergency_reset():
    _safe_emit('emergency_reset', {'reset_at': _now()})        # WPF용 (기존 유지)
    _safe_emit('emergency_stop',  {'isEmergency': False})      # 모바일 앱용
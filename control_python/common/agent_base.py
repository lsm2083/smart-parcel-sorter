import sys
import os
import json
import paho.mqtt.client as mqtt

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.abspath(os.path.join(BASE_DIR, ".."))
COMMON_DIR = os.path.join(PROJECT_DIR, "common")

if COMMON_DIR not in sys.path:
    sys.path.insert(0, COMMON_DIR)

from topics import SYSTEM_EMERGENCY


class AgentBase:
    """
    공통 Agent 베이스 클래스.

    사용법:
    1. 이 클래스를 상속한다.
    2. handle_command(data)를 구현한다.
    3. connect()를 호출한다.

    예시:
        class ConveyorAgent(AgentBase):
            def handle_command(self, data):
                cmd = data['command']
                if cmd == 'CONVEYOR_START':
                    # 모터 제어 로직
                    self.publish_result(cmd, 'DONE')

        agent = ConveyorAgent('192.168.0.100', 'conveyor_agent_01', 'CONVEYOR',
                              'parcel/conveyor/command', 'parcel/conveyor/result')
        agent.connect()
    """

    def __init__(self, broker_host, device_id, device_type, command_topic, result_topic):
        self.broker_host   = broker_host
        self.device_id     = device_id
        self.device_type   = device_type
        self.command_topic = command_topic
        self.result_topic  = result_topic

        self.client = mqtt.Client(client_id=device_id)
        self.client.on_connect    = self._on_connect
        self.client.on_disconnect = self._on_disconnect
        self.client.on_message    = self._on_message

        # LWT: 비정상 종료 시 브로커가 자동으로 DISCONNECTED 발행
        self.client.will_set(
            topic=f"parcel/{device_id}/status",
            payload=json.dumps({"status": "DISCONNECTED", "device_id": device_id}),
            retain=True
        )

    def _on_connect(self, client, userdata, flags, rc):
        print(f"[{self.device_id}] 브로커 연결됨")
        client.subscribe(self.command_topic)
        client.subscribe(SYSTEM_EMERGENCY)
        # 정상 연결 상태 발행
        client.publish(
            f"parcel/{self.device_id}/status",
            json.dumps({
                "status": "ONLINE",
                "device_id": self.device_id,
                "device_type": self.device_type
            }),
            retain=True
        )

    def _on_disconnect(self, client, userdata, rc):
        print(f"[{self.device_id}] 브로커 연결 끊김 (rc={rc})")

    def _on_message(self, client, userdata, message):
        try:
            data = json.loads(message.payload.decode())
        except Exception:
            return
        cmd = data.get('command', '')
        print(f"[{self.device_id}] 명령 수신: {cmd}")

        if cmd == 'EMERGENCY_STOP':
            print(f"[{self.device_id}] 비상정지!")
            self.handle_emergency()
            return

        try:
            self.handle_command(data)
        except Exception as e:
            print(f"[{self.device_id}] 오류: {e}")
            self.publish_result(cmd, 'FAIL', message=str(e))

    # ── 자식 클래스에서 구현 ─────────────────────────

    def handle_command(self, data):
        """명령 처리. 자식 클래스에서 반드시 구현."""
        raise NotImplementedError("handle_command를 구현하세요")

    def handle_emergency(self):
        """비상정지 처리. 필요시 오버라이드."""
        print(f"[{self.device_id}] 기본 비상정지 — 대기")

    # ── 이벤트/결과 발행 ─────────────────────────────

    def publish_event(self, topic, data):
        """Flask에 이벤트 발행."""
        self.client.publish(topic, json.dumps(data, ensure_ascii=False))

    def publish_result(self, command, status, **kwargs):
        """명령 실행 결과 발행."""
        self.client.publish(
            self.result_topic,
            json.dumps({"command": command, "status": status, **kwargs}, ensure_ascii=False)
        )

    # ── 연결 ─────────────────────────────────────────

    def connect(self):
        """브로커 연결 + 이벤트 루프 시작. 블로킹 함수."""
        print(f"[{self.device_id}] {self.broker_host}에 연결 중...")
        self.client.connect(self.broker_host, 1883, keepalive=60)
        self.client.loop_forever()
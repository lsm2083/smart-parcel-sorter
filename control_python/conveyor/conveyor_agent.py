import sys
import threading
sys.path.append('../common')

from agent_base import AgentBase
from topics import CONVEYOR_COMMAND, CONVEYOR_RESULT, CONVEYOR_SENSOR, CONVEYOR_STATUS
import serial

class ConveyorAgent(AgentBase):
    def __init__(self, broker_host):
        super().__init__(broker_host, 'conveyor', 'CONVEYOR',
                         CONVEYOR_COMMAND, CONVEYOR_RESULT)
        self.arduino = serial.Serial('COM3', 9600, timeout=1)
        print("[CONVEYOR] Arduino 연결됨 (COM3)")

        # Arduino 이벤트 수신 스레드 시작
        threading.Thread(target=self.listen_arduino, daemon=True).start()

    def handle_command(self, data):
        cmd = data['command']

        if cmd == 'CONVEYOR_START':
            self.arduino.write(b"CONVEYOR_START\n")
            self.publish_result(cmd, 'DONE')
            self.publish_event(CONVEYOR_STATUS, {
                "motor": "ON",
                "actuator": "STOP",
                "speed": 180,
                "status": "작동중"
            })

        # elif cmd == 'CONVEYOR_STOP':
        #     print("[TEST] Arduino에 STOP 전송")
        #     self.arduino.write(b"CONVEYOR_STOP\n")
        #     print("[TEST] Arduino 전송 완료")
        #     self.publish_result(cmd, 'DONE')
        #     self.publish_event(CONVEYOR_STATUS, {
        #         "motor": "OFF",
        #         "actuator": "STOP",
        #         "speed": 0,
        #         "status": "정지"
        #     })

        elif cmd == 'CONVEYOR_STOP':
            cmd_str = b"CONVEYOR_STOP\n"
            print(f"[SEND] {repr(cmd_str)}")
            self.arduino.write(cmd_str)
            print("[TEST] Arduino 전송 완료")

        elif cmd == 'EMERGENCY_STOP':
            self.arduino.write(b"EMERGENCY_STOP\n")
            self.publish_result(cmd, 'DONE')
            self.publish_event(CONVEYOR_STATUS, {
                "motor": "OFF",
                "actuator": "STOP",
                "speed": 0,
                "status": "비상정지"
            })

    def handle_emergency(self):
        self.arduino.write(b"EMERGENCY_STOP\n")
        print("[CONVEYOR] 비상정지 — 모터 즉시 정지")

    def listen_arduino(self):
        """Arduino에서 오는 이벤트 수신"""
        while True:
            try:
                line = self.arduino.readline().decode('utf-8', errors='ignore').strip()
                if not line:
                    continue

                print(f"[RAW] '{line}'")
                print(f"[ARDUINO] {line}")

                if line == "EVENT:PHYSICAL_ESTOP":
                    print("[TEST] ESTOP 발행 시도")
                    self.publish_event(CONVEYOR_SENSOR, {"event": "PHYSICAL_ESTOP"})
                elif line == "EVENT:ESTOP_RELEASED":
                    print("[TEST] ESTOP 해제 발행")
                    self.publish_event(CONVEYOR_SENSOR, {"event": "ESTOP_RELEASED"})
                elif line == "EVENT:PACKAGE_DETECTED":
                    self.publish_event(CONVEYOR_SENSOR, {"event": "PACKAGE_DETECTED"})
                elif line == "EVENT:SCAN_POSITION_ARRIVED":
                    self.publish_event(CONVEYOR_SENSOR, {"event": "SCAN_POSITION_ARRIVED"})
                elif line.startswith("STATUS:speed="):
                    speed_val = line.split("=")[1]
                    print(f"[TEST] 속도 발행: {speed_val}")
                    self.publish_event(CONVEYOR_STATUS, {
                        "motor": "ON",
                        "actuator": "STOP",
                        "speed": int(speed_val),
                        "status": "작동중"
                    })

            except Exception as e:
                print(f"[CONVEYOR] Arduino 수신 오류: {e}")
                break

if __name__ == '__main__':
    agent = ConveyorAgent('192.168.0.21')
    agent.connect()
import sys
import threading
sys.path.append('../common')

from agent_base import AgentBase
from topics import CONVEYOR_COMMAND, CONVEYOR_RESULT, CONVEYOR_SENSOR
import serial

class ConveyorAgent(AgentBase):
    def __init__(self, broker_host):
        super().__init__(broker_host, 'conveyor_agent_01', 'CONVEYOR',
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

        elif cmd == 'CONVEYOR_STOP':
            self.arduino.write(b"CONVEYOR_STOP\n")
            self.publish_result(cmd, 'DONE')

        elif cmd == 'EMERGENCY_STOP':
            self.arduino.write(b"EMERGENCY_STOP\n")
            self.publish_result(cmd, 'DONE')

    def handle_emergency(self):
        self.arduino.write(b"EMERGENCY_STOP\n")
        print("[CONVEYOR] 비상정지 — 모터 즉시 정지")

    def listen_arduino(self):
        """Arduino에서 오는 이벤트 수신"""
        while True:
            try:
                line = self.arduino.readline().decode().strip()
                if not line:
                    continue

                print(f"[ARDUINO] {line}")

                if line == "EVENT:PHYSICAL_ESTOP":
                    self.publish_event(CONVEYOR_SENSOR, {"event": "PHYSICAL_ESTOP"})
                elif line == "EVENT:PACKAGE_DETECTED":
                    self.publish_event(CONVEYOR_SENSOR, {"event": "PACKAGE_DETECTED"})
                elif line == "EVENT:SCAN_POSITION_ARRIVED":
                    self.publish_event(CONVEYOR_SENSOR, {"event": "SCAN_POSITION_ARRIVED"})

            except Exception as e:
                print(f"[CONVEYOR] Arduino 수신 오류: {e}")
                break

if __name__ == '__main__':
    agent = ConveyorAgent('192.168.0.24')
    agent.connect()
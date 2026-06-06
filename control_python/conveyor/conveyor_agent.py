import sys, os
sys.path.append(os.path.join(os.path.dirname(__file__), '..', 'common'))
import threading

from agent_base import AgentBase
from topics import CONVEYOR_COMMAND, CONVEYOR_RESULT, CONVEYOR_SENSOR, CONVEYOR_STATUS
import serial

class ConveyorAgent(AgentBase):
    def __init__(self, broker_host):
        super().__init__(broker_host, 'conveyor', 'CONVEYOR',
                         CONVEYOR_COMMAND, CONVEYOR_RESULT)
        self.arduino = serial.Serial('COM3', 9600, timeout=1)
        print("[CONVEYOR] Arduino 연결됨 (COM3)")

        threading.Thread(target=self.listen_arduino, daemon=True).start()

    def handle_command(self, data):
        cmd = data['command']

        if cmd == 'CONVEYOR_START':
            self.arduino.write(b"CONVEYOR_START\n")
            self.publish_result(cmd, 'DONE')
            self.publish_event(CONVEYOR_STATUS, {
                "motor": "ON",
                "actuator": "STOP",
                "status": "작동중"
            })

        elif cmd == 'CONVEYOR_STOP':
            cmd_str = b"CONVEYOR_STOP\n"
            print(f"[SEND] {repr(cmd_str)}")
            self.arduino.write(cmd_str)
            print("[TEST] Arduino 전송 완료")
            self.publish_result(cmd, 'DONE')
            self.publish_event(CONVEYOR_STATUS, {
                "motor": "OFF",
                "actuator": "STOP",
                "status": "정지"
            })

        elif cmd == 'EMERGENCY_STOP':
            self.arduino.write(b"EMERGENCY_STOP\n")
            self.publish_result(cmd, 'DONE')
            self.publish_event(CONVEYOR_STATUS, {
                "motor": "OFF",
                "actuator": "STOP",
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

                print(f"[ARDUINO] {line}")

                if line == "EVENT:PHYSICAL_ESTOP":
                    print("[CONVEYOR] 물리 비상정지 발동")
                    self.publish_event(CONVEYOR_SENSOR, {"event": "PHYSICAL_ESTOP"})

                elif line == "EVENT:ESTOP_RELEASED":
                    print("[CONVEYOR] 비상정지 해제")
                    self.publish_event(CONVEYOR_SENSOR, {"event": "ESTOP_RELEASED"})

                # 박스에 로봇팔 감지 (카운트 누적) - 7번 박스 포함
                elif line.startswith("EVENT:BOX_COUNT:"):
                    parts = line.split(":")
                    box_num = int(parts[2])
                    count = int(parts[3])
                    # 7번은 6개 기준, 나머지는 4개 기준
                    threshold = 6 if box_num == 7 else 4
                    print(f"[CONVEYOR] BOX{box_num} 카운트: {count}/{threshold}")
                    self.publish_event(CONVEYOR_SENSOR, {
                        "event": "BOX_COUNT",
                        "box": box_num,
                        "count": count
                    })

                # 일반 박스 가득 참 (BOX1~6, 4개 도달)
                elif line.startswith("EVENT:BOX_FULL:"):
                    box_num = int(line.split(":")[-1])
                    print(f"[CONVEYOR] 🚛 BOX{box_num} 가득 참! 라즈베리카 호출 필요")
                    self.publish_event(CONVEYOR_SENSOR, {
                        "event": "BOX_FULL",
                        "box": box_num
                    })

                # 불량 박스 가득 참 (BOX7, 6개 도달)
                elif line.startswith("EVENT:DEFECT_BOX_FULL:"):
                    box_num = int(line.split(":")[-1])
                    print(f"[CONVEYOR] ⚠️ 불량 박스(BOX{box_num}) 가득 참! 라즈베리카 호출 필요")
                    self.publish_event(CONVEYOR_SENSOR, {
                        "event": "DEFECT_BOX_FULL",
                        "box": box_num
                    })

            except Exception as e:
                print(f"[CONVEYOR] Arduino 수신 오류: {e}")
                break


if __name__ == '__main__':
    agent = ConveyorAgent('192.168.0.21')
    agent.connect()
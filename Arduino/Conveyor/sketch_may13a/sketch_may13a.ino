const int stepPin = 9;
const int dirPin = 8;
const int enaPin = 7;
const int emergencyPin = 3;

const int SENSOR_PINS[10] = {22, 24, 26, 28, 30, 32, 34, 36, 38, 40};
const int SENSOR_COUNT = 2;

bool emergencyStop = false;
unsigned long lastSensorPrint = 0;  // 센서 출력 타이머
bool conveyorRunning = false;

void setup() {
  pinMode(stepPin, OUTPUT);
  pinMode(dirPin, OUTPUT);
  pinMode(enaPin, OUTPUT);
  pinMode(emergencyPin, INPUT_PULLUP);

  for (int i = 0; i < SENSOR_COUNT; i++) {
    pinMode(SENSOR_PINS[i], INPUT_PULLUP);
  }

  digitalWrite(dirPin, HIGH);
  digitalWrite(enaPin, LOW);

  Serial.begin(9600);
  Serial.println("시스템 시작. 비상정지 버튼 D3.");
}

void loop() {
  handleSerial();

  // 비상정지 체크
  if (digitalRead(emergencyPin) == HIGH) {
    if (!emergencyStop) {
      emergencyStop = true;
      digitalWrite(enaPin, HIGH);
      Serial.println("비상정지 발동!");
    }
    return;
  }

  if (emergencyStop) {
    emergencyStop = false;
    digitalWrite(enaPin, LOW);
    Serial.println("비상정지 해제. 재시작!");
  }

  // 센서값 출력은 500ms마다만 (모터 타이밍 방해 안 함)
  if (millis() - lastSensorPrint >= 500) {
    lastSensorPrint = millis();
    for (int i = 0; i < SENSOR_COUNT; i++) {
      int val = digitalRead(SENSOR_PINS[i]);
      Serial.print("센서");
      Serial.print(i + 1);
      Serial.print(": ");
      Serial.print(val == LOW ? "감지" : "없음");
      Serial.print("  ");
    }
    Serial.println();
  }

  // 컨베이어 정상 동작
  if (conveyorRunning) {
  digitalWrite(stepPin, HIGH);
  delayMicroseconds(500);
  digitalWrite(stepPin, LOW);
  delayMicroseconds(500);
  }
}

void handleSerial() {
  if (Serial.available()) {
    String cmd = Serial.readStringUntil('\n');
    cmd.trim();

    if (cmd.startsWith("CONVEYOR_START")) {
      conveyorRunning = true;
      Serial.println("OK:CONVEYOR_START");

    } else if (cmd == "CONVEYOR_STOP") {
      conveyorRunning = false;
      digitalWrite(enaPin, HIGH);
      Serial.println("OK:CONVEYOR_STOP");

    } else if (cmd == "EMERGENCY_STOP") {
      conveyorRunning = false;
      digitalWrite(enaPin, HIGH);
      Serial.println("OK:EMERGENCY_STOP");
    }
  }
}
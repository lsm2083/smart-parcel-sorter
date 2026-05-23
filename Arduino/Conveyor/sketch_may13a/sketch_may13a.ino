const int stepPin = 9;
const int dirPin = 8;
const int enaPin = 7;
const int emergencyPin = 3;
const int buzzerPin = 6;

const int SENSOR_PINS[10] = {22, 24, 26, 28, 30, 32, 34, 36, 38, 40};
const int SENSOR_COUNT = 2;

bool emergencyStop = false;
bool lastSensorState = false;
unsigned long lastStatusPrint = 0;
bool conveyorRunning = false;
bool dirForward = true;

void setup() {
  pinMode(stepPin, OUTPUT);
  pinMode(dirPin, OUTPUT);
  pinMode(enaPin, OUTPUT);
  pinMode(emergencyPin, INPUT_PULLUP);
  pinMode(buzzerPin, OUTPUT);
  digitalWrite(buzzerPin, LOW);

  for (int i = 0; i < SENSOR_COUNT; i++) {
    pinMode(SENSOR_PINS[i], INPUT);
  }

  digitalWrite(dirPin, HIGH);
  digitalWrite(enaPin, LOW);

  Serial.begin(9600);
  Serial.println("READY");
}

void loop() {
  handleSerial();

  // 비상정지 발동
  if (digitalRead(emergencyPin) == HIGH) {
    if (!emergencyStop) {
      emergencyStop = true;
      digitalWrite(enaPin, HIGH);
      tone(buzzerPin, 1000);
      Serial.println("EVENT:PHYSICAL_ESTOP");
    }
    return;  // 펄스 안 보냄 → 컨베이어 멈춤
  }

  // 비상정지 해제 - 이 블록만 교체
if (emergencyStop) {
    emergencyStop = false;
    conveyorRunning = true;    // ← 이 한 줄 추가
    digitalWrite(enaPin, LOW);
    noTone(buzzerPin);
    Serial.println("EVENT:ESTOP_RELEASED");
}

 // loop() 안에 추가
if (millis() - lastStatusPrint >= 1000) {
    lastStatusPrint = millis();
    if (conveyorRunning) {
        Serial.println("STATUS:speed=500");
    }
}

bool curState = (digitalRead(SENSOR_PINS[0]) == LOW);  // LOW = 감지
if (curState && !lastSensorState) {
    Serial.println("EVENT:PACKAGE_DETECTED");
    Serial.println("EVENT:SCAN_POSITION_ARRIVED");
}
lastSensorState = curState;

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
      digitalWrite(enaPin, LOW);
      Serial.println("OK:CONVEYOR_START");

    } else if (cmd == "CONVEYOR_STOP") {
      conveyorRunning = false;
      digitalWrite(enaPin, HIGH);
      Serial.println("OK:CONVEYOR_STOP");

    } else if (cmd == "EMERGENCY_STOP") {
      conveyorRunning = false;
      digitalWrite(enaPin, HIGH);
      noTone(buzzerPin);
      Serial.println("OK:EMERGENCY_STOP");

    } else if (cmd == "DIR_FORWARD") {
      dirForward = true;
      digitalWrite(dirPin, HIGH);
      Serial.println("OK:DIR_FORWARD");

    } else if (cmd == "DIR_BACKWARD") {
      dirForward = false;
      digitalWrite(dirPin, LOW);
      Serial.println("OK:DIR_BACKWARD");

    } else if (cmd == "DIR_TOGGLE") {
      dirForward = !dirForward;
      digitalWrite(dirPin, dirForward ? HIGH : LOW);
      Serial.println(dirForward ? "OK:DIR_FORWARD" : "OK:DIR_BACKWARD");
    }
  }
}
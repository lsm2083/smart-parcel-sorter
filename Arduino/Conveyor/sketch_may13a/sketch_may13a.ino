const int stepPin = 9;
const int dirPin = 8;
const int enaPin = 7;
const int emergencyPin = 3;

const int SENSOR_PINS[10] = {22, 24, 26, 28, 30, 32, 34, 36, 38, 40};
const int SENSOR_COUNT = 2;

bool emergencyStop = false;
unsigned long lastSensorPrint = 0;
bool conveyorRunning = false;
bool dirForward = true;  // true = 정방향, false = 역방향

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
  Serial.println("명령: CONVEYOR_START / CONVEYOR_STOP / DIR_FORWARD / DIR_BACKWARD / DIR_TOGGLE");
}

void loop() {
  handleSerial();

  if (digitalRead(emergencyPin) == HIGH) {
    if (!emergencyStop) {
      emergencyStop = true;
      digitalWrite(enaPin, HIGH);
      Serial.println("EVENT:PHYSICAL_ESTOP");
    }
    return;
  }

  if (emergencyStop) {
    emergencyStop = false;
    digitalWrite(enaPin, LOW);
    Serial.println("비상정지 해제. 재시작!");
  }

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
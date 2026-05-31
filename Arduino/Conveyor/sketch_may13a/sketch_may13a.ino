const int stepPin = 9;
const int dirPin = 8;
const int enaPin = 7;
const int emergencyPin = 3;
const int buzzerPin = 6;

const int SENSOR_COUNT = 6;
const int SENSOR_PINS[SENSOR_COUNT] = {22, 24, 26, 28, 30, 32};

// 박스별 카운트 (4개 차면 가득 참 신호 발생)
const int BOX_FULL_COUNT = 4;
int boxCount[SENSOR_COUNT] = {0, 0, 0, 0, 0, 0};

// 디바운스: 마지막 감지 시각 기록 (ms)
const unsigned long DEBOUNCE_MS = 5000;  // 5초
unsigned long lastDetectTime[SENSOR_COUNT] = {0, 0, 0, 0, 0, 0};

bool emergencyStop = false;
bool lastSensorState[SENSOR_COUNT] = {false};
unsigned long lastStatusPrint = 0;
bool conveyorRunning = false;
bool dirForward = false;
int stepDelay = 500;

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

  digitalWrite(dirPin, dirForward ? HIGH : LOW);
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
      conveyorRunning = false;
      digitalWrite(enaPin, HIGH);
      tone(buzzerPin, 1000);
      Serial.println("EVENT:PHYSICAL_ESTOP");
    }
    return;
  }

  // 비상정지 해제
  if (emergencyStop) {
    emergencyStop = false;
    conveyorRunning = true;
    digitalWrite(enaPin, LOW);
    noTone(buzzerPin);
    Serial.println("EVENT:ESTOP_RELEASED");
  }

  // 상태 출력 (1초마다)
  if (millis() - lastStatusPrint >= 1000) {
    lastStatusPrint = millis();
    if (conveyorRunning) {
      Serial.print("STATUS:speed=");
      Serial.print(stepDelay);
      Serial.print(",dir=");
      Serial.println(dirForward ? "FWD" : "BWD");
    }
  }

  // 분류 박스 센서 6개 체크 (들어오는 순간만 감지)
  unsigned long now = millis();
  for (int i = 0; i < SENSOR_COUNT; i++) {
    bool curState = (digitalRead(SENSOR_PINS[i]) == LOW);  // LOW = 감지
    
    // 새로 감지된 순간 (이전엔 없었는데 지금 있음) + 디바운스 통과
    if (curState && !lastSensorState[i]) {
      if (now - lastDetectTime[i] >= DEBOUNCE_MS) {
        lastDetectTime[i] = now;
        boxCount[i]++;
        
        // 매번 카운트 알림 (디버깅/모니터링용)
        Serial.print("EVENT:BOX_COUNT:");
        Serial.print(i + 1);
        Serial.print(":");
        Serial.println(boxCount[i]);
        
        // 4개 차면 Flask에 보낼 신호 발생 + 자동 리셋
        if (boxCount[i] >= BOX_FULL_COUNT) {
          Serial.print("EVENT:BOX_FULL:");
          Serial.println(i + 1);
          boxCount[i] = 0;  // 자동 리셋
        }
      }
      // 디바운스 시간 내면 그냥 무시 (로그도 안 찍음)
    }
    
    lastSensorState[i] = curState;
  }

  // 펄스 출력
  if (conveyorRunning) {
    digitalWrite(stepPin, HIGH);
    delayMicroseconds(stepDelay);
    digitalWrite(stepPin, LOW);
    delayMicroseconds(stepDelay);
  }
}

void handleSerial() {
  if (Serial.available()) {
    String cmd = Serial.readStringUntil('\n');
    cmd.trim();

    if (cmd == "CONVEYOR_START") {
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

    } else if (cmd.startsWith("SPEED:")) {
      int newSpeed = cmd.substring(6).toInt();
      if (newSpeed >= 200 && newSpeed <= 5000) {
        stepDelay = newSpeed;
        Serial.print("OK:SPEED=");
        Serial.println(stepDelay);
      } else {
        Serial.println("ERR:SPEED_OUT_OF_RANGE");
      }

    } else if (cmd == "SENSOR_STATUS") {
      Serial.print("STATUS:SENSORS=");
      for (int i = 0; i < SENSOR_COUNT; i++) {
        Serial.print(digitalRead(SENSOR_PINS[i]) == LOW ? "1" : "0");
      }
      Serial.println();

    } else if (cmd == "COUNT_STATUS") {
      // 박스별 현재 카운트 조회
      Serial.print("STATUS:COUNTS=");
      for (int i = 0; i < SENSOR_COUNT; i++) {
        Serial.print(boxCount[i]);
        if (i < SENSOR_COUNT - 1) Serial.print(",");
      }
      Serial.println();

    } else if (cmd.startsWith("RESET_COUNT:")) {
      // 특정 박스 카운트 리셋 (예: RESET_COUNT:3)
      int boxNum = cmd.substring(12).toInt();
      if (boxNum >= 1 && boxNum <= SENSOR_COUNT) {
        boxCount[boxNum - 1] = 0;
        Serial.print("OK:RESET_COUNT:");
        Serial.println(boxNum);
      } else {
        Serial.println("ERR:INVALID_BOX_NUMBER");
      }

    } else if (cmd == "RESET_ALL_COUNTS") {
      // 전체 박스 카운트 리셋
      for (int i = 0; i < SENSOR_COUNT; i++) {
        boxCount[i] = 0;
      }
      Serial.println("OK:RESET_ALL_COUNTS");
    }
  }
}
const int stepPin = 9;
const int dirPin = 8;
const int enaPin = 7;
const int emergencyPin = 3;
const int buzzerPin = 6;

// 센서 7개 (BOX1~6: 일반 분류함, BOX7: 불량 박스)
const int SENSOR_COUNT = 7;
const int SENSOR_PINS[SENSOR_COUNT] = {22, 24, 26, 28, 30, 32, 34};

// 박스별 가득 참 기준 (1~6번=4개, 7번 불량박스=6개)
const int BOX_FULL_THRESHOLD[SENSOR_COUNT] = {4, 4, 4, 4, 4, 4, 6};
int boxCount[SENSOR_COUNT] = {0, 0, 0, 0, 0, 0, 0};

// 디바운스: 마지막 감지 시각 기록 (ms)
const unsigned long DEBOUNCE_MS = 5000;
unsigned long lastDetectTime[SENSOR_COUNT] = {0, 0, 0, 0, 0, 0, 0};

bool emergencyStop = false;
bool lastSensorState[SENSOR_COUNT] = {false};
bool conveyorRunning = false;
bool dirForward = false;
int stepDelay = 500;

void setup() {
  pinMode(stepPin, OUTPUT);
  pinMode(dirPin, OUTPUT);
  pinMode(enaPin, OUTPUT);
  pinMode(emergencyPin, INPUT_PULLUP);
  pinMode(buzzerPin, OUTPUT);
  digitalWrite(buzzerPin, HIGH);  // 부저 OFF 상태로 시작 (MH-FMD는 LOW 트리거)

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
      digitalWrite(buzzerPin, LOW);  // 부저 ON
      Serial.println("EVENT:PHYSICAL_ESTOP");
    }
    return;
  }

  // 비상정지 해제
  if (emergencyStop) {
    emergencyStop = false;
    conveyorRunning = true;
    digitalWrite(enaPin, LOW);
    digitalWrite(buzzerPin, HIGH);  // 부저 OFF
    Serial.println("EVENT:ESTOP_RELEASED");
  }

  // 분류 박스 센서 7개 체크
  unsigned long now = millis();
  for (int i = 0; i < SENSOR_COUNT; i++) {
    bool curState = (digitalRead(SENSOR_PINS[i]) == LOW);
    
    if (curState && !lastSensorState[i]) {
      if (now - lastDetectTime[i] >= DEBOUNCE_MS) {
        lastDetectTime[i] = now;
        boxCount[i]++;
        
        Serial.print("EVENT:BOX_COUNT:");
        Serial.print(i + 1);
        Serial.print(":");
        Serial.println(boxCount[i]);
        
        if (boxCount[i] >= BOX_FULL_THRESHOLD[i]) {
          if (i + 1 == 7) {
            Serial.print("EVENT:DEFECT_BOX_FULL:");
          } else {
            Serial.print("EVENT:BOX_FULL:");
          }
          Serial.println(i + 1);
          boxCount[i] = 0;
        }
      }
    }
    
    lastSensorState[i] = curState;
  }

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
      digitalWrite(buzzerPin, HIGH);  // 부저 OFF
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

    } else if (cmd == "SENSOR_STATUS") {
      Serial.print("STATUS:SENSORS=");
      for (int i = 0; i < SENSOR_COUNT; i++) {
        Serial.print(digitalRead(SENSOR_PINS[i]) == LOW ? "1" : "0");
      }
      Serial.println();

    } else if (cmd == "COUNT_STATUS") {
      Serial.print("STATUS:COUNTS=");
      for (int i = 0; i < SENSOR_COUNT; i++) {
        Serial.print(boxCount[i]);
        if (i < SENSOR_COUNT - 1) Serial.print(",");
      }
      Serial.println();

    } else if (cmd.startsWith("RESET_COUNT:")) {
      int boxNum = cmd.substring(12).toInt();
      if (boxNum >= 1 && boxNum <= SENSOR_COUNT) {
        boxCount[boxNum - 1] = 0;
        Serial.print("OK:RESET_COUNT:");
        Serial.println(boxNum);
      } else {
        Serial.println("ERR:INVALID_BOX_NUMBER");
      }

    } else if (cmd == "RESET_ALL_COUNTS") {
      for (int i = 0; i < SENSOR_COUNT; i++) {
        boxCount[i] = 0;
      }
      Serial.println("OK:RESET_ALL_COUNTS");
    }
  }
}
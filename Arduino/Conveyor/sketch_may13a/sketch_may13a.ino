const int stepPin = 9;
const int dirPin = 8;
const int enaPin = 7;
const int emergencyPin = 3;
const int buzzerPin = 10;

bool emergencyStop = false;
bool conveyorRunning = false;
bool dirForward = false;
int stepDelay = 500;

void setup() {
  pinMode(stepPin, OUTPUT);
  pinMode(dirPin, OUTPUT);
  pinMode(enaPin, OUTPUT);
  pinMode(emergencyPin, INPUT_PULLUP);
  pinMode(buzzerPin, OUTPUT);
  digitalWrite(buzzerPin, HIGH);  // 부저 OFF 상태로 시작 (LOW 트리거)

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
      Serial.println("[DEBUG] 비상정지 발동 - buzzerPin LOW 출력함");
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
    Serial.println("[DEBUG] 비상정지 해제 - buzzerPin HIGH 출력함");
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

    Serial.print("[DEBUG] 명령 받음: '");  // ← 추가
    Serial.print(cmd);                       // ← 추가
    Serial.println("'");                     // ← 추가


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
      //digitalWrite(buzzerPin, HIGH);  // 부저 OFF
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
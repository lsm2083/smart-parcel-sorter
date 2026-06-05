// 자동 반복: s = 시작, q = 현재 사이클 완료 후 복귀하고 정지
const int RPWM = 5;
const int LPWM = 6;
const int R_EN = 7;
const int L_EN = 8;

const int SPEED = 220;
const int PUSH_TIME = 2000;    // 전진 시간 (ms)
const int RETURN_TIME = 3000;  // 복귀 시간 (ms)
const int CYCLE_GAP = 15000;   // 사이클 간격 (ms)

bool running = false;
bool stopRequested = false; // q 눌렀는지 여부

void setup() {
  pinMode(RPWM, OUTPUT);
  pinMode(LPWM, OUTPUT);
  pinMode(R_EN, OUTPUT);
  pinMode(L_EN, OUTPUT);

  digitalWrite(R_EN, HIGH);
  digitalWrite(L_EN, HIGH);

  analogWrite(RPWM, 0);
  analogWrite(LPWM, 0);

  Serial.begin(9600);
  Serial.println("'s' = 시작 / 'q' = 현재 사이클 완료 후 복귀하고 정지");
}

void checkSerial() {
  if (Serial.available() > 0) {
    char cmd = Serial.read();
    if (cmd == 's' && !running) {
      running = true;
      stopRequested = false;
      Serial.println("시작!");
    }
    if (cmd == 'q') {
      stopRequested = true;
      Serial.println("정지 요청 - 현재 사이클 완료 후 복귀하고 멈춥니다...");
    }
  }
}

void loop() {
  checkSerial();

  if (running) {
    // 1. 전진
    Serial.println("전진 중...");
    analogWrite(RPWM, 0);
    analogWrite(LPWM, SPEED);
    delay(PUSH_TIME);

    // 2. 잠깐 정지
    analogWrite(LPWM, 0);
    delay(300);

    // 3. 완전 복귀 (q 눌렸어도 항상 복귀)
    Serial.println("완전 복귀 중...");
    analogWrite(RPWM, SPEED);
    delay(RETURN_TIME);

    // 4. 정지
    analogWrite(RPWM, 0);

    // 5. q 눌렸으면 여기서 종료
    if (stopRequested) {
      running = false;
      stopRequested = false;
      Serial.println("완전 복귀 완료. 정지!");
      return;
    }

    // 6. 15초 대기 (이 중에도 s/q 체크)
    Serial.println("대기 중... (15초)");
    for (int i = 0; i < 150; i++) {
      checkSerial();
      delay(100);
    }
  }
}

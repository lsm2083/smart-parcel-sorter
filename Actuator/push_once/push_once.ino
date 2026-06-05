// 수동 1회: p 누를 때마다 전진 → 완전 복귀 1사이클 실행
const int RPWM = 5;
const int LPWM = 6;
const int R_EN = 7;
const int L_EN = 8;

const int SPEED = 220;
const int PUSH_TIME = 2000;    // 전진 시간 (ms)
const int RETURN_TIME = 3000;  // 복귀 시간 (ms)

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
  Serial.println("'p' = 1회 실행 (전진 → 복귀)");
}

void loop() {
  if (Serial.available() > 0) {
    char cmd = Serial.read();

    if (cmd == 'p') {
      // 1. 전진
      Serial.println("전진 중...");
      analogWrite(RPWM, 0);
      analogWrite(LPWM, SPEED);
      delay(PUSH_TIME);

      // 2. 잠깐 정지
      analogWrite(LPWM, 0);
      delay(300);

      // 3. 완전 복귀
      Serial.println("완전 복귀 중...");
      analogWrite(RPWM, SPEED);
      delay(RETURN_TIME);

      // 4. 정지
      analogWrite(RPWM, 0);
      Serial.println("완료! 다시 'p' 누르면 재실행");
    }
  }
}

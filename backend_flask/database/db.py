import pymysql

# MySQL 연결 정보
DB_CONFIG = {
    'host': '192.168.0.20',
    'port': 3306,
    'user': 'final_user',
    'password': '1234',        # ← 본인 MySQL 비밀번호로 변경
    'database': 'smart_parcel_sorter',
    'charset': 'utf8mb4',
    'cursorclass': pymysql.cursors.DictCursor,
}


def get_db():
    return pymysql.connect(**DB_CONFIG)


def init_db():
    """DB 연결 확인만 수행 (테이블은 승민이가 이미 생성함)"""
    try:
        conn = get_db()
        cur = conn.cursor()
        cur.execute("SELECT 1")
        conn.close()
        print("[DB] MySQL 연결 확인 완료")
    except Exception as e:
        print(f"[DB] MySQL 연결 실패: {e}")
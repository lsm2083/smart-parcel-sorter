from database.db import get_db


def set_online(device_id, device_type, ip_address):
    conn = get_db()
    cur = conn.cursor()
    cur.execute("""
        INSERT INTO device_status (device_id, device_type, status, last_seen, ip_address)
        VALUES (%s, %s, 'ONLINE', NOW(), %s)
        ON DUPLICATE KEY UPDATE
            status='ONLINE', last_seen=NOW(), ip_address=%s
    """, (device_id, device_type, ip_address, ip_address))
    conn.commit()
    conn.close()


def set_disconnected(device_id):
    conn = get_db()
    cur = conn.cursor()
    cur.execute("""
        UPDATE device_status SET status='DISCONNECTED', last_seen=NOW()
        WHERE device_id=%s
    """, (device_id,))
    conn.commit()
    conn.close()
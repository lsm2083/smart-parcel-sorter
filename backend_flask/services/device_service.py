from database.db import get_db


def set_online(device_id, device_type, ip_address, stream_url=None):
    conn = get_db()
    cur = conn.cursor()
    cur.execute("""
        INSERT INTO device_status (device_id, device_type, status, last_seen, ip_address, stream_url)
        VALUES (%s, %s, 'ONLINE', NOW(), %s, %s)
        ON DUPLICATE KEY UPDATE
            status='ONLINE', last_seen=NOW(), ip_address=%s, stream_url=COALESCE(%s, stream_url)
    """, (device_id, device_type, ip_address, stream_url, ip_address, stream_url))
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
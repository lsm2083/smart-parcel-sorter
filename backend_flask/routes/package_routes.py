from flask import Blueprint, jsonify
from database.db import get_db

package_bp = Blueprint('package', __name__)


@package_bp.route('/package/current')
def get_current_package():
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("SELECT id, invoice_no, status FROM packages ORDER BY id DESC LIMIT 1")
        row = cur.fetchone()
    finally:
        conn.close()

    if not row:
        return jsonify({'package_id': None, 'status': 'WAIT_INPUT'})
    return jsonify({
        'package_id': row['id'],
        'invoice_no': row['invoice_no'],
        'status': row['status']
    })
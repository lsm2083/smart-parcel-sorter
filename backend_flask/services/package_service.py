"""
package_service.py — v6.0 (이미지 표시 최종본)

이미지 표시 2경로 모두 처리:
  1) 실시간 (sorting_log_added): payload 에 imageUrl 실어서 발행
  2) DB 조회 (LoadSortLogsAsync): error_logs.image_path 채워서
     지수 WPF가 FAIL-xxx 행에 이미지 붙이게 함  ← 이게 화면에 실제로 보이는 경로

핵심:
  - _mark_defect 가 방금 INSERT 한 error_logs row id 를 반환
  - 이미지 복사 후 그 row 의 image_path 를 UPDATE
  - 그래야 지수 LoadSortLogsAsync 가 error_logs.image_path 읽어서 FAIL 행에 아이콘 표시
"""
import os
import shutil
from datetime import datetime

from database.db import get_db
from mqtt.command_publish import (
    publish_sort, publish_emergency_stop_all,
    publish_blackbox_snapshot, publish_forklift_move,
)
from sockets.wpf_events import (
    emit_package_detected, emit_package_scanned, emit_package_classified,
    emit_emergency_stop, emit_blackbox_event_added, emit_sorting_log_added,
)
from services.manifest_service import lookup_manifest


# ── 분류함 번호 ↔ sort_code 매핑 (v6.0) ───────────
BOX_TO_SORT_CODE = {
    1: '서울_BOX',
    2: '서울_BOX_FRAGILE',
    3: '서울_VINYL',
    4: '경기도_BOX',
    5: '경기도_BOX_FRAGILE',
    6: '경기도_VINYL',
    7: 'DEFECT',
}
SORT_CODE_TO_BOX = {v: k for k, v in BOX_TO_SORT_CODE.items()}

# ── 이미지 경로 설정 ─────────────────────────────
VISION_DEBUG_DIR = os.path.abspath(
    os.path.join(os.path.dirname(__file__), '..', '..',
                 'control_python', 'debug_scans')
)
DEFECT_STORAGE_DIR = os.path.join('storage', 'blackbox', 'defect')


def _set_status(package_id, status):
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("UPDATE packages SET status=%s, updated_at=NOW() WHERE id=%s",
                    (status, package_id))
        conn.commit()
    finally:
        conn.close()


def _get_destination(sort_code):
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("SELECT destination_name FROM sort_destinations WHERE sort_code=%s",
                    (sort_code,))
        row = cur.fetchone()
        return row['destination_name'] if row else sort_code
    finally:
        conn.close()


def _mark_defect(pid, reason, error_code='SCAN_FAIL'):
    """
    packages 를 DEFECT 로 표시 + error_logs INSERT.
    ★ 방금 INSERT 한 error_logs row id 반환 (나중에 image_path UPDATE 용).
    """
    error_log_id = None
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("UPDATE packages SET status='DEFECT', sort_code='DEFECT', "
                    "updated_at=NOW() WHERE id=%s", (pid,))
        cur.execute(
            "INSERT INTO error_logs (error_code, device_id, package_id, message) "
            "VALUES (%s, %s, %s, %s)",
            (error_code, 'vision', pid, reason)
        )
        error_log_id = cur.lastrowid
        conn.commit()
    finally:
        conn.close()
    return error_log_id


def _update_error_log_image(error_log_id, image_url):
    """error_logs row 의 image_path 를 채운다 (지수 LoadSortLogsAsync가 읽는 컬럼)."""
    if not error_log_id or not image_url:
        return
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute(
            "UPDATE error_logs SET image_path=%s WHERE id=%s",
            (image_url, error_log_id)
        )
        conn.commit()
        print(f"[VISION_FAIL] error_logs.image_path 갱신: id={error_log_id} {image_url}")
    except Exception as e:
        print(f"[VISION_FAIL] error_logs image_path UPDATE 실패: {e}")
    finally:
        conn.close()


def _create_or_get_package(scan_id):
    """scan_id로 packages row 신규 생성. UNIQUE 제약이라 중복 시 기존 row 반환."""
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute("SELECT id FROM packages WHERE scan_id=%s", (scan_id,))
        row = cur.fetchone()
        if row:
            return row['id']

        cur.execute(
            "INSERT INTO packages (scan_id, status, created_at) "
            "VALUES (%s, 'SCANNING', NOW())",
            (scan_id,)
        )
        pid = cur.lastrowid
        conn.commit()
        return pid
    finally:
        conn.close()


# ── Vision 디버그 이미지 → defect storage 복사 ────────
def _copy_vision_image_to_defect_storage(scan_id):
    if not scan_id:
        return None

    src_filename = f"{scan_id}_roi.jpg"

    project_root = os.path.abspath(
        os.path.join(os.path.dirname(__file__), '..', '..')
    )

    candidates = [
        os.path.join(project_root, 'control_python', 'vision', 'debug_scans', src_filename),
        os.path.join(project_root, 'control_python', 'debug_scans', src_filename),
        os.path.join(project_root, 'backend_flask', 'debug_scans', src_filename),
        os.path.abspath(os.path.join('debug_scans', src_filename)),
    ]

    src_full = None
    for cand in candidates:
        if os.path.exists(cand):
            src_full = cand
            print(f"[VISION_FAIL] 이미지 발견: {cand}")
            break

    if src_full is None:
        print(f"[VISION_FAIL] 원본 이미지 없음. 시도한 경로:")
        for cand in candidates:
            print(f"  - {cand}")
        return None

    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    dst_filename = f"vision_fail_{ts}_{scan_id}.jpg"

    try:
        os.makedirs(DEFECT_STORAGE_DIR, exist_ok=True)
        dst_full = os.path.join(DEFECT_STORAGE_DIR, dst_filename)
        shutil.copy(src_full, dst_full)
        relative = f"blackbox/defect/{dst_filename}"
        print(f"[VISION_FAIL] 이미지 복사 완료: {src_full} → {dst_full}")
        return relative
    except Exception as e:
        print(f"[VISION_FAIL] 이미지 복사 실패: {e}")
        return None


def _record_vision_fail_blackbox(package_id, scan_id, fail_type, reason,
                                  invoice_no='', extra_info=None):
    """
    Vision 인식 실패 → 이미지 복사 + blackbox_events INSERT + WebSocket 발행.
    ★ image_url 반환 (호출자가 sorting_log_added / error_logs 갱신에 사용).
    """
    image_path = _copy_vision_image_to_defect_storage(scan_id)
    image_url = f"/storage/{image_path}" if image_path else None

    description_map = {
        'QR_OCR_MISMATCH': f'QR/OCR 불일치 — {reason}',
        'OCR_FAIL':        f'OCR 인식 실패 — {reason}',
        'SCAN_FAILED':     f'스캔 실패 — {reason}',
    }
    description = description_map.get(fail_type, f'Vision 오류 — {reason}')

    event_id = None
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute(
            "INSERT INTO blackbox_events "
            "(event_type, camera_id, package_id, image_path, severity, description, created_at) "
            "VALUES (%s, %s, %s, %s, %s, %s, NOW())",
            ('Vision실패', 'qr_camera', package_id, image_path,
             '오류', description)
        )
        event_id = cur.lastrowid
        conn.commit()
    except Exception as e:
        print(f"[VISION_FAIL] blackbox_events INSERT 실패: {e}")
    finally:
        conn.close()

    # blackbox_event_added 발행 (총괄 관제 탭용)
    try:
        emit_blackbox_event_added({
            'id': event_id,
            'package_id': package_id,
            'scan_id': scan_id,
            'invoice_no': invoice_no,
            'tracking_number': invoice_no,
            'eventType': 'Vision실패',
            'camera': 'qr_camera',
            'description': description,
            'imagePath': image_path,
            'imageUrl': image_url,
            'severity': '오류',
            'timestamp': datetime.now().isoformat(),
            'extra': extra_info or {},
        })
        print(f"[VISION_FAIL] WebSocket 발행: blackbox_event_added "
              f"image_url={image_url}")
    except Exception as e:
        print(f"[VISION_FAIL] WebSocket emit 실패: {e}")

    return image_url


# ── 센서 이벤트 (민지) ──────────────────────────────

def handle_sensor_event(data):
    event = data.get('event')

    if event == 'BOX_FULL':
        box_num = data.get('box')
        sort_code = BOX_TO_SORT_CODE.get(box_num)

        if not sort_code:
            print(f"[BOX_FULL] 알 수 없는 box 번호: {box_num}")
            return

        if sort_code == 'DEFECT':
            print(f"[BOX_FULL] DEFECT 분류함 가득 참 (box={box_num}) — 사람이 비워주세요")
            try:
                from app import socketio
                socketio.emit('defect_bin_full', {'box': box_num})
            except Exception:
                pass
            return

        try:
            publish_forklift_move(from_pos=sort_code, to_pos='CAR_SLOT')
        except Exception as e:
            print(f"[BOX_FULL] AGV 명령 발행 실패: {e}")
            return

        conn = get_db()
        try:
            cur = conn.cursor()
            cur.execute(
                "INSERT INTO forklift_jobs (from_position, to_position, status, started_at) "
                "VALUES (%s, %s, 'PENDING', NOW())",
                (sort_code, 'CAR_SLOT')
            )
            conn.commit()
        finally:
            conn.close()
        print(f"[BOX_FULL] AGV 호출: box={box_num} ({sort_code})")

    elif event == 'BOX_COUNT':
        box_num = data.get('box')
        count = data.get('count')
        sort_code = BOX_TO_SORT_CODE.get(box_num, f'UNKNOWN_BOX_{box_num}')
        print(f"[BOX_COUNT] box={box_num} ({sort_code}) count={count}/4")
        try:
            from app import socketio
            socketio.emit('box_count', {
                'box': box_num,
                'sort_code': sort_code,
                'count': count
            })
        except Exception:
            pass

    elif event == 'PHYSICAL_ESTOP':
        publish_emergency_stop_all()
        emit_emergency_stop(source='PHYSICAL_BUTTON')
        from services.system_state import system_state
        system_state["emergencyStop"] = True          # ← 추가
        from services.fcm_service import send_emergency_stop_notification
        send_emergency_stop_notification('PHYSICAL_BUTTON')

    elif event == 'ESTOP_RELEASED':
        from sockets.wpf_events import emit_emergency_reset
        emit_emergency_reset()


# ── 명령 결과 ──────────────────────────────────────

def handle_command_result(data):
    pass


# ── 스캔 결과 (Vision 자가 트리거) ─────────────────

def handle_scan_result(data):
    scan_id = data.get('scan_id')
    if not scan_id:
        print("[SCAN] scan_id 없음, 무시")
        return

    pid = _create_or_get_package(scan_id)
    scan_method = data.get('scan_method', 'QR')

    if scan_method == 'OCR':
        manifest = lookup_manifest(
            invoice_no=data.get('invoice_no'),
            region_code=data.get('region_code'),
            recipient_name=data.get('recipient_name'),
        )
        if manifest is None:
            error_log_id = _mark_defect(pid, reason='NO_MANIFEST_MATCH', error_code='OCR_NO_MATCH')
            publish_sort('DEFECT', pid, box=SORT_CODE_TO_BOX['DEFECT'])
            image_url = _record_vision_fail_blackbox(
                pid, scan_id, 'OCR_FAIL', 'NO_MANIFEST_MATCH',
                invoice_no=data.get('invoice_no', ''),
            )
            _update_error_log_image(error_log_id, image_url)   # ★ error_logs 갱신
            _emit_log('불량', '매니페스트 매칭 실패', pid, 'OCR',
                      data.get('invoice_no', ''), data.get('region_code', ''),
                      data.get('ocr_confidence', 0), image_url=image_url)
            return
        data['package_type'] = manifest['package_type']
        data['sort_code']    = manifest['sort_code']
        data['invoice_no']   = manifest['invoice_no']
        data['region']       = manifest['region']

    invoice_no = data.get('invoice_no', '')
    region = data.get('region', '')
    package_type = data.get('package_type', 'BOX')
    sort_code = data.get('sort_code') or f'{region}_{package_type}'

    try:
        destination = _get_destination(sort_code)
    except Exception:
        destination = sort_code

    conn = get_db()
    try:
        cur = conn.cursor()
        try:
            cur.execute("""
                UPDATE packages
                SET invoice_no=%s, region=%s, package_type=%s, sort_code=%s,
                    qr_raw=%s, status='CLASSIFIED', scanned_at=NOW()
                WHERE id=%s
            """, (invoice_no, region, package_type, sort_code,
                  data.get('qr_raw', ''), pid))

            cur.execute("""
                INSERT INTO sort_logs (package_id, invoice_no, sort_code, scan_method,
                                       sort_result, completed_at)
                VALUES (%s, %s, %s, %s, 'SORT_DONE', NOW())
            """, (pid, invoice_no, sort_code, scan_method))
            conn.commit()
        except Exception as e:
            print(f"[DB] 저장 실패, 최소 정보만 재시도: {e}")
            conn.rollback()
            cur.execute("""
                UPDATE packages
                SET invoice_no=%s, region=%s, package_type=%s,
                    status='CLASSIFIED', scanned_at=NOW()
                WHERE id=%s
            """, (invoice_no, region, package_type, pid))
            conn.commit()
    finally:
        conn.close()

    emit_package_detected(pid)
    emit_package_scanned(pid, invoice_no, sort_code, scan_method)
    emit_package_classified(pid, sort_code, destination)
    box = SORT_CODE_TO_BOX.get(sort_code)
    publish_sort(sort_code, pid, box=box)

    _emit_log('정상', '-', pid, scan_method, invoice_no, region, 100)


# ── 검증 실패 / 인식 실패 ──────────────────────────

def handle_mismatch(data):
    scan_id = data.get('scan_id')
    if not scan_id:
        return
    pid = _create_or_get_package(scan_id)
    reason = data.get('reason', 'QR_OCR_MISMATCH')
    error_log_id = _mark_defect(pid, reason=f"QR_OCR_MISMATCH: {reason}", error_code='QR_OCR_MISMATCH')

    try:
        publish_blackbox_snapshot(reason='QR_OCR_MISMATCH')
    except Exception as e:
        print(f"[BLACKBOX] snapshot 발행 실패: {e}")

    publish_sort('DEFECT', pid, box=SORT_CODE_TO_BOX['DEFECT'])

    qr_inv = data.get('qr_invoice', '') or data.get('ocr_invoice', '')
    image_url = _record_vision_fail_blackbox(
        pid, scan_id, 'QR_OCR_MISMATCH', reason,
        invoice_no=qr_inv,
        extra_info={
            'qr_invoice': data.get('qr_invoice'),
            'ocr_invoice': data.get('ocr_invoice'),
            'qr_region': data.get('qr_region'),
            'ocr_region': data.get('ocr_region'),
        },
    )
    _update_error_log_image(error_log_id, image_url)   # ★ error_logs 갱신

    _emit_log('불량', 'QR/OCR 불일치', pid, 'QR+OCR',
              qr_inv,
              data.get('qr_region', '') or data.get('ocr_region', ''),
              data.get('ocr_confidence', 0), image_url=image_url)


def handle_scan_fail(data):
    scan_id = data.get('scan_id')
    if not scan_id:
        return
    pid = _create_or_get_package(scan_id)
    reason = data.get('reason', 'QR_AND_OCR_FAILED')
    error_log_id = _mark_defect(pid, reason=reason, error_code='SCAN_FAIL')
    publish_sort('DEFECT', pid, box=SORT_CODE_TO_BOX['DEFECT'])

    image_url = _record_vision_fail_blackbox(pid, scan_id, 'OCR_FAIL', reason)
    _update_error_log_image(error_log_id, image_url)   # ★ error_logs 갱신

    _emit_log('불량', 'QR/OCR 인식실패', pid, '-', '', '',
              data.get('confidence', 0), image_url=image_url)


def handle_scan_failed_event(data):
    scan_id = data.get('scan_id')
    reason = data.get('reason', 'UNKNOWN')

    image_url = None
    if scan_id:
        pid = _create_or_get_package(scan_id)
        error_log_id = _mark_defect(pid, reason=f"SCAN_FAILED: {reason}", error_code='SCAN_FAILED')
        publish_sort('DEFECT', pid, box=SORT_CODE_TO_BOX['DEFECT'])

        image_url = _record_vision_fail_blackbox(pid, scan_id, 'SCAN_FAILED', reason)
        _update_error_log_image(error_log_id, image_url)   # ★ error_logs 갱신
    else:
        pid = None

    try:
        from app import socketio
        socketio.emit('scan_failed', {
            'scan_id': scan_id,
            'package_id': pid,
            'reason': reason,
            'time_text': data.get('time_text', ''),
        })
    except Exception as e:
        print(f"[WS] scan_failed emit 실패: {e}")

    print(f"[SCAN] 실패 알림: scan_id={scan_id} reason={reason}")
    _emit_log('불량', f'스캔실패({reason})', pid, '-', '', '', 0, image_url=image_url)


# ── 지게차 결과 ────────────────────────────────────

def handle_forklift_result(data):
    from sockets.wpf_events import emit_forklift_status
    conn = get_db()
    try:
        cur = conn.cursor()
        cur.execute(
            "UPDATE forklift_jobs SET status=%s, completed_at=NOW(), duration_s=%s WHERE id=%s",
            (data['result'], data.get('duration_s'), data['job_id'])
        )
        conn.commit()
    finally:
        conn.close()
    emit_forklift_status(data['result'], data['job_id'])


# ── 공통 로그 emit 헬퍼 ────────────────────────────

def _emit_log(status, error_type, pid, scan_method, tracking_no, region,
              confidence, image_url=None):
    """sorting_log_added 발행. image_url 있으면 payload 에 imageUrl 로 실음 (실시간 경로)."""
    try:
        payload = {
            'status': status,
            'errorType': error_type,
            'package_id': pid,
            'recognitionType': scan_method,
            'trackingNumber': tracking_no,
            'region': region,
            'confidence': confidence,
        }
        if image_url:
            payload['imageUrl'] = image_url
            print(f"[SORTING_LOG] 이미지 URL 포함: {image_url}")
        emit_sorting_log_added(payload)
    except Exception as e:
        print(f"[WS] sorting_log_added emit 실패: {e}")
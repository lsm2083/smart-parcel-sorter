# config.py
# Flask 서버 설정값

# 서버
FLASK_HOST = '0.0.0.0'
FLASK_PORT = 5000

# 데이터베이스 (SQLite)
DATABASE = 'parcel_sorter.db'

# Agent device_id 목록
DEVICE_IDS = {
    'conveyor':  'conveyor_agent_01',
    'robot':     'robot_agent_01',
    'vision':    'vision_agent_01',
    'blackbox':  'blackbox_agent_01',
    'forklift_box':   'forklift_box_01',
    'forklift_vinyl': 'forklift_vinyl_01',
}
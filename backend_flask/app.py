import eventlet
eventlet.monkey_patch()

from flask import Flask
from flask_socketio import SocketIO
from flask_mqtt import Mqtt
from flask_cors import CORS

app = Flask(__name__)
app.json.ensure_ascii = False
CORS(app)

app.config['MQTT_BROKER_URL'] = '127.0.0.1'
app.config['MQTT_BROKER_PORT'] = 1883
app.config['MQTT_KEEPALIVE'] = 60

socketio = SocketIO(app, cors_allowed_origins="*", async_mode='eventlet')
mqtt_client = Mqtt()  # 아직 연결 안 함


def register_all():
    from routes.status_routes import status_bp
    from routes.package_routes import package_bp
    from routes.log_routes import log_bp
    from routes.emergency_routes import emergency_bp
    from routes.yolo_routes import yolo_bp
    from routes.storage_routes import storage_bp
    from routes.blackbox_routes import blackbox_bp      # ★ 추가

    app.register_blueprint(status_bp, url_prefix='/api')
    app.register_blueprint(package_bp, url_prefix='/api')
    app.register_blueprint(log_bp, url_prefix='/api')
    app.register_blueprint(emergency_bp, url_prefix='/api')
    app.register_blueprint(yolo_bp, url_prefix='/api')
    app.register_blueprint(storage_bp)
    app.register_blueprint(blackbox_bp, url_prefix='/api')  # ★ 추가

    # 핸들러 먼저 등록
    from mqtt.mqtt_client import register_mqtt_handlers
    register_mqtt_handlers(mqtt_client, app)

    # 그 다음 브로커 연결
    mqtt_client.init_app(app)


if __name__ == '__main__':
    from database.db import init_db
    init_db()
    register_all()                     # ← 이게 빠져있었음
    socketio.run(app, host='0.0.0.0', port=5000)
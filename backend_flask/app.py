import eventlet
eventlet.monkey_patch()

from flask import Flask
from flask_socketio import SocketIO
from flask_cors import CORS

app = Flask(__name__)
app.json.ensure_ascii = False
CORS(app)
socketio = SocketIO(app, cors_allowed_origins="*", async_mode='eventlet')


def register_routes():
    from routes.status_routes import status_bp
    from routes.package_routes import package_bp
    from routes.log_routes import log_bp
    from routes.emergency_routes import emergency_bp

    app.register_blueprint(status_bp, url_prefix='/api')
    app.register_blueprint(package_bp, url_prefix='/api')
    app.register_blueprint(log_bp, url_prefix='/api')
    app.register_blueprint(emergency_bp, url_prefix='/api')

    from sockets import agent_handlers  # noqa: F401


if __name__ == '__main__':
    from database.db import init_db
    init_db()
    register_routes()
    socketio.run(app, host='0.0.0.0', port=5000)
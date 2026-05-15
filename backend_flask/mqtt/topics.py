# Flask -> Agent 명령
CONVEYOR_COMMAND = "parcel/conveyor/command"
ROBOT_COMMAND    = "parcel/robot/command"
VISION_COMMAND   = "parcel/vision/command"
BLACKBOX_COMMAND = "parcel/blackbox/command"
FORKLIFT_COMMAND = "parcel/forklift/command"
SYSTEM_EMERGENCY = "parcel/system/emergency"

# Agent -> Flask 이벤트
CONVEYOR_SENSOR  = "parcel/conveyor/sensor"
CONVEYOR_RESULT  = "parcel/conveyor/result"
CONVEYOR_STATUS  = "parcel/conveyor/status"
VISION_SCAN_RESULT = "parcel/vision/scan_result"
VISION_FAIL      = "parcel/vision/fail"
ROBOT_RESULT     = "parcel/robot/result"
ROBOT_STATUS     = "parcel/robot/status"
BLACKBOX_EVENT   = "parcel/blackbox/event"
FORKLIFT_RESULT  = "parcel/forklift/result"
FORKLIFT_STATUS  = "parcel/forklift/status"

# 장비 연결 상태 (LWT)
DEVICE_STATUS    = "parcel/+/status"
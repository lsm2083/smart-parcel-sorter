from pyzbar.pyzbar import decode


def detect_qr(frame):

    decoded_objects = decode(frame)

    for obj in decoded_objects:

        try:
            data = obj.data.decode("utf-8")

        except:
            data = obj.data.decode("cp949")

        points = obj.polygon

        bbox = []

        for point in points:
            bbox.append([point.x, point.y])

        return data, [bbox]

    return None, None
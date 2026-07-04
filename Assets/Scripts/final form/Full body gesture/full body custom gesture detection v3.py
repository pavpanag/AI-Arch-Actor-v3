import cv2
import mediapipe as mp
import numpy as np
from pythonosc import udp_client

# Camera index
camera_index = 1

# Initialize MediaPipe solutions with GPU support
mp_pose = mp.solutions.pose
mp_face = mp.solutions.face_mesh
mp_hands = mp.solutions.hands
mp_drawing = mp.solutions.drawing_utils

pose_estimator = mp_pose.Pose(model_complexity=1, enable_segmentation=False, min_detection_confidence=0.5)
face_estimator = mp_face.FaceMesh(max_num_faces=1, refine_landmarks=True, min_detection_confidence=0.5, min_tracking_confidence=0.5)
hand_estimator = mp_hands.Hands(max_num_hands=2, min_detection_confidence=0.5, min_tracking_confidence=0.5)
# OSC client setup
osc_client = udp_client.SimpleUDPClient("127.0.0.1", 3333)

# Global variables
recorded_poses = {}
selected_landmarks = []
landmark_sets = []
detecting_pose = False
stage = "Point Selection"
message = ""
pose_detected = None
previous_pose_state = None
current_pose_index = 1
pose_instances = {i: 0 for i in range(1, 10)}
tolerance = 15

# Function to calculate the angle between three points
def calculate_angle(a, b, c):
    a, b, c = np.array(a), np.array(b), np.array(c)
    radians = np.arctan2(c[1] - b[1], c[0] - b[0]) - np.arctan2(a[1] - b[1], a[0] - b[0])
    angle = np.degrees(radians)
    return angle + 360 if angle < 0 else angle

# Function to restart the process
def restart_process():
    global recorded_poses, selected_landmarks, landmark_sets, detecting_pose, stage, message, pose_detected, previous_pose_state, current_pose_index, pose_instances
    recorded_poses = {}
    selected_landmarks = []
    landmark_sets = []
    detecting_pose = False
    stage = "Point Selection"
    message = "Process restarted"
    pose_detected = None
    previous_pose_state = None
    current_pose_index = 1
    pose_instances = {i: 0 for i in range(1, 10)}
    print(message)

# Mouse callback function to select the closest landmark
def select_landmark(event, x, y, flags, param):
    global selected_landmarks, landmarks
    selection_radius = 5
    if event == cv2.EVENT_LBUTTONDOWN:
        closest_landmark = None
        min_distance = float('inf')
        for i, landmark in enumerate(landmarks):
            lx, ly = int(landmark.x * frame.shape[1]), int(landmark.y * frame.shape[0])
            distance = np.sqrt((x - lx) ** 2 + (y - ly) ** 2)
            if distance <= selection_radius and distance < min_distance:
                closest_landmark = i
                min_distance = distance
        if closest_landmark is not None:
            if closest_landmark not in selected_landmarks:
                selected_landmarks.append(closest_landmark)
            else:
                selected_landmarks.remove(closest_landmark)
        print(f"Selected landmarks: {selected_landmarks}")

# Capture video
cap = cv2.VideoCapture(camera_index)
cv2.namedWindow('Gesture Detection', cv2.WINDOW_NORMAL)
cv2.setMouseCallback('Gesture Detection', select_landmark)
cv2.createTrackbar('Tolerance', 'Gesture Detection', tolerance, 50, lambda val: globals().update(tolerance=val))
cv2.resizeWindow('Gesture Detection', 1280 * 2, 720 * 2)

while cap.isOpened():
    ret, frame = cap.read()
    if not ret:
        print("Failed to capture image")
        break

    # Process frame
    image_rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
    pose_result = pose_estimator.process(image_rgb)
    face_result = face_estimator.process(image_rgb)
    hand_result = hand_estimator.process(image_rgb)
    image_bgr = cv2.cvtColor(image_rgb, cv2.COLOR_RGB2BGR)

    # Display instructions
    instructions = [
        f"Stage: {stage}",
        "1. Choose sets of 3 points to define gestures",
        "2. Press 'r' to record gestures",
        "3. Press 't' to start recognizing gestures",
        "4. Press 'x' to restart process",
        "5. Press 'q' to quit",
        "6. Press '1-9' to select gesture index"
    ]
    for i, text in enumerate(instructions):
        cv2.putText(image_bgr, text, (10, 30 + i * 30), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 1, cv2.LINE_AA)

    # Draw landmarks
    landmarks = []
    if pose_result.pose_landmarks:
        mp_drawing.draw_landmarks(image_bgr, pose_result.pose_landmarks, mp_pose.POSE_CONNECTIONS)
        landmarks.extend(pose_result.pose_landmarks.landmark)
    if face_result.multi_face_landmarks:
        for face_landmarks in face_result.multi_face_landmarks:
            mp_drawing.draw_landmarks(image_bgr, face_landmarks, mp_face.FACEMESH_TESSELATION)
            landmarks.extend(face_landmarks.landmark)
    if hand_result.multi_hand_landmarks:
        for hand_landmarks in hand_result.multi_hand_landmarks:
            mp_drawing.draw_landmarks(image_bgr, hand_landmarks, mp_hands.HAND_CONNECTIONS)
            landmarks.extend(hand_landmarks.landmark)

    # Highlight selected landmarks
    for i, landmark in enumerate(landmarks):
        lx, ly = int(landmark.x * image_bgr.shape[1]), int(landmark.y * image_bgr.shape[0])
        color = (0, 255, 0) if i in selected_landmarks else (0, 0, 255)
        cv2.circle(image_bgr, (lx, ly), 2, color, -1)

    # Record selected landmarks
    if len(selected_landmarks) == 3 and all(idx < len(landmarks) for idx in selected_landmarks):
        landmark_sets.append(selected_landmarks.copy())
        selected_landmarks = []
        message = f"Landmark sets updated: {landmark_sets}"
        print(message)

    # Calculate angles
    current_angles = []
    for landmark_set in landmark_sets:
        if all(idx < len(landmarks) for idx in landmark_set):
            a, b, c = [landmarks[idx] for idx in landmark_set]
            current_angles.append(calculate_angle([a.x, a.y], [b.x, b.y], [c.x, c.y]))
    print(f"Current angles: {current_angles}")

    # Display angles
    for idx, angle in enumerate(current_angles):
        cv2.putText(image_bgr, f'Angle {idx + 1}: {int(angle)}', (10, 360 + idx * 30), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 0, 0), 1, cv2.LINE_AA)

    # Record gestures
    key = cv2.waitKey(10) & 0xFF
    print(f"Key pressed: {chr(key) if key != 255 else 'None'}")  # Debugging key press
    if key == ord('r'):
        recorded_poses.setdefault(current_pose_index, []).append(current_angles.copy())
        pose_instances[current_pose_index] += 1
        stage = "Gesture Recording"
        message = f"Gesture gesture_{current_pose_index} Instance {pose_instances[current_pose_index]} Recorded!"
        print(message)
        print(f"Recorded poses: {recorded_poses}")

    # Detect gestures
    if detecting_pose:
        print("Detecting gestures...")
        pose_detected = None
        for idx, poses in recorded_poses.items():
            for pose in poses:
                if all(abs(pa - ca) <= tolerance for pa, ca in zip(pose, current_angles)):
                    pose_detected = f"full_body_gesture_{idx}"
                    break
            if pose_detected:
                break

        if pose_detected:
            message = f"Pose detected: {pose_detected}"
            print(message)
        else:
            message = "No gesture detected"
            print(message)

    # Send OSC messages
    if stage == "Gesture Recognition" and detecting_pose:
        if pose_detected != previous_pose_state:
            osc_client.send_message("/gesture", pose_detected if pose_detected else "no_gesture")
            print(f"OSC: /gesture {pose_detected if pose_detected else 'no_gesture'}")
            previous_pose_state = pose_detected

    # Display message
    if message:
        cv2.putText(image_bgr, message, (10, 330), cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 255, 255), 2, cv2.LINE_AA)

    # Handle keyboard input
    if key == ord('t') and not detecting_pose:
        detecting_pose = True
        stage = "Gesture Recognition"
        message = "Gesture recognition started"
        print(message)
    elif key == ord('x'):
        restart_process()
    elif key == ord('q'):
        message = "Exiting program"
        print(message)
        break
    elif key in [ord(str(i)) for i in range(1, 10)]:
        current_pose_index = int(chr(key))
        message = f"Switched to Gesture Index: gesture_{current_pose_index}"
        print(message)

    # Show frame
    cv2.imshow('Gesture Detection', image_bgr)

# Release resources
cap.release()
cv2.destroyAllWindows()
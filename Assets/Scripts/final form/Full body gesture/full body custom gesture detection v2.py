import cv2
import mediapipe as mp
import numpy as np
from pythonosc import udp_client
import time

# Define the camera index
camera_index = 1  # Change this to the desired camera index

# Initialize MediaPipe pose, face, hand, and drawing utilities
mp_pose = mp.solutions.pose
mp_face = mp.solutions.face_mesh
mp_hands = mp.solutions.hands
mp_drawing = mp.solutions.drawing_utils

# Initialize the Pose, Face, and Hand estimators
pose_estimator = mp_pose.Pose()
face_estimator = mp_face.FaceMesh()
hand_estimator = mp_hands.Hands()

# Function to calculate the angle between three points
def calculate_angle(a, b, c):
    a = np.array(a)  # First point
    b = np.array(b)  # Midpoint
    c = np.array(c)  # Endpoint

    radians = np.arctan2(c[1] - b[1], c[0] - b[0]) - np.arctan2(a[1] - b[1], a[0] - b[0])
    angle = np.degrees(radians)
    
    if angle < 0:
        angle += 360

    return angle

# OSC client setup
osc_client = udp_client.SimpleUDPClient("127.0.0.1", 3333)

# Callback function for the trackbar
def on_trackbar(val):
    global tolerance
    tolerance = val

# Capture video from the selected webcam
cap = cv2.VideoCapture(camera_index)

# Variables to store recorded poses and other states
recorded_poses = {}
selected_landmarks = []
landmark_sets = []
detecting_pose = False  # Flag for pose detection
stage = "Point Selection"  # Current stage
message = ""  # Message to display
pose_detected = None  # Store the currently detected pose as a string
last_message_time = 0  # Tracks the last time an OSC message was sent
previous_pose_state = None  # Tracks the previous pose state (detected pose name or None)
current_pose_index = 1  # Initialize counter for poses
pose_instances = {i: 0 for i in range(1, 10)}  # Dictionary to store instances for each pose

def restart_process():
    """Resets all variables to restart the process."""
    global recorded_poses, selected_landmarks, landmark_sets, detecting_pose, stage, message, pose_detected, previous_pose_state, current_pose_index, pose_instances
    recorded_poses = {}
    selected_landmarks = []
    landmark_sets = []
    detecting_pose = False
    stage = "Point Selection"
    message = ""
    pose_detected = None
    previous_pose_state = None
    current_pose_index = 1  # Reset the pose index
    pose_instances = {i: 0 for i in range(1, 10)}  # Reset instances for each pose

# Mouse callback function to select landmarks
def select_landmark(event, x, y, flags, param):
    global selected_landmarks, landmarks
    selection_radius = 5  # Set the radius to match the drawn point size
    if event == cv2.EVENT_LBUTTONDOWN:
        for i, landmark in enumerate(landmarks):
            lx, ly = int(landmark.x * frame.shape[1]), int(landmark.y * frame.shape[0])
            if abs(x - lx) <= selection_radius and abs(y - ly) <= selection_radius:
                if i not in selected_landmarks:
                    selected_landmarks.append(i)
                else:
                    selected_landmarks.remove(i)

# Set mouse callback function
cv2.namedWindow('Gesture Detection', cv2.WINDOW_NORMAL)
cv2.setMouseCallback('Gesture Detection', select_landmark)

# Create a trackbar for adjusting tolerance
tolerance = 15  # Initial tolerance value
cv2.createTrackbar('Tolerance', 'Gesture Detection', tolerance, 50, on_trackbar)

# Resize the display window
cv2.resizeWindow('Gesture Detection', 1280 * 2, 720 * 2)  # Set the desired window size

while cap.isOpened():
    ret, frame = cap.read()
    
    if not ret:
        print("Failed to capture image")
        break

    # Convert the image to RGB for MediaPipe processing
    image_rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
    pose_result = pose_estimator.process(image_rgb)
    face_result = face_estimator.process(image_rgb)
    hand_result = hand_estimator.process(image_rgb)

    # Convert the image back to BGR for OpenCV
    image_bgr = cv2.cvtColor(image_rgb, cv2.COLOR_RGB2BGR)

    # Display instructions and current stage
    cv2.putText(image_bgr, f"Stage: {stage}", (10, 30), 
                cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 255), 2, cv2.LINE_AA)
    cv2.putText(image_bgr, "1. Choose sets of 3 points to define gestures", (10, 70), 
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 1, cv2.LINE_AA)
    cv2.putText(image_bgr, "2. Press 'r' to record gestures", (10, 100), 
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 1, cv2.LINE_AA)
    cv2.putText(image_bgr, "3. Press 't' to start recognizing gestures", (10, 130), 
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 1, cv2.LINE_AA)
    cv2.putText(image_bgr, "4. Press 'x' to restart process", (10, 160), 
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 1, cv2.LINE_AA)
    cv2.putText(image_bgr, "5. Press 'q' to quit", (10, 190), 
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 1, cv2.LINE_AA)
    cv2.putText(image_bgr, "6. Press '1-9' to select gesture index", (10, 220), 
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 1, cv2.LINE_AA)

    # Draw pose landmarks on the image
    if pose_result.pose_landmarks:
        mp_drawing.draw_landmarks(image_bgr, pose_result.pose_landmarks, mp_pose.POSE_CONNECTIONS)

    # Draw face landmarks on the image
    if face_result.multi_face_landmarks:
        for face_landmarks in face_result.multi_face_landmarks:
            mp_drawing.draw_landmarks(image_bgr, face_landmarks, mp_face.FACEMESH_TESSELATION)

    # Draw hand landmarks on the image
    if hand_result.multi_hand_landmarks:
        for hand_landmarks in hand_result.multi_hand_landmarks:
            mp_drawing.draw_landmarks(image_bgr, hand_landmarks, mp_hands.HAND_CONNECTIONS)

    # Get the landmark coordinates
    landmarks = []
    if pose_result.pose_landmarks:
        landmarks.extend(pose_result.pose_landmarks.landmark)
    if face_result.multi_face_landmarks:
        for face_landmarks in face_result.multi_face_landmarks:
            landmarks.extend(face_landmarks.landmark)
    if hand_result.multi_hand_landmarks:
        for hand_landmarks in hand_result.multi_hand_landmarks:
            landmarks.extend(hand_landmarks.landmark)

    # Highlight selected landmarks
    for i in range(len(landmarks)):
        lx, ly = int(landmarks[i].x * image_bgr.shape[1]), int(landmarks[i].y * image_bgr.shape[0])
        if i in selected_landmarks:
            cv2.circle(image_bgr, (lx, ly), 2, (0, 255, 0), -1)  # Smaller green circle for selected
        else:
            cv2.circle(image_bgr, (lx, ly), 1, (0, 0, 255), -1)  # Smaller red circle for unselected

    # Check if three landmarks are selected
    if len(selected_landmarks) == 3:
        if all(idx < len(landmarks) for idx in selected_landmarks):  # Ensure indices are valid
            landmark_sets.append(selected_landmarks.copy())
        selected_landmarks = []

    # Calculate angles for all sets of selected landmarks
    current_angles = []
    for landmark_set in landmark_sets:
        if all(idx < len(landmarks) for idx in landmark_set):  # Ensure indices are valid
            a = [landmarks[landmark_set[0]].x, landmarks[landmark_set[0]].y]
            b = [landmarks[landmark_set[1]].x, landmarks[landmark_set[1]].y]
            c = [landmarks[landmark_set[2]].x, landmarks[landmark_set[2]].y]
            angle = calculate_angle(a, b, c)
            current_angles.append(angle)

    # Display angles
    for idx, angle in enumerate(current_angles):
        cv2.putText(image_bgr, f'Angle {idx+1}: {int(angle)}', (10, 360 + idx * 30), 
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 0, 0), 1, cv2.LINE_AA)

    # If the user presses 'r', record this gesture as a new set of reference angles
    key = cv2.waitKey(10) & 0xFF  # Increase delay to 10ms
    if key == ord('r'):
        if current_pose_index not in recorded_poses:
            recorded_poses[current_pose_index] = []
        recorded_poses[current_pose_index].append(current_angles.copy())
        pose_instances[current_pose_index] += 1
        stage = "Gesture Recording"
        message = f"Gesture gesture_{current_pose_index} Instance {pose_instances[current_pose_index]} Recorded!"
        print(f'Gesture gesture_{current_pose_index} Instance {pose_instances[current_pose_index]} Recorded!')

    # If detecting gestures, compare current angles to recorded gestures
    if detecting_pose:
        pose_detected = None
        for idx, poses in recorded_poses.items():
            for pose in poses:
                if all(abs(pa - ca) <= tolerance for pa, ca in zip(pose, current_angles)):
                    pose_detected = f"full_body_gesture_{idx}"
                    break
            if pose_detected:
                break

    else:
        current_angles = []  # Clear angles when no gesture is detected

    # Display detected gesture
    if stage == "Gesture Recognition" and detecting_pose:
        if pose_detected:
            message = f"Gesture Detected: {pose_detected}"
        else:
            message = "No Gesture Detected"
    elif stage == "Gesture Recording":
        message = f"Gesture gesture_{current_pose_index} Instance {pose_instances[current_pose_index]} Recorded"

    if message:
        cv2.putText(image_bgr, message, (10, 330),
                    cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 255, 255), 2, cv2.LINE_AA)

    # OSC message sending
    if stage == "Gesture Recognition" and detecting_pose:
        if pose_detected != previous_pose_state:
            if pose_detected:
                osc_client.send_message("/gesture", pose_detected)  # Send the gesture name
                print(f"OSC: /gesture {pose_detected}")
            else:
                osc_client.send_message("/gesture", "gesture_none")
                print("OSC: /gesture gesture_none")
            previous_pose_state = pose_detected

    # Display the image
    cv2.imshow('Gesture Detection', image_bgr)

    # Keyboard input handling
    key = cv2.waitKey(10) & 0xFF  # Increase delay to 10ms
    if key == ord('t'):
        detecting_pose = True
        stage = "Gesture Recognition"
    elif key == ord('x'):
        restart_process()
    elif key == ord('q'):
        break
    elif key in [ord(str(i)) for i in range(1, 10)]:
        current_pose_index = int(chr(key))
        print(f"Selected Gesture Index: gesture_{current_pose_index}")

# Release resources
cap.release()
cv2.destroyAllWindows()
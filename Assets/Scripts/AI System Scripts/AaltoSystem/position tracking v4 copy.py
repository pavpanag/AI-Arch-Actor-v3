import os
os.environ['KMP_DUPLICATE_LIB_OK'] = 'TRUE'

import cv2
from ultralytics import YOLO
import time
import json
from pythonosc import udp_client
import torch

# Headless: no camera-preview window and no matplotlib graph — detection + OSC only, to save
# CPU/GPU during the demo. The tracked people are shown in Unity, which is the only view needed.
camera_index = 1
RES_WIDTH, RES_HEIGHT = 640, 480

# Load YOLOv8 Nano model for faster inference
model = YOLO('yolov8n.pt')

# Enable GPU acceleration if available
if torch.cuda.is_available():
    model.to('cuda')

# OSC client setup
# NOTE: 3334, not 3333 — the C&C demo's speech receiver owns 3333. Unity listens for
# "/position" on 3334 via an OSC component wired to ReceivePosition.
osc_client = udp_client.SimpleUDPClient("127.0.0.1", 3334)

# Function to detect humans in the frame and return their positions
def detect_humans(frame):
    # Resize the frame for faster processing (use consistent resolution)
    frame = cv2.resize(frame, (RES_WIDTH, RES_HEIGHT))
    # Flip the frame horizontally
    frame = cv2.flip(frame, 1)

    # verbose=False silences YOLO's per-frame console print (overhead + noise, no window to see).
    results = model(frame, verbose=False)
    positions = []

    for result in results:
        for box in result.boxes:
            # safe scalar extraction for confidence
            try:
                conf = float(box.conf)
            except Exception:
                conf = float(box.conf.cpu().numpy())

            if conf < 0.5:
                continue

            # safe extraction of xyxy coordinates
            try:
                xy = box.xyxy
                xy_np = xy.cpu().numpy()
            except Exception:
                xy_np = box.xyxy  # fallback

            if xy_np.ndim == 2:
                x1, y1, x2, y2 = xy_np[0]
            else:
                x1, y1, x2, y2 = xy_np

            cls = int(box.cls[0].cpu().numpy()) if hasattr(box.cls, 'cpu') else int(box.cls)

            if model.names[cls] == "person":
                # No drawing — headless. Just take the person's centre.
                center_x = (x1 + x2) / 2
                center_y = (y1 + y2) / 2
                positions.append((center_x, center_y))

    return frame, positions

# Initialize video capture (0 for webcam, or provide path to a video file)
cap = cv2.VideoCapture(camera_index)
if not cap.isOpened():
    raise SystemExit("Error: Cannot open camera. Check camera index or connection.")

# Use the fixed resize resolution for normalization and plotting
frame_width = RES_WIDTH
frame_height = RES_HEIGHT

# Variable to store the latest positions with timestamp
latest_positions = []

# Headless detection loop — runs on the main thread (no windows to service), detects people,
# and sends each position to Unity over OSC. Ctrl+C in the console to stop.
def run_detection():
    global latest_positions
    start_time = time.time()
    frame_skip = 2  # Process every 2nd frame
    frame_count = 0

    print("Tracking started (headless). Sending /position to 127.0.0.1:3334. Ctrl+C to stop.")

    while cap.isOpened():
        ret, frame = cap.read()
        if not ret:
            break

        frame_count += 1
        if frame_count % frame_skip != 0:
            continue

        # Detect humans in the frame
        _, positions = detect_humans(frame)

        # Store the most recent positions with timestamp
        current_time = time.time()
        if current_time - start_time >= 1:
            if positions:
                # Normalize and round positions using the consistent resized frame dimensions
                latest_positions = [
                    ( round((frame_width - pos[0]) / frame_width * 100), round(pos[1] / frame_height * 100) )
                    for pos in positions
                ]

                # Save the latest positions with timestamp and camera resolution to a file
                data_to_save = {
                    "latest_positions": latest_positions,
                    "timestamp": current_time,
                    "frame_width": frame_width,
                    "frame_height": frame_height
                }
                with open('latest_positions.json', 'w') as f:
                    json.dump(data_to_save, f)

                # Send OSC messages for each position
                for pos in latest_positions:
                    osc_client.send_message("/position", pos)

            start_time = current_time

    cap.release()


try:
    run_detection()
except KeyboardInterrupt:
    print("\nTracking stopped.")
finally:
    cap.release()
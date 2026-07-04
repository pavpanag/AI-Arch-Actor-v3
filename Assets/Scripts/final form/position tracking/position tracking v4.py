import os
import cv2
import time
import json
import torch
import matplotlib.pyplot as plt
import matplotlib.animation as animation
from ultralytics import YOLO
from threading import Thread
from pythonosc import udp_client

# Prevent library conflicts
os.environ['KMP_DUPLICATE_LIB_OK'] = 'TRUE'

# Configuration
CAMERA_INDEX = 0
FRAME_SKIP = 2  # Process every 2nd frame
CONFIDENCE_THRESHOLD = 0.5  # Minimum confidence for detections
OSC_IP = "127.0.0.1"
OSC_PORT = 3333
OUTPUT_JSON = "latest_positions.json"

# Initialize YOLOv8 model
model = YOLO('yolov8n.pt')

# Enable GPU acceleration if available
if torch.cuda.is_available():
    model.to('cuda')

# Initialize OSC client
osc_client = udp_client.SimpleUDPClient(OSC_IP, OSC_PORT)

# Initialize video capture
cap = cv2.VideoCapture(CAMERA_INDEX)
frame_width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
frame_height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))

# Global variable to store the latest positions
latest_positions = []

# Function to detect humans in a frame
def detect_humans(frame):
    # Resize and flip the frame for processing
    frame = cv2.resize(frame, (640, 480))
    frame = cv2.flip(frame, 1)

    # Run YOLOv8 inference
    results = model(frame)
    positions = []

    for result in results:
        for box in result.boxes:
            conf = box.conf[0].cpu().numpy()  # Confidence score
            if conf < CONFIDENCE_THRESHOLD:
                continue

            x1, y1, x2, y2 = box.xyxy[0].cpu().numpy()  # Bounding box coordinates
            cls = int(box.cls[0].cpu().numpy())  # Class ID

            if model.names[cls] == "person":
                # Draw bounding box and label
                cv2.rectangle(frame, (int(x1), int(y1)), (int(x2), int(y2)), (0, 255, 0), 2)
                label = f"Person {conf:.2f}"
                cv2.putText(frame, label, (int(x1), int(y1) - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 0), 2)

                # Calculate the center of the bounding box
                center_x = (x1 + x2) / 2
                center_y = (y1 + y2) / 2
                positions.append((center_x, center_y))

    return frame, positions

# Function to normalize positions and send OSC messages
def process_positions(positions):
    global latest_positions
    # Normalize positions to percentages
    latest_positions = [
        (round((frame_width - pos[0]) / frame_width * 100), round(pos[1] / frame_height * 100))
        for pos in positions
    ]

    # Save positions to JSON file
    data_to_save = {
        "latest_positions": latest_positions,
        "timestamp": time.time(),
        "frame_width": frame_width,
        "frame_height": frame_height
    }
    with open(OUTPUT_JSON, 'w') as f:
        json.dump(data_to_save, f)

    # Send OSC messages
    for pos in latest_positions:
        osc_client.send_message("/position", pos)

# Function to update the plot
def update_plot(i):
    ax.clear()
    ax.set_xlim(0, 100)
    ax.set_ylim(0, 100)
    if latest_positions:
        for pos in latest_positions:
            ax.plot(pos[0], pos[1], 'ro')
            ax.text(pos[0], pos[1], f'({pos[0]}, {pos[1]})', fontsize=9, ha='right')

# Real-time detection loop
def run_detection():
    global latest_positions
    frame_count = 0
    start_time = time.time()

    while cap.isOpened():
        ret, frame = cap.read()
        if not ret:
            break

        frame_count += 1
        if frame_count % FRAME_SKIP != 0:
            continue

        # Detect humans in the frame
        frame_with_detections, positions = detect_humans(frame)

        # Process positions every second
        if time.time() - start_time >= 1:
            if positions:
                process_positions(positions)
            start_time = time.time()

        # Uncomment to display the frame (optional)
        # cv2.imshow('YOLOv8 Human Detection', frame_with_detections)

        # Exit on 'q' key press
        if cv2.waitKey(1) & 0xFF == ord('q'):
            break

    # Release resources
    cap.release()
    cv2.destroyAllWindows()

# Start detection in a separate thread
detection_thread = Thread(target=run_detection)
detection_thread.start()

# Create a plot for visualizing positions
fig, ax = plt.subplots()
ani = animation.FuncAnimation(fig, update_plot, interval=1000, cache_frame_data=False)

# Show the plot
plt.show()

# Wait for the detection thread to finish
detection_thread.join()

# Print the latest positions
if latest_positions:
    print(f"Latest Positions: {latest_positions}")
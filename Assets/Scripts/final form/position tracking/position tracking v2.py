import os
os.environ['KMP_DUPLICATE_LIB_OK'] = 'TRUE'

import cv2
from ultralytics import YOLO
import time
import json
import matplotlib.pyplot as plt
import matplotlib.animation as animation
from threading import Thread
from pythonosc import udp_client

camera_index = 2

# Load YOLOv8 pre-trained model
model = YOLO('yolov8s.pt')

# OSC client setup
osc_client = udp_client.SimpleUDPClient("127.0.0.1", 3333)

# Function to detect humans in the frame and return their positions
def detect_humans(frame):
    # Flip the frame horizontally
    frame = cv2.flip(frame, 1)
    results = model(frame)
    positions = []

    for result in results:
        for box in result.boxes:
            x1, y1, x2, y2 = box.xyxy[0].cpu().numpy()  # Box coordinates
            conf = box.conf[0].cpu().numpy()  # Confidence score
            cls = int(box.cls[0].cpu().numpy())  # Class ID

            if model.names[cls] == "person":
                # Draw the bounding box without inverting the x-coordinates
                cv2.rectangle(frame, (int(x1), int(y1)), (int(x2), int(y2)), (0, 255, 0), 2)
                label = f"Person {conf:.2f}"
                cv2.putText(frame, label, (int(x1), int(y1) - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 0), 2)

                # Calculate the center of the bounding box
                center_x = (x1 + x2) / 2
                center_y = (y1 + y2) / 2
                positions.append((center_x, center_y))

    return frame, positions

# Initialize video capture (0 for webcam, or provide path to a video file)
cap = cv2.VideoCapture(camera_index)

# Get the camera resolution
frame_width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
frame_height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))

# Variable to store the latest positions with timestamp
latest_positions = []

# Function to update the plot with all the latest positions
def update_plot(i):
    ax.clear()
    ax.set_xlim(0, 100)
    ax.set_ylim(0, 100)
    if latest_positions:
        for pos in latest_positions:
            ax.plot(pos[0], pos[1], 'ro')
            ax.text(pos[0], pos[1], f'({pos[0]}, {pos[1]})', fontsize=9, ha='right')

# Create a figure and axis for the plot
fig, ax = plt.subplots()
ani = animation.FuncAnimation(fig, update_plot, interval=100, cache_frame_data=False)

# Run real-time detection in a separate thread
def run_detection():
    global latest_positions
    start_time = time.time()
    while cap.isOpened():
        ret, frame = cap.read()
        if not ret:
            break

        # Detect humans in the frame
        frame_with_detections, positions = detect_humans(frame)

        # Store the most recent positions with timestamp
        current_time = time.time()
        if current_time - start_time >= 1:
            if positions:
                # Normalize and round positions
                latest_positions = [
                    (round((frame_width - pos[0]) / frame_width * 100), round(pos[1] / frame_height * 100))
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
                    print(f"OSC: /position {pos}")

            start_time = current_time

        # Display the frame with detections
        cv2.imshow('YOLOv8 Human Detection (Flipped)', frame_with_detections)

        # Press 'q' to exit the loop
        if cv2.waitKey(1) & 0xFF == ord('q'):
            break

    # Release the video capture and close windows
    cap.release()
    cv2.destroyAllWindows()

# Start the detection thread
detection_thread = Thread(target=run_detection)
detection_thread.start()

# Show the plot
plt.show()

# Wait for the detection thread to finish
detection_thread.join()

# Print the stored latest positions with timestamp
if latest_positions:
    print(f"Latest Positions: {latest_positions}")
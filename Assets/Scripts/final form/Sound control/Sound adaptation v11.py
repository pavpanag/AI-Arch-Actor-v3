import tkinter as tk
import threading
import os
import wave
import pygame
import sounddevice as sd
import numpy as np
from functools import partial
from pythonosc import dispatcher, osc_server
from tkinter import filedialog
import time

# -----------------------------
# CONFIG & GLOBAL VARIABLES
# -----------------------------

OSC_PORT = 5555        # Same port as your light script
SAMPLE_RATE = 44100
NUM_MEMORIES = 8

# Holds file paths for each slot (sound_1.wav, sound_2.wav, etc.)
MEMORY_FILES = [None] * NUM_MEMORIES

# Holds which output device index (as a string) each memory uses
OUTPUT_DEVICES = [None] * NUM_MEMORIES

NOISE_THRESHOLD = 0   # Adjust this for the noise gate
is_recording = False   # Flag to stop the recording loop
selected_input_device = None

# For listing devices in GUI
output_device_names = []

# Initialize Pygame for playback
pygame.mixer.init()

# Create recordings folder if it doesn't exist
RECORDINGS_FOLDER = os.path.join(os.path.dirname(__file__), "recordings")
os.makedirs(RECORDINGS_FOLDER, exist_ok=True)

# -----------------------------
# AUDIO / RECORDING FUNCTIONS
# -----------------------------

def list_input_devices():
    """ Return a list of (index, name) for all input-capable devices. """
    devices = sd.query_devices()
    input_devices = [
        (i, device['name'])
        for i, device in enumerate(devices)
        if device.get('max_input_channels', 0) > 0
    ]
    return input_devices

def list_output_devices():
    """ Return a list of (index, name) for all output-capable devices. """
    devices = sd.query_devices()
    output_devices = [
        (i, device['name'])
        for i, device in enumerate(devices)
        if device.get('max_output_channels', 0) > 0
    ]
    return output_devices

def record_audio(memory_index, device_index):
    """
    Records audio from the selected input device, applying a basic noise gate,
    and saves it to a WAV file (sound_N.wav).
    """
    global is_recording
    is_recording = True
    filename = os.path.join(RECORDINGS_FOLDER, f"sound_{memory_index+1}.wav")
    print(f"[Sound {memory_index+1}] Recording to {filename}... (Stop by clicking 'Stop Recording')")

    # Stop any playback and unload the sound file
    stop_audio()
    MEMORY_FILES[memory_index] = None

    def callback(indata, frames, time, status):
        if status:
            print(status)
        if not is_recording:
            raise sd.CallbackStop
        # Noise gate
        indata = np.where(np.abs(indata) < NOISE_THRESHOLD, 0, indata)
        wf.writeframes(indata.copy())

    try:
        with wave.open(filename, 'wb') as wf:
            wf.setnchannels(1)
            wf.setsampwidth(2)  # 2 bytes per sample (int16)
            wf.setframerate(SAMPLE_RATE)

            with sd.InputStream(
                samplerate=SAMPLE_RATE,
                channels=1,
                dtype='int16',
                callback=callback,
                device=device_index
            ):
                while is_recording:
                    sd.sleep(100)

        MEMORY_FILES[memory_index] = filename
        print(f"[Sound {memory_index+1}] Saved to {filename}")

    except Exception as e:
        print(f"Error recording audio: {e}")
        is_recording = False

def stop_recording():
    """ Stops the recording callback. """
    global is_recording
    is_recording = False
    print("Recording stopped.")

def load_audio(memory_index):
    """ Let user pick a file to assign to memory slot. """
    filepath = filedialog.askopenfilename(
        filetypes=[("Audio Files", "*.wav *.mp3 *.ogg"), ("All Files", "*.*")]
    )
    if filepath:
        MEMORY_FILES[memory_index] = filepath
        print(f"[Sound {memory_index+1}] Loaded: {filepath}")

def play_audio(memory_index):
    """
    Plays the file in MEMORY_FILES[memory_index],
    using the device set in OUTPUT_DEVICES[memory_index].
    """
    audio_file = MEMORY_FILES[memory_index]
    output_device = OUTPUT_DEVICES[memory_index]

    if not audio_file or not os.path.isfile(audio_file):
        print(f"[Sound {memory_index+1}] No valid audio file loaded.")
        return

    print(f"[Sound {memory_index+1}] Playing: {audio_file} on device {output_device}")
    # Re-init pygame mixer with the chosen device
    pygame.mixer.quit()
    pygame.mixer.init(devicename=output_device)  # Some OSes may ignore devicename.
    pygame.mixer.music.load(audio_file)
    pygame.mixer.music.play()

def stop_audio():
    """ Stops all playback and unloads any playing file. """
    pygame.mixer.music.stop()
    # Explicitly unload the music to release the file handle
    pygame.mixer.music.unload()
    print("Playback stopped.")
    # Give Windows a moment to release the file handle
    time.sleep(0.2)

# -----------------------------
# OSC HANDLING
# -----------------------------

def handle_osc(address, *args):
    """
    We handle all incoming messages (e.g. "sound 1"), 
    then parse the argument string. For example:
      Args=('sound 1',)
    We'll parse out 'sound' and '1'.
    """
    print(f"[OSC] Received message: Address={address}, Args={args}")
    if len(args) > 0:
        # Example: "sound 1"
        arg_str = str(args[0])  # e.g. "sound 1"
        parts = arg_str.split()
        if len(parts) == 2:
            cmd, num_str = parts[0], parts[1]
            if cmd == "sound":
                try:
                    mem_index = int(num_str) - 1
                    if 0 <= mem_index < NUM_MEMORIES:
                        play_audio(mem_index)
                    else:
                        print(f"[OSC] Memory index out of range: {num_str}")
                except ValueError:
                    print(f"[OSC] Invalid number: {num_str}")
            else:
                print(f"[OSC] Unrecognized command: {cmd}")
        else:
            print(f"[OSC] Could not parse argument: {arg_str}")
    else:
        print("[OSC] No arguments provided.")

def start_osc_server():
    """ Spin up an OSC server on port 4444. """
    try:
        disp = dispatcher.Dispatcher()
        disp.map("*", handle_osc)  # Handle ALL addresses
        server = osc_server.ThreadingOSCUDPServer(("0.0.0.0", OSC_PORT), disp)
        print(f"[OSC] Server started, listening on port {OSC_PORT}...")
        server.serve_forever()
    except OSError as e:
        print(f"[OSC] Server error: {e}")

# -----------------------------
# GUI
# -----------------------------

def create_memory_ui(root, memory_index):
    """
    Creates a row of UI elements for one memory slot:
    - Load, Record, Stop Recording, Play, Stop Playback
    - Dropdown for output device
    """
    frame = tk.Frame(root, relief="ridge", bd=2)
    frame.pack(side=tk.TOP, fill=tk.X, padx=5, pady=5)

    label = tk.Label(frame, text=f"Sound {memory_index+1}")
    label.pack(side=tk.LEFT, padx=5)

    # Load button
    tk.Button(frame, text="Load", 
              command=partial(load_audio, memory_index)).pack(side=tk.LEFT, padx=2)
    # Record button
    tk.Button(frame, text="Record", 
              command=lambda: threading.Thread(
                  target=record_audio,
                  args=[memory_index, int(selected_input_device.get().split(':')[0])]
              ).start()).pack(side=tk.LEFT, padx=2)
    # Stop Recording
    tk.Button(frame, text="Stop Recording", command=stop_recording).pack(side=tk.LEFT, padx=2)

    # Play button
    tk.Button(frame, text="Play", 
              command=partial(play_audio, memory_index)).pack(side=tk.LEFT, padx=2)
    # Stop button
    tk.Button(frame, text="Stop", command=stop_audio).pack(side=tk.LEFT, padx=2)

    # Output device dropdown
    output_device_var = tk.StringVar(frame)
    output_device_var.set(output_device_names[0])  # Default to first device
    OUTPUT_DEVICES[memory_index] = output_device_var.get().split(':', 1)[1].strip()

    def on_output_device_select(value, idx=memory_index):
        # value is like "3: External Headphones"
        # parse out "External Headphones"
        device_str = value.split(':', 1)[1].strip()
        OUTPUT_DEVICES[idx] = device_str
        print(f"[Sound {idx+1}] Output device set to {device_str}")

    tk.Label(frame, text="Output:").pack(side=tk.LEFT, padx=5)
    tk.OptionMenu(frame, output_device_var, *output_device_names, 
                  command=lambda val: on_output_device_select(val, memory_index)).pack(side=tk.LEFT, padx=2)

def update_noise_threshold(value):
    """ Update the noise threshold based on the slider value. """
    global NOISE_THRESHOLD
    NOISE_THRESHOLD = int(value)
    print(f"Noise threshold set to: {NOISE_THRESHOLD}")

def main_gui():
    """ Builds and runs the main GUI window. """
    root = tk.Tk()
    root.title("Sound Control - OSC Trigger")

    tk.Label(root, text="OSC Sound Playback & Recording", font=("Helvetica", 16, "bold")).pack(pady=5)
    tk.Label(root, text=f"Listening on Port {OSC_PORT}").pack()

    # Input device dropdown (for recording)
    global selected_input_device
    selected_input_device = tk.StringVar(root)
    input_devices = list_input_devices()
    if not input_devices:
        print("No input devices found! Recording won't work.")
        input_device_names = ["0: NoInput"]
    else:
        input_device_names = [f"{i}: {name}" for i, name in input_devices]
    selected_input_device.set(input_device_names[0])

    tk.Label(root, text="Select Microphone:").pack()
    tk.OptionMenu(root, selected_input_device, *input_device_names).pack()

    # Output devices (for playback)
    global output_device_names
    out_devs = list_output_devices()
    if not out_devs:
        print("No output devices found! Playback won't work.")
        output_device_names = ["0: NoOutput"]
    else:
        output_device_names = [f"{i}: {name}" for i, name in out_devs]

    # Create 8 memory slots in the UI
    for i in range(NUM_MEMORIES):
        create_memory_ui(root, i)

    # Noise threshold slider
    tk.Label(root, text="Noise Threshold:").pack(pady=5)
    noise_threshold_slider = tk.Scale(root, from_=0, to=100, orient=tk.HORIZONTAL, command=update_noise_threshold)
    noise_threshold_slider.set(NOISE_THRESHOLD)
    noise_threshold_slider.pack()

    # Start the OSC server in the background
    threading.Thread(target=start_osc_server, daemon=True).start()

    # Main loop
    root.mainloop()

if __name__ == "__main__":
    try:
        main_gui()
    except Exception as e:
        print(f"An error occurred: {e}")
        input("Press Enter to exit...")
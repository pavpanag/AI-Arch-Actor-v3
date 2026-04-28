import os
import threading
import time
import wave
from collections import deque
from functools import partial

import numpy as np
import pygame
import sounddevice as sd
import tkinter as tk
from pythonosc import dispatcher, osc_server
from tkinter import filedialog

# -----------------------------
# CONFIG
# -----------------------------

OSC_PORT = 5555
SAMPLE_RATE = 44100
CHANNELS = 1
NUM_MEMORIES = 8
NOISE_THRESHOLD_DEFAULT = 0
PRE_ROLL_SECONDS = 2.0
RECORD_BLOCKSIZE = 1024

# -----------------------------
# APP
# -----------------------------


class SoundControlApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Sound Control - OSC Trigger (Improved)")

        self.recordings_folder = os.path.join(os.path.dirname(__file__), "recordings")
        os.makedirs(self.recordings_folder, exist_ok=True)

        self.memory_files = [None] * NUM_MEMORIES
        self.output_devices = [None] * NUM_MEMORIES
        self.noise_threshold = NOISE_THRESHOLD_DEFAULT

        self.output_device_names = []
        self.input_device_names = []

        self.selected_input_device = tk.StringVar(self.root)

        self.recording_stop_event = threading.Event()
        self.is_recording = False
        self.recording_memory_index = None
        self.recording_thread = None

        self.is_playing = False
        self.playing_memory_index = None

        self.pre_roll_max_blocks = max(1, int((PRE_ROLL_SECONDS * SAMPLE_RATE) / RECORD_BLOCKSIZE))
        self.pre_roll_buffer = deque(maxlen=self.pre_roll_max_blocks)
        self.pre_roll_lock = threading.Lock()
        self.monitor_stream = None

        pygame.mixer.init()

        self.status_var = tk.StringVar(value="Idle")
        self.record_indicator_var = tk.StringVar(value="REC: OFF")
        self.play_indicator_var = tk.StringVar(value="PLAY: OFF")

        self._build_gui()
        self._start_osc_server()
        self._start_input_monitor()
        self._poll_playback_state()

    # -----------------------------
    # Device discovery
    # -----------------------------

    @staticmethod
    def list_input_devices():
        devices = sd.query_devices()
        return [
            (i, dev["name"])
            for i, dev in enumerate(devices)
            if dev.get("max_input_channels", 0) > 0
        ]

    @staticmethod
    def list_output_devices():
        devices = sd.query_devices()
        return [
            (i, dev["name"])
            for i, dev in enumerate(devices)
            if dev.get("max_output_channels", 0) > 0
        ]

    # -----------------------------
    # GUI
    # -----------------------------

    def _build_gui(self):
        tk.Label(self.root, text="OSC Sound Playback & Recording", font=("Helvetica", 16, "bold")).pack(pady=5)
        tk.Label(self.root, text=f"Listening on Port {OSC_PORT}").pack()

        status_frame = tk.Frame(self.root, relief="groove", bd=2)
        status_frame.pack(fill=tk.X, padx=6, pady=6)

        tk.Label(status_frame, text="Status:").pack(side=tk.LEFT, padx=5)
        tk.Label(status_frame, textvariable=self.status_var, width=24, anchor="w").pack(side=tk.LEFT, padx=5)

        tk.Label(status_frame, textvariable=self.record_indicator_var, fg="red", width=10).pack(side=tk.LEFT, padx=10)
        tk.Label(status_frame, textvariable=self.play_indicator_var, fg="blue", width=10).pack(side=tk.LEFT, padx=10)

        input_devices = self.list_input_devices()
        if not input_devices:
            print("No input devices found! Recording won't work.")
            self.input_device_names = ["0: NoInput"]
        else:
            self.input_device_names = [f"{i}: {name}" for i, name in input_devices]

        self.selected_input_device.set(self.input_device_names[0])

        tk.Label(self.root, text="Select Microphone:").pack()
        tk.OptionMenu(self.root, self.selected_input_device, *self.input_device_names, command=self._on_input_device_changed).pack()

        out_devs = self.list_output_devices()
        if not out_devs:
            print("No output devices found! Playback won't work.")
            self.output_device_names = ["0: NoOutput"]
        else:
            self.output_device_names = [f"{i}: {name}" for i, name in out_devs]

        for i in range(NUM_MEMORIES):
            self._create_memory_ui(i)

        tk.Label(self.root, text="Noise Threshold:").pack(pady=5)
        slider = tk.Scale(self.root, from_=0, to=100, orient=tk.HORIZONTAL, command=self._update_noise_threshold)
        slider.set(self.noise_threshold)
        slider.pack()

    def _create_memory_ui(self, memory_index):
        frame = tk.Frame(self.root, relief="ridge", bd=2)
        frame.pack(side=tk.TOP, fill=tk.X, padx=5, pady=5)

        tk.Label(frame, text=f"Sound {memory_index + 1}").pack(side=tk.LEFT, padx=5)

        tk.Button(frame, text="Load", command=partial(self.load_audio, memory_index)).pack(side=tk.LEFT, padx=2)
        tk.Button(frame, text="Record", command=partial(self.start_recording, memory_index)).pack(side=tk.LEFT, padx=2)
        tk.Button(frame, text="Stop Recording", command=self.stop_recording).pack(side=tk.LEFT, padx=2)

        tk.Button(frame, text="Play", command=partial(self.play_audio, memory_index)).pack(side=tk.LEFT, padx=2)
        tk.Button(frame, text="Stop", command=self.stop_audio).pack(side=tk.LEFT, padx=2)

        output_device_var = tk.StringVar(frame)
        output_device_var.set(self.output_device_names[0])
        self.output_devices[memory_index] = output_device_var.get().split(":", 1)[1].strip()

        def on_output_device_select(value, idx=memory_index):
            self.output_devices[idx] = value.split(":", 1)[1].strip()
            print(f"[Sound {idx + 1}] Output device set to {self.output_devices[idx]}")

        tk.Label(frame, text="Output:").pack(side=tk.LEFT, padx=5)
        tk.OptionMenu(frame, output_device_var, *self.output_device_names, command=on_output_device_select).pack(side=tk.LEFT, padx=2)

    # -----------------------------
    # Indicators and status
    # -----------------------------

    def _set_status(self, text):
        self.status_var.set(text)

    def _set_record_indicator(self, active):
        self.record_indicator_var.set("REC: ON" if active else "REC: OFF")

    def _set_play_indicator(self, active):
        self.play_indicator_var.set("PLAY: ON" if active else "PLAY: OFF")

    def _update_noise_threshold(self, value):
        self.noise_threshold = int(value)
        print(f"Noise threshold set to: {self.noise_threshold}")

    # -----------------------------
    # Input monitor (pre-roll)
    # -----------------------------

    def _selected_input_index(self):
        return int(self.selected_input_device.get().split(":", 1)[0])

    def _monitor_callback(self, indata, frames, time_info, status):
        if status:
            print(f"[Monitor] {status}")
        with self.pre_roll_lock:
            self.pre_roll_buffer.append(indata.copy().tobytes())

    def _start_input_monitor(self):
        if self.input_device_names and self.input_device_names[0].endswith("NoInput"):
            return

        self._stop_input_monitor()
        try:
            self.monitor_stream = sd.InputStream(
                samplerate=SAMPLE_RATE,
                channels=CHANNELS,
                dtype="int16",
                blocksize=RECORD_BLOCKSIZE,
                callback=self._monitor_callback,
                device=self._selected_input_index(),
                latency="low",
            )
            self.monitor_stream.start()
            print("[Monitor] Pre-roll monitor started.")
        except Exception as exc:
            self.monitor_stream = None
            print(f"[Monitor] Could not start pre-roll monitor: {exc}")

    def _stop_input_monitor(self):
        if self.monitor_stream is not None:
            try:
                self.monitor_stream.stop()
                self.monitor_stream.close()
            except Exception:
                pass
            self.monitor_stream = None

    def _on_input_device_changed(self, _value):
        print(f"Input device changed to {self.selected_input_device.get()}")
        with self.pre_roll_lock:
            self.pre_roll_buffer.clear()
        if not self.is_recording:
            self._start_input_monitor()

    def _snapshot_pre_roll(self):
        with self.pre_roll_lock:
            return b"".join(self.pre_roll_buffer)

    # -----------------------------
    # Recording
    # -----------------------------

    def start_recording(self, memory_index):
        if self.is_recording:
            print("A recording is already in progress.")
            return

        if self.input_device_names and self.input_device_names[0].endswith("NoInput"):
            print("No input device available.")
            return

        self.stop_audio()

        self.is_recording = True
        self.recording_memory_index = memory_index
        self.recording_stop_event.clear()
        self._set_record_indicator(True)
        self._set_status(f"Recording Sound {memory_index + 1}...")

        pre_roll_bytes = self._snapshot_pre_roll()
        device_index = self._selected_input_index()

        self.recording_thread = threading.Thread(
            target=self._record_worker,
            args=(memory_index, device_index, pre_roll_bytes),
            daemon=True,
        )
        self.recording_thread.start()

    def _record_worker(self, memory_index, device_index, pre_roll_bytes):
        filename = os.path.join(self.recordings_folder, f"sound_{memory_index + 1}.wav")
        print(f"[Sound {memory_index + 1}] Recording to {filename}")

        self.memory_files[memory_index] = None
        self._stop_input_monitor()

        try:
            with wave.open(filename, "wb") as wf:
                wf.setnchannels(CHANNELS)
                wf.setsampwidth(2)
                wf.setframerate(SAMPLE_RATE)

                if pre_roll_bytes:
                    # Include recent audio so speech right before/during click is not lost.
                    wf.writeframes(pre_roll_bytes)

                def callback(indata, frames, time_info, status):
                    if status:
                        print(f"[Record] {status}")
                    if self.recording_stop_event.is_set():
                        raise sd.CallbackStop

                    gated = np.where(np.abs(indata) < self.noise_threshold, 0, indata)
                    wf.writeframes(gated.astype(np.int16).tobytes())

                with sd.InputStream(
                    samplerate=SAMPLE_RATE,
                    channels=CHANNELS,
                    dtype="int16",
                    blocksize=RECORD_BLOCKSIZE,
                    callback=callback,
                    device=device_index,
                    latency="low",
                ):
                    while not self.recording_stop_event.is_set():
                        sd.sleep(50)

            self.memory_files[memory_index] = filename
            print(f"[Sound {memory_index + 1}] Saved to {filename}")
        except Exception as exc:
            print(f"Error recording audio: {exc}")
        finally:
            self.is_recording = False
            self.recording_memory_index = None
            self.root.after(0, lambda: self._set_record_indicator(False))
            self.root.after(0, lambda: self._set_status("Idle"))
            self.root.after(0, self._start_input_monitor)

    def stop_recording(self):
        if not self.is_recording:
            return
        self.recording_stop_event.set()
        print("Recording stop requested.")

    # -----------------------------
    # Playback
    # -----------------------------

    def play_audio(self, memory_index):
        audio_file = self.memory_files[memory_index]
        output_device = self.output_devices[memory_index]

        if not audio_file or not os.path.isfile(audio_file):
            print(f"[Sound {memory_index + 1}] No valid audio file loaded.")
            return

        print(f"[Sound {memory_index + 1}] Playing: {audio_file} on device {output_device}")

        try:
            pygame.mixer.quit()
            pygame.mixer.init(devicename=output_device)
            pygame.mixer.music.load(audio_file)
            pygame.mixer.music.play()

            self.is_playing = True
            self.playing_memory_index = memory_index
            self._set_play_indicator(True)
            self._set_status(f"Playing Sound {memory_index + 1}...")
        except Exception as exc:
            print(f"Playback error: {exc}")
            self.is_playing = False
            self.playing_memory_index = None
            self._set_play_indicator(False)
            self._set_status("Idle")

    def stop_audio(self):
        try:
            pygame.mixer.music.stop()
            if hasattr(pygame.mixer.music, "unload"):
                pygame.mixer.music.unload()
            time.sleep(0.1)
        except Exception:
            pass

        self.is_playing = False
        self.playing_memory_index = None
        self._set_play_indicator(False)
        if not self.is_recording:
            self._set_status("Idle")
        print("Playback stopped.")

    def _poll_playback_state(self):
        try:
            busy = pygame.mixer.music.get_busy()
        except Exception:
            busy = False

        if self.is_playing and not busy:
            self.is_playing = False
            self.playing_memory_index = None
            self._set_play_indicator(False)
            if not self.is_recording:
                self._set_status("Idle")

        self.root.after(100, self._poll_playback_state)

    # -----------------------------
    # File load
    # -----------------------------

    def load_audio(self, memory_index):
        filepath = filedialog.askopenfilename(
            filetypes=[("Audio Files", "*.wav *.mp3 *.ogg"), ("All Files", "*.*")]
        )
        if filepath:
            self.memory_files[memory_index] = filepath
            print(f"[Sound {memory_index + 1}] Loaded: {filepath}")

    # -----------------------------
    # OSC
    # -----------------------------

    def _handle_osc(self, address, *args):
        print(f"[OSC] Received: Address={address}, Args={args}")
        if not args:
            print("[OSC] No arguments provided.")
            return

        parts = str(args[0]).split()
        if len(parts) != 2:
            print(f"[OSC] Could not parse argument: {args[0]}")
            return

        cmd, number = parts
        if cmd != "sound":
            print(f"[OSC] Unrecognized command: {cmd}")
            return

        try:
            mem_index = int(number) - 1
        except ValueError:
            print(f"[OSC] Invalid number: {number}")
            return

        if not 0 <= mem_index < NUM_MEMORIES:
            print(f"[OSC] Memory index out of range: {number}")
            return

        self.root.after(0, lambda idx=mem_index: self.play_audio(idx))

    def _start_osc_server(self):
        def run_server():
            try:
                disp = dispatcher.Dispatcher()
                disp.map("*", self._handle_osc)
                server = osc_server.ThreadingOSCUDPServer(("0.0.0.0", OSC_PORT), disp)
                print(f"[OSC] Server started on port {OSC_PORT}")
                server.serve_forever()
            except OSError as exc:
                print(f"[OSC] Server error: {exc}")

        threading.Thread(target=run_server, daemon=True).start()


# -----------------------------
# Main
# -----------------------------


def main():
    root = tk.Tk()
    SoundControlApp(root)
    root.mainloop()


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"An error occurred: {exc}")
        input("Press Enter to exit...")

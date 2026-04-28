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
import queue
from collections import deque

try:
    import soundfile as sf
except Exception:
    sf = None

try:
    from pydub import AudioSegment
except Exception:
    AudioSegment = None

# -----------------------------
# CONFIG & GLOBAL VARIABLES
# -----------------------------

OSC_PORT = 5555        # Same port as your light script
SAMPLE_RATE = 44100
NUM_MEMORIES = 8
DEBUG_RECORDING = True
DEBUG_WRITE_FILE = False
STARTUP_SILENCE_ABS_THRESHOLD = 100
RECORD_BLOCKSIZE = 1024
RECORD_QUEUE_MAX_BLOCKS = 512
PREBUFFER_SECONDS = 0
TRIM_LEADING_SILENCE = True
MEANINGFUL_START_ABS_THRESHOLD = 350
MEANINGFUL_MIN_DURATION_MS = 25
MEANINGFUL_PREROLL_MS = 80
ALLOW_PYGAME_FALLBACK = False

# Holds file paths for each slot (sound_1.wav, sound_2.wav, etc.)
MEMORY_FILES = [None] * NUM_MEMORIES
MEMORY_LOADED_LABEL_VARS = [None] * NUM_MEMORIES
LAST_RECORDED_FILE = None

# Holds which output device index (as a string) each memory uses
OUTPUT_DEVICES = [None] * NUM_MEMORIES
OUTPUT_START_CHANNELS = [1] * NUM_MEMORIES
OUTPUT_VOLUMES = [1.0] * NUM_MEMORIES

NOISE_THRESHOLD = 0   # Adjust this for the noise gate
is_recording = False   # Flag to stop the recording loop
selected_input_device = None
last_logged_threshold = None
is_playing = False

# Tkinter status vars
ui_root = None
status_var = None
record_var = None
play_var = None
master_record_target_var = None
master_record_name_var = None
last_recorded_label_var = None

# Rolling prebuffer (memory only). It is written to disk only after Record is pressed.
prebuffer_blocks = deque(maxlen=max(1, int((PREBUFFER_SECONDS * SAMPLE_RATE) / RECORD_BLOCKSIZE)))
prebuffer_lock = threading.Lock()
monitor_stream = None

# For listing devices in GUI
output_device_names = []
output_device_max_channels = {}

# Initialize Pygame for playback
pygame.mixer.init()

# Create recordings folder if it doesn't exist
RECORDINGS_FOLDER = os.path.join(os.path.dirname(__file__), "recordings")
os.makedirs(RECORDINGS_FOLDER, exist_ok=True)

def debug_log(message):
    """Debug logger for recording timing diagnostics."""
    if DEBUG_RECORDING:
        print(f"[REC-DEBUG] {message}")

def trim_leading_silence_in_wav(filename):
    """Trim leading silence so playback starts near first meaningful content."""
    if not os.path.isfile(filename):
        return 0

    try:
        with wave.open(filename, 'rb') as rf:
            channels = rf.getnchannels()
            sampwidth = rf.getsampwidth()
            framerate = rf.getframerate()
            nframes = rf.getnframes()
            raw = rf.readframes(nframes)

        if sampwidth != 2 or framerate <= 0 or nframes == 0:
            return 0

        samples = np.frombuffer(raw, dtype=np.int16)
        if samples.size == 0:
            return 0

        if channels > 1:
            reshaped = samples.reshape(-1, channels)
            mono_abs = np.max(np.abs(reshaped), axis=1)
        else:
            mono_abs = np.abs(samples)

        # Track user noise gate but ensure a meaningful floor for speech onset.
        effective_threshold = max(MEANINGFUL_START_ABS_THRESHOLD, NOISE_THRESHOLD + 100)
        min_samples = max(1, int((MEANINGFUL_MIN_DURATION_MS / 1000.0) * framerate))
        preroll_samples = max(0, int((MEANINGFUL_PREROLL_MS / 1000.0) * framerate))

        above = mono_abs > effective_threshold
        run = 0
        first_meaningful = None
        for i, is_above in enumerate(above):
            if is_above:
                run += 1
                if run >= min_samples:
                    first_meaningful = i - min_samples + 1
                    break
            else:
                run = 0

        if first_meaningful is None:
            debug_log("Trim skipped: no meaningful start detected.")
            return 0

        cut_frame = max(0, first_meaningful - preroll_samples)
        if cut_frame <= 0:
            return 0

        start_sample = cut_frame * channels
        trimmed = samples[start_sample:]
        if trimmed.size == 0:
            return 0

        with wave.open(filename, 'wb') as wf:
            wf.setnchannels(channels)
            wf.setsampwidth(sampwidth)
            wf.setframerate(framerate)
            wf.writeframes(trimmed.tobytes())

        trimmed_ms = int((cut_frame / framerate) * 1000)
        return trimmed_ms
    except Exception as e:
        debug_log(f"Trim failed: {e}")
        return 0

def monitor_input_callback(indata, frames, time_info, status):
    """Continuously capture recent microphone audio in memory only."""
    if status:
        debug_log(f"Monitor status: {status}")
    with prebuffer_lock:
        prebuffer_blocks.append(bytes(indata))

def stop_input_monitor():
    global monitor_stream
    if monitor_stream is not None:
        try:
            monitor_stream.stop()
            monitor_stream.close()
        except Exception:
            pass
        monitor_stream = None

def start_input_monitor():
    """Start/refresh prebuffer monitor on current selected input device."""
    global monitor_stream
    if selected_input_device is None:
        return

    selected = selected_input_device.get()
    if "NoInput" in selected:
        return

    stop_input_monitor()
    with prebuffer_lock:
        prebuffer_blocks.clear()

    try:
        device_index = int(selected.split(':')[0])
        monitor_stream = sd.RawInputStream(
            samplerate=SAMPLE_RATE,
            channels=1,
            dtype='int16',
            callback=monitor_input_callback,
            device=device_index,
            blocksize=RECORD_BLOCKSIZE,
            latency='low'
        )
        monitor_stream.start()
        debug_log(f"Prebuffer monitor started ({PREBUFFER_SECONDS:.1f}s)")
    except Exception as e:
        monitor_stream = None
        debug_log(f"Prebuffer monitor failed: {e}")

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
    """ Return a list of (index, name, max_output_channels) for output-capable devices. """
    devices = sd.query_devices()
    output_devices = [
        (i, device['name'], int(device.get('max_output_channels', 0)))
        for i, device in enumerate(devices)
        if device.get('max_output_channels', 0) > 0
    ]
    return output_devices

def parse_output_device_index(device_value):
    """Parse option value like '3: Device Name' to int index, or None if invalid."""
    try:
        return int(device_value.split(':', 1)[0].strip())
    except Exception:
        return None

def get_output_max_channels(device_index):
    if device_index is None:
        return 0
    return int(output_device_max_channels.get(device_index, 0))

def sanitize_filename_piece(text):
    """Convert arbitrary text to a safe filename piece."""
    if text is None:
        return ""
    safe = "".join(c if c.isalnum() or c in ('-', '_') else '_' for c in text.strip())
    safe = safe.strip('_')
    while '__' in safe:
        safe = safe.replace('__', '_')
    return safe[:48]

def get_display_name_for_path(path):
    if not path:
        return "(empty)"
    return os.path.basename(path)

def refresh_loaded_label(memory_index):
    label_var = MEMORY_LOADED_LABEL_VARS[memory_index]
    if label_var is not None:
        label_var.set(get_display_name_for_path(MEMORY_FILES[memory_index]))

def set_memory_file(memory_index, filepath, source_text="Loaded"):
    MEMORY_FILES[memory_index] = filepath
    refresh_loaded_label(memory_index)
    print(f"[Sound {memory_index+1}] {source_text}: {filepath}")

def refresh_last_recorded_label():
    if last_recorded_label_var is not None:
        if LAST_RECORDED_FILE and os.path.isfile(LAST_RECORDED_FILE):
            last_recorded_label_var.set(os.path.basename(LAST_RECORDED_FILE))
        else:
            last_recorded_label_var.set("(none)")

def decode_audio_to_mono_int16(file_path):
    """
    Decode audio (wav/mp3/ogg/...) to mono int16 numpy array + sample rate.
    Decoder order:
      1) soundfile (if installed; supports 24-bit WAV and many formats)
      2) pydub/ffmpeg (if installed)
      3) wave module (16-bit WAV fallback)
    """
    ext = os.path.splitext(file_path)[1].lower()

    # soundfile supports many formats (depending on libsndfile build), including 24-bit WAV.
    if sf is not None:
        try:
            data, rate = sf.read(file_path, dtype='float32', always_2d=True)
            if data.size > 0:
                mono = np.mean(data, axis=1)
                pcm = np.clip(mono * 32767.0, -32768, 32767).astype(np.int16)
                return pcm, int(rate)
        except Exception as e:
            debug_log(f"soundfile decode failed: {e}")

    # pydub fallback (requires ffmpeg/avlib in PATH).
    if AudioSegment is not None:
        try:
            seg = AudioSegment.from_file(file_path)
            seg = seg.set_channels(1).set_sample_width(2)
            mono = np.frombuffer(seg.raw_data, dtype=np.int16)
            return mono, int(seg.frame_rate)
        except Exception as e:
            debug_log(f"pydub decode failed: {e}")

    # Final fallback: standard-library WAV path (16-bit PCM only).
    if ext == ".wav":
        with wave.open(file_path, 'rb') as rf:
            channels = rf.getnchannels()
            rate = rf.getframerate()
            width = rf.getsampwidth()
            data = rf.readframes(rf.getnframes())

        if width != 2:
            raise ValueError(
                f"Unsupported WAV sample width: {width} bytes (install soundfile for 24-bit WAV support)"
            )

        samples = np.frombuffer(data, dtype=np.int16)
        if channels > 1:
            samples = samples.reshape(-1, channels)
            mono = np.mean(samples.astype(np.float32), axis=1)
        else:
            mono = samples.astype(np.float32)

        return np.clip(mono, -32768, 32767).astype(np.int16), int(rate)

    raise RuntimeError(
        "No decoder available for this file. Install either 'soundfile' or 'pydub' (plus ffmpeg) for mp3/ogg support."
    )

def generate_unique_recording_path(memory_index, recording_name=None):
    """Create readable, collision-safe recording filename for a memory slot."""
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    clean_name = sanitize_filename_piece(recording_name)
    if clean_name:
        base_name = f"sound_{memory_index+1}_{clean_name}_{timestamp}"
    else:
        base_name = f"sound_{memory_index+1}_{timestamp}"
    filename = os.path.join(RECORDINGS_FOLDER, f"{base_name}.wav")

    # If two takes happen within the same second, append an incrementing suffix.
    counter = 1
    while os.path.exists(filename):
        filename = os.path.join(RECORDINGS_FOLDER, f"{base_name}_{counter:02d}.wav")
        counter += 1

    return filename

def record_audio(memory_index, device_index, recording_name=None):
    """
    Records audio from the selected input device, applying a basic noise gate,
    and saves it to a WAV file (sound_N.wav).
    """
    global is_recording
    is_recording = True
    if selected_input_device is not None and "NoInput" in selected_input_device.get():
        print("No input device found. Recording canceled.")
        is_recording = False
        set_recording_indicator(False)
        set_status("Idle")
        return
    set_recording_indicator(False)
    set_status(f"Starting recording Sound {memory_index+1}...")
    global LAST_RECORDED_FILE
    filename = generate_unique_recording_path(memory_index, recording_name)
    print(f"[Sound {memory_index+1}] Recording to {filename}... (Stop by clicking 'Stop Recording')")
    t_button = time.perf_counter()
    t_stream_started = None
    t_first_callback = None
    t_first_non_silent = None
    callback_count = 0
    total_frames = 0
    peak_abs = 0
    selected_device_text = selected_input_device.get() if selected_input_device is not None else str(device_index)

    debug_log(f"Button pressed for Sound {memory_index+1}")
    debug_log(f"Input device: {selected_device_text}")
    debug_log(f"Sample rate: {SAMPLE_RATE}, channels: 1")

    # Stop any playback and unload the sound file
    stop_audio()
    MEMORY_FILES[memory_index] = None
    refresh_loaded_label(memory_index)

    # Snapshot rolling prebuffer and then stop monitor so recording stream can use device.
    with prebuffer_lock:
        prebuffer_chunks = list(prebuffer_blocks)
    stop_input_monitor()

    prebuffer_samples_written = 0

    callback_started = False
    dropped_blocks = 0
    overflow_status_count = 0
    audio_queue = queue.Queue(maxsize=RECORD_QUEUE_MAX_BLOCKS)

    def callback(indata, frames, time_info, status):
        nonlocal callback_started
        nonlocal t_first_callback
        nonlocal callback_count, dropped_blocks, overflow_status_count
        if status:
            overflow_status_count += 1
            debug_log(f"Callback status: {status}")
        if not is_recording:
            raise sd.CallbackStop

        callback_count += 1

        if t_first_callback is None:
            t_first_callback = time.perf_counter()
            debug_log(f"First callback at +{(t_first_callback - t_button)*1000:.1f} ms from button press")

        if not callback_started:
            callback_started = True
            set_recording_indicator(True)
            set_status(f"Recording Sound {memory_index+1}...")

        try:
            # Keep callback light: enqueue raw bytes and do processing in writer loop.
            audio_queue.put_nowait(bytes(indata))
        except queue.Full:
            dropped_blocks += 1

    try:
        with wave.open(filename, 'wb') as wf:
            wf.setnchannels(1)
            wf.setsampwidth(2)  # 2 bytes per sample (int16)
            wf.setframerate(SAMPLE_RATE)

            # Write prebuffer only now (nothing is persisted before Record is pressed).
            for chunk in prebuffer_chunks:
                pre_samples = np.frombuffer(chunk, dtype=np.int16)
                if pre_samples.size == 0:
                    continue
                if NOISE_THRESHOLD > 0:
                    pre_samples = np.where(np.abs(pre_samples) < NOISE_THRESHOLD, 0, pre_samples).astype(np.int16)
                wf.writeframesraw(pre_samples.tobytes())
                prebuffer_samples_written += pre_samples.size

            debug_log(
                f"Prebuffer appended: {prebuffer_samples_written / SAMPLE_RATE:.3f}s "
                f"({prebuffer_samples_written} samples)"
            )

            debug_log("Opening input stream...")
            with sd.RawInputStream(
                samplerate=SAMPLE_RATE,
                channels=1,
                dtype='int16',
                callback=callback,
                device=device_index,
                blocksize=RECORD_BLOCKSIZE,
                latency='low'
            ):
                t_stream_started = time.perf_counter()
                debug_log(f"Input stream started at +{(t_stream_started - t_button)*1000:.1f} ms")

                # Write queued audio in this thread to avoid disk I/O work in callback.
                while is_recording or not audio_queue.empty():
                    try:
                        chunk = audio_queue.get(timeout=0.05)
                    except queue.Empty:
                        continue

                    samples = np.frombuffer(chunk, dtype=np.int16)
                    if samples.size == 0:
                        continue

                    current_peak = int(np.max(np.abs(samples)))
                    peak_abs = max(peak_abs, current_peak)
                    total_frames += samples.size

                    if t_first_non_silent is None and current_peak > STARTUP_SILENCE_ABS_THRESHOLD:
                        t_first_non_silent = time.perf_counter()
                        debug_log(
                            f"First non-silent input (peak {current_peak}) at +{(t_first_non_silent - t_button)*1000:.1f} ms"
                        )

                    if NOISE_THRESHOLD > 0:
                        samples = np.where(np.abs(samples) < NOISE_THRESHOLD, 0, samples).astype(np.int16)

                    wf.writeframesraw(samples.tobytes())

        LAST_RECORDED_FILE = filename
        refresh_last_recorded_label()
        set_memory_file(memory_index, filename, source_text="Saved")

        trimmed_ms = 0
        if TRIM_LEADING_SILENCE:
            trimmed_ms = trim_leading_silence_in_wav(filename)
            if trimmed_ms > 0:
                debug_log(f"Leading silence trimmed: {trimmed_ms} ms")

        print(f"[Sound {memory_index+1}] Saved to {filename}")

    except Exception as e:
        print(f"Error recording audio: {e}")
        is_recording = False
    finally:
        t_end = time.perf_counter()
        duration_s = max(0.0, t_end - t_button)
        captured_s = total_frames / SAMPLE_RATE if SAMPLE_RATE else 0.0
        first_callback_ms = (t_first_callback - t_button) * 1000 if t_first_callback is not None else None
        first_non_silent_ms = (t_first_non_silent - t_button) * 1000 if t_first_non_silent is not None else None
        stream_start_ms = (t_stream_started - t_button) * 1000 if t_stream_started is not None else None

        debug_lines = [
            f"sound_index={memory_index+1}",
            f"wav_path={filename}",
            f"device={selected_device_text}",
            f"duration_wall_seconds={duration_s:.3f}",
            f"duration_captured_seconds={captured_s:.3f}",
            f"stream_started_ms_from_button={stream_start_ms if stream_start_ms is not None else 'N/A'}",
            f"first_callback_ms_from_button={first_callback_ms if first_callback_ms is not None else 'N/A'}",
            f"first_non_silent_ms_from_button={first_non_silent_ms if first_non_silent_ms is not None else 'N/A'}",
            f"callback_count={callback_count}",
            f"total_frames={total_frames}",
            f"max_peak_abs={peak_abs}",
            f"callback_overflow_status_count={overflow_status_count}",
            f"queue_dropped_blocks={dropped_blocks}",
            f"prebuffer_samples_written={prebuffer_samples_written}",
            f"prebuffer_seconds_written={prebuffer_samples_written / SAMPLE_RATE if SAMPLE_RATE else 0.0}",
            f"trim_leading_silence={TRIM_LEADING_SILENCE}",
            f"meaningful_start_threshold={max(MEANINGFUL_START_ABS_THRESHOLD, NOISE_THRESHOLD + 100)}",
            f"meaningful_min_duration_ms={MEANINGFUL_MIN_DURATION_MS}",
            f"meaningful_preroll_ms={MEANINGFUL_PREROLL_MS}",
            f"noise_threshold={NOISE_THRESHOLD}",
        ]

        for line in debug_lines:
            debug_log(line)

        if DEBUG_WRITE_FILE:
            try:
                debug_path = filename + ".debug.txt"
                with open(debug_path, "w", encoding="utf-8") as f:
                    f.write("\n".join(debug_lines) + "\n")
                debug_log(f"Wrote debug report: {debug_path}")
            except Exception as debug_e:
                print(f"Could not write debug report: {debug_e}")

        set_recording_indicator(False)
        set_status("Idle")
        if ui_root is not None:
            ui_root.after(0, start_input_monitor)

def stop_recording():
    """ Stops the recording callback. """
    global is_recording
    is_recording = False
    print("Recording stopped.")

def load_audio(memory_index):
    """ Let user pick a file to assign to memory slot. """
    os.makedirs(RECORDINGS_FOLDER, exist_ok=True)
    filepath = filedialog.askopenfilename(
        initialdir=os.path.abspath(RECORDINGS_FOLDER),
        title="Load Recording",
        filetypes=[("Audio Files", "*.wav *.mp3 *.ogg"), ("All Files", "*.*")]
    )
    if filepath:
        set_memory_file(memory_index, filepath, source_text="Loaded")

def get_selected_master_memory_index():
    if master_record_target_var is None:
        return 0
    try:
        # Value format: "Sound 3"
        return max(0, min(NUM_MEMORIES - 1, int(master_record_target_var.get().split()[-1]) - 1))
    except Exception:
        return 0

def start_master_recording():
    target_index = get_selected_master_memory_index()
    rec_name = master_record_name_var.get().strip() if master_record_name_var is not None else ""
    threading.Thread(
        target=record_audio,
        args=[target_index, int(selected_input_device.get().split(':')[0]), rec_name],
        daemon=True
    ).start()

def assign_last_recording_to_selected_memory():
    if not LAST_RECORDED_FILE or not os.path.isfile(LAST_RECORDED_FILE):
        print("No recent recording available to assign.")
        return
    target_index = get_selected_master_memory_index()
    set_memory_file(target_index, LAST_RECORDED_FILE, source_text="Assigned")

def play_audio(memory_index):
    """
    Plays the file in MEMORY_FILES[memory_index],
    using the device set in OUTPUT_DEVICES[memory_index].
    """
    audio_file = MEMORY_FILES[memory_index]
    output_device = OUTPUT_DEVICES[memory_index]
    start_channel = int(OUTPUT_START_CHANNELS[memory_index])
    volume = float(OUTPUT_VOLUMES[memory_index])

    if not audio_file or not os.path.isfile(audio_file):
        print(f"[Sound {memory_index+1}] No valid audio file loaded.")
        return

    global is_playing
    print(f"[Sound {memory_index+1}] Playing: {audio_file} on device {output_device}")

    # Unified routed playback for all supported formats.
    if output_device is not None:
        try:
            mono, source_rate = decode_audio_to_mono_int16(audio_file)

            # Apply per-slot volume and clip safely.
            mono = np.clip(mono.astype(np.float32) * volume, -32768, 32767).astype(np.int16)

            max_out = get_output_max_channels(output_device)
            if max_out <= 0:
                raise ValueError("Selected output device has no output channels")

            ch0 = max(0, min(start_channel - 1, max_out - 1))
            out = np.zeros((mono.shape[0], max_out), dtype=np.int16)
            out[:, ch0] = mono
            route_text = f"channel {ch0+1}"

            is_playing = True
            set_play_indicator(True)
            set_status(f"Playing Sound {memory_index+1}...")
            debug_log(f"Routed playback to device {output_device}, {route_text}, volume={volume:.2f}")

            def _play_sd_buffer(buffer, sr, dev):
                global is_playing
                try:
                    sd.play(buffer, samplerate=sr, device=dev, blocking=True)
                except Exception as e:
                    print(f"[Sound {memory_index+1}] Routed playback error: {e}")
                finally:
                    is_playing = False
                    set_play_indicator(False)
                    if not is_recording:
                        set_status("Idle")

            threading.Thread(target=_play_sd_buffer, args=(out, source_rate, output_device), daemon=True).start()
            return
        except Exception as e:
            print(f"[Sound {memory_index+1}] Routed decoded playback failed: {e}")
            if not ALLOW_PYGAME_FALLBACK:
                print(f"[Sound {memory_index+1}] Playback stopped (pygame fallback disabled to preserve line routing).")
                is_playing = False
                set_play_indicator(False)
                if not is_recording:
                    set_status("Idle")
                return

    # Fallback for non-WAV formats or routing errors.
    try:
        if pygame.mixer.get_init():
            pygame.mixer.quit()
        pygame.mixer.init(devicename=str(output_device))  # Some OSes may ignore devicename.
    except Exception as e:
        print(f"[Sound {memory_index+1}] Mixer init with selected device failed: {e}")
        if not pygame.mixer.get_init():
            pygame.mixer.init()

    pygame.mixer.music.load(audio_file)
    pygame.mixer.music.set_volume(max(0.0, min(1.0, volume)))
    pygame.mixer.music.play()
    is_playing = True
    set_play_indicator(True)
    set_status(f"Playing Sound {memory_index+1}...")

def stop_audio():
    """ Stops all playback and unloads any playing file. """
    global is_playing
    try:
        sd.stop()
        if not pygame.mixer.get_init():
            is_playing = False
            set_play_indicator(False)
            if not is_recording:
                set_status("Idle")
            return
        pygame.mixer.music.stop()
        # Explicitly unload the music to release the file handle
        if hasattr(pygame.mixer.music, "unload"):
            pygame.mixer.music.unload()
        is_playing = False
        set_play_indicator(False)
        if not is_recording:
            set_status("Idle")
        print("Playback stopped.")
        # Give Windows a moment to release the file handle
        time.sleep(0.2)
    except Exception as e:
        print(f"Playback stop warning: {e}")

def on_input_device_change(_value):
    if not is_recording:
        start_input_monitor()

def set_status(text):
    if ui_root is not None and status_var is not None:
        ui_root.after(0, lambda: status_var.set(text))

def set_recording_indicator(active):
    if ui_root is not None and record_var is not None:
        ui_root.after(0, lambda: record_var.set("REC: ON" if active else "REC: OFF"))

def set_play_indicator(active):
    if ui_root is not None and play_var is not None:
        ui_root.after(0, lambda: play_var.set("PLAY: ON" if active else "PLAY: OFF"))

def poll_playback_status():
    """Update PLAY indicator when a track ends naturally."""
    global is_playing
    try:
        busy = pygame.mixer.music.get_busy() if pygame.mixer.get_init() else False
    except Exception:
        busy = False

    if is_playing and not busy:
        is_playing = False
        set_play_indicator(False)
        if not is_recording:
            set_status("Idle")

    if ui_root is not None:
        ui_root.after(120, poll_playback_status)

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
    - Load, Play, Stop Playback
    - Dropdown for output device
    """
    frame = tk.Frame(root, relief="ridge", bd=2)
    frame.pack(side=tk.TOP, fill=tk.X, padx=5, pady=5)

    label = tk.Label(frame, text=f"Sound {memory_index+1}")
    label.pack(side=tk.LEFT, padx=5)

    # Load button
    tk.Button(frame, text="Load", 
              command=partial(load_audio, memory_index)).pack(side=tk.LEFT, padx=2)

    # Play button
    tk.Button(frame, text="Play", 
              command=partial(play_audio, memory_index)).pack(side=tk.LEFT, padx=2)
    # Stop button
    tk.Button(frame, text="Stop", command=stop_audio).pack(side=tk.LEFT, padx=2)

    # Output device dropdown
    output_device_var = tk.StringVar(frame)
    output_device_var.set(output_device_names[0])  # Default to first device
    default_device_index = parse_output_device_index(output_device_var.get())
    OUTPUT_DEVICES[memory_index] = default_device_index
    default_max_channels = max(1, get_output_max_channels(default_device_index))
    OUTPUT_START_CHANNELS[memory_index] = 1
    OUTPUT_VOLUMES[memory_index] = 1.0

    def on_output_device_select(value, idx=memory_index):
        dev_idx = parse_output_device_index(value)
        OUTPUT_DEVICES[idx] = dev_idx
        max_ch = max(1, get_output_max_channels(dev_idx))
        channel_spin.config(to=max_ch)
        current = int(OUTPUT_START_CHANNELS[idx])
        if current > max_ch:
            OUTPUT_START_CHANNELS[idx] = 1
            channel_var.set("1")
        print(f"[Sound {idx+1}] Output device set to index {dev_idx} (max channels: {max_ch})")

    def on_output_channel_change(*_args, idx=memory_index):
        try:
            dev_idx = OUTPUT_DEVICES[idx]
            max_ch = max(1, get_output_max_channels(dev_idx))
            ch = int(channel_var.get())
            ch = max(1, min(ch, max_ch))
            OUTPUT_START_CHANNELS[idx] = ch
            if str(ch) != channel_var.get():
                channel_var.set(str(ch))
        except Exception:
            OUTPUT_START_CHANNELS[idx] = 1
            channel_var.set("1")

    def on_volume_change(value, idx=memory_index):
        try:
            vol = float(value)
            vol = max(0.0, min(1.0, vol))
            OUTPUT_VOLUMES[idx] = vol
        except Exception:
            OUTPUT_VOLUMES[idx] = 1.0

    tk.Label(frame, text="Output:").pack(side=tk.LEFT, padx=5)
    tk.OptionMenu(frame, output_device_var, *output_device_names, 
                  command=lambda val: on_output_device_select(val, memory_index)).pack(side=tk.LEFT, padx=2)

    tk.Label(frame, text="Line:").pack(side=tk.LEFT, padx=5)
    channel_var = tk.StringVar(frame, value="1")
    channel_spin = tk.Spinbox(frame, from_=1, to=default_max_channels, width=4, textvariable=channel_var)
    channel_spin.pack(side=tk.LEFT, padx=2)
    channel_var.trace_add("write", lambda *_: on_output_channel_change(idx=memory_index))

    tk.Label(frame, text="Vol:").pack(side=tk.LEFT, padx=5)
    volume_slider = tk.Scale(
        frame,
        from_=0,
        to=100,
        orient=tk.HORIZONTAL,
        length=100,
        showvalue=False,
        command=lambda v: on_volume_change(float(v) / 100.0, memory_index)
    )
    volume_slider.set(100)
    volume_slider.pack(side=tk.LEFT, padx=2)

    loaded_var = tk.StringVar(frame, value="(empty)")
    MEMORY_LOADED_LABEL_VARS[memory_index] = loaded_var
    tk.Label(frame, text="Loaded:").pack(side=tk.LEFT, padx=5)
    tk.Label(frame, textvariable=loaded_var, width=22, anchor="w").pack(side=tk.LEFT, padx=2)
    refresh_loaded_label(memory_index)

def update_noise_threshold(value):
    """ Update the noise threshold based on the slider value. """
    global NOISE_THRESHOLD, last_logged_threshold
    NOISE_THRESHOLD = int(value)
    # Slider callback fires on every tiny movement; throttle log output.
    if last_logged_threshold is None or abs(NOISE_THRESHOLD - last_logged_threshold) >= 5:
        print(f"Noise threshold set to: {NOISE_THRESHOLD}")
        last_logged_threshold = NOISE_THRESHOLD

def main_gui():
    """ Builds and runs the main GUI window. """
    root = tk.Tk()
    root.title("Sound Control - OSC Trigger")

    global ui_root, status_var, record_var, play_var
    ui_root = root
    status_var = tk.StringVar(value="Idle")
    record_var = tk.StringVar(value="REC: OFF")
    play_var = tk.StringVar(value="PLAY: OFF")

    tk.Label(root, text="OSC Sound Playback & Recording", font=("Helvetica", 16, "bold")).pack(pady=5)
    tk.Label(root, text=f"Listening on Port {OSC_PORT}").pack()

    status_frame = tk.Frame(root, relief="groove", bd=2)
    status_frame.pack(fill=tk.X, padx=6, pady=6)
    tk.Label(status_frame, text="Status:").pack(side=tk.LEFT, padx=5)
    tk.Label(status_frame, textvariable=status_var, width=22, anchor="w").pack(side=tk.LEFT, padx=5)
    tk.Label(status_frame, textvariable=record_var, fg="red", width=10).pack(side=tk.LEFT, padx=8)
    tk.Label(status_frame, textvariable=play_var, fg="blue", width=10).pack(side=tk.LEFT, padx=8)

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
    tk.OptionMenu(root, selected_input_device, *input_device_names, command=on_input_device_change).pack()

    # Master recording controls: one central recording function with separate assignment.
    master_frame = tk.Frame(root, relief="groove", bd=2)
    master_frame.pack(fill=tk.X, padx=6, pady=6)
    tk.Label(master_frame, text="Main Recording:").pack(side=tk.LEFT, padx=5)

    global master_record_target_var, master_record_name_var, last_recorded_label_var
    master_record_target_var = tk.StringVar(root, value="Sound 1")
    slot_names = [f"Sound {i+1}" for i in range(NUM_MEMORIES)]

    master_record_name_var = tk.StringVar(root, value="main_take")
    tk.Label(master_frame, text="Name:").pack(side=tk.LEFT, padx=5)
    tk.Entry(master_frame, textvariable=master_record_name_var, width=18).pack(side=tk.LEFT, padx=3)

    tk.Button(master_frame, text="Record", command=start_master_recording).pack(side=tk.LEFT, padx=3)
    tk.Button(master_frame, text="Stop Recording", command=stop_recording).pack(side=tk.LEFT, padx=3)

    tk.Label(master_frame, text="Last:").pack(side=tk.LEFT, padx=5)
    last_recorded_label_var = tk.StringVar(value="(none)")
    tk.Label(master_frame, textvariable=last_recorded_label_var, width=22, anchor="w").pack(side=tk.LEFT, padx=3)

    tk.Label(master_frame, text="Assign To:").pack(side=tk.LEFT, padx=5)
    tk.OptionMenu(master_frame, master_record_target_var, *slot_names).pack(side=tk.LEFT, padx=3)
    tk.Button(master_frame, text="Assign Last To Slot", command=assign_last_recording_to_selected_memory).pack(side=tk.LEFT, padx=3)
    refresh_last_recorded_label()

    # Output devices (for playback)
    global output_device_names
    out_devs = list_output_devices()
    if not out_devs:
        print("No output devices found! Playback won't work.")
        output_device_names = ["0: NoOutput"]
        output_device_max_channels[0] = 0
    else:
        output_device_names = [f"{i}: {name}" for i, name, _ in out_devs]
        output_device_max_channels.clear()
        for i, _name, max_ch in out_devs:
            output_device_max_channels[i] = max_ch

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

    # Start prebuffer monitor; this keeps recent audio in memory only.
    start_input_monitor()

    # Keep playback indicator in sync if a track ends naturally.
    poll_playback_status()

    # Main loop
    root.mainloop()

if __name__ == "__main__":
    try:
        main_gui()
    except Exception as e:
        print(f"An error occurred: {e}")
        input("Press Enter to exit...")
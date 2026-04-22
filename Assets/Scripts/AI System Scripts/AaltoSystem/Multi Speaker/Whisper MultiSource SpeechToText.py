"""
Whisper Multi-Source -> OSC Transcriber (Tkinter GUI)

What it does:
- Captures up to 4 microphone sources simultaneously
- Uses Faster-Whisper for offline speech-to-text
- Sends finalized transcripts via OSC with speaker identity

Default OSC payload:
    address: /speech
    args: (string speaker_id, string text)

Optional payload mode:
    args: (int channel, string text)
"""

from __future__ import annotations

import os
import queue
import threading
import socket
import struct
import tkinter as tk
import sys
from tkinter.scrolledtext import ScrolledText
from collections.abc import Sequence

# Work around duplicate OpenMP runtime loading on some Windows Python setups.
os.environ.setdefault("KMP_DUPLICATE_LIB_OK", "TRUE")
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")


def require(module_name: str, pip_name: str | None = None) -> None:
    try:
        __import__(module_name)
    except Exception as e:
        name = pip_name or module_name
        print(f"\n[Dependency missing] '{module_name}' failed to import.")
        print("Install with THIS python:")
        print(f"  {sys.executable} -m pip install --upgrade pip")
        print(f"  {sys.executable} -m pip install {name}")
        print("\nError details:")
        print(f"  {e}\n")
        raise SystemExit(1)


require("sounddevice")
require("numpy")
require("faster_whisper", "faster-whisper")

import numpy as np
import sounddevice as sd
from faster_whisper import WhisperModel


def _osc_pad4(n: int) -> int:
    return (4 - (n % 4)) % 4


def _osc_pack_str(s: str) -> bytes:
    b = (s or "").encode("utf-8") + b"\x00"
    return b + (b"\x00" * _osc_pad4(len(b)))


def _osc_pack_int(i: int) -> bytes:
    return struct.pack(">i", int(i))


def _osc_type_tag_for(value: object) -> str:
    if isinstance(value, bool):
        return "i"
    if isinstance(value, int):
        return "i"
    if isinstance(value, float):
        return "f"
    return "s"


def _osc_pack_arg(value: object) -> bytes:
    if isinstance(value, bool):
        value = int(value)
    if isinstance(value, int):
        return _osc_pack_int(value)
    if isinstance(value, float):
        return struct.pack(">f", float(value))
    return _osc_pack_str("" if value is None else str(value))


def build_osc_packet(address: str, args: Sequence[object]) -> bytes:
    addr = _osc_pack_str(address or "/")
    type_tags = "," + "".join(_osc_type_tag_for(arg) for arg in args)
    return addr + _osc_pack_str(type_tags) + b"".join(_osc_pack_arg(arg) for arg in args)


def send_osc(ip: str, port: int, address: str, args: Sequence[object]) -> None:
    data = build_osc_packet(address, args)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.sendto(data, (ip, int(port)))


SAMPLE_RATE = 16000
BYTES_PER_SAMPLE = 2
SOURCE_COUNT = 4


class SourceState:
    def __init__(self) -> None:
        self.enabled = tk.BooleanVar(value=False)
        self.speaker_id = tk.StringVar(value="")
        self.channel = tk.IntVar(value=1)
        self.mic = tk.StringVar(value="")
        self.audio_q: "queue.Queue[bytes]" = queue.Queue()
        self.stream: sd.InputStream | None = None
        self.last_sent_text: str = ""


class WhisperMultiSourceApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("Whisper Multi-Source -> OSC")
        self.root.geometry("1040x800")

        self.running = False
        self.model: WhisperModel | None = None
        self.model_lock = threading.Lock()
        self.run_token = 0
        self.sources: list[SourceState] = [SourceState() for _ in range(SOURCE_COUNT)]

        for i, src in enumerate(self.sources, start=1):
            src.speaker_id.set(f"actor_{i}")
            src.channel.set(i)
            src.enabled.set(i <= 2)

        self.osc_ip = tk.StringVar(value="127.0.0.1")
        self.osc_port = tk.StringVar(value="3333")
        self.osc_address = tk.StringVar(value="/speech")
        self.payload_mode = tk.StringVar(value="speaker_text")

        self.model_name = tk.StringVar(value="medium.en")
        self.device_var = tk.StringVar(value="cuda")
        self.compute_var = tk.StringVar(value="float16")
        self.lang = tk.StringVar(value="en")
        self.beam_size = tk.IntVar(value=2)
        self.chunk_seconds = tk.IntVar(value=2)
        self.low_latency = tk.BooleanVar(value=True)
        self.overlap_ms = tk.IntVar(value=600)
        self.min_rms = tk.StringVar(value="0.010")
        self.no_speech_threshold = tk.StringVar(value="0.60")
        self.vad_filter = tk.BooleanVar(value=False)

        self._build_ui()
        self.refresh_mics()

    def _build_ui(self) -> None:
        cfg = tk.Frame(self.root)
        cfg.pack(fill=tk.X, padx=10, pady=8)

        tk.Label(cfg, text="Whisper model (name or path):").grid(row=0, column=0, sticky="w")
        tk.Entry(cfg, textvariable=self.model_name, width=80).grid(row=0, column=1, columnspan=9, sticky="we", padx=6)

        tk.Label(cfg, text="Device:").grid(row=1, column=0, sticky="w")
        tk.OptionMenu(cfg, self.device_var, "auto", "cuda", "cpu").grid(row=1, column=1, sticky="w", padx=6)

        tk.Label(cfg, text="Compute:").grid(row=1, column=2, sticky="w")
        tk.OptionMenu(cfg, self.compute_var, "auto", "float16", "int8", "int8_float16", "float32").grid(row=1, column=3, sticky="w", padx=6)

        tk.Label(cfg, text="Language:").grid(row=1, column=4, sticky="w")
        tk.Entry(cfg, textvariable=self.lang, width=8).grid(row=1, column=5, sticky="w", padx=6)

        tk.Label(cfg, text="Beam:").grid(row=1, column=6, sticky="w")
        tk.Spinbox(cfg, from_=1, to=10, textvariable=self.beam_size, width=6).grid(row=1, column=7, sticky="w", padx=6)

        tk.Label(cfg, text="Chunk sec:").grid(row=1, column=8, sticky="w")
        tk.Spinbox(cfg, from_=1, to=8, textvariable=self.chunk_seconds, width=6).grid(row=1, column=9, sticky="w", padx=6)

        tk.Checkbutton(cfg, text="Low-latency overlap", variable=self.low_latency).grid(row=2, column=0, columnspan=2, sticky="w")

        tk.Label(cfg, text="Overlap ms:").grid(row=2, column=2, sticky="w")
        tk.Spinbox(cfg, from_=100, to=1500, increment=50, textvariable=self.overlap_ms, width=6).grid(row=2, column=3, sticky="w", padx=6)

        tk.Checkbutton(cfg, text="VAD filter", variable=self.vad_filter).grid(row=2, column=4, columnspan=2, sticky="w")

        tk.Label(cfg, text="Min RMS:").grid(row=2, column=6, sticky="w")
        tk.Entry(cfg, textvariable=self.min_rms, width=8).grid(row=2, column=7, sticky="w", padx=6)

        tk.Label(cfg, text="No-speech:").grid(row=2, column=8, sticky="w")
        tk.Entry(cfg, textvariable=self.no_speech_threshold, width=8).grid(row=2, column=9, sticky="w", padx=6)

        tk.Label(cfg, text="OSC IP:").grid(row=3, column=2, sticky="w")
        tk.Entry(cfg, textvariable=self.osc_ip, width=20).grid(row=3, column=3, sticky="w", padx=6)

        tk.Label(cfg, text="Port:").grid(row=3, column=4, sticky="w")
        tk.Entry(cfg, textvariable=self.osc_port, width=8).grid(row=3, column=5, sticky="w", padx=6)

        tk.Label(cfg, text="Address:").grid(row=3, column=6, sticky="w")
        tk.Entry(cfg, textvariable=self.osc_address, width=14).grid(row=3, column=7, sticky="w", padx=6)

        tk.Label(cfg, text="Payload:").grid(row=3, column=8, sticky="w")
        tk.OptionMenu(cfg, self.payload_mode, "speaker_text", "channel_text").grid(row=3, column=9, sticky="we", padx=6)

        tk.Button(cfg, text="Refresh mics", command=self.refresh_mics).grid(row=4, column=9, sticky="e")

        sources_frame = tk.LabelFrame(self.root, text="Sources (up to 4)")
        sources_frame.pack(fill=tk.X, padx=10, pady=6)

        headers = ["Use", "Speaker ID", "Channel", "Mic device"]
        for c, h in enumerate(headers):
            tk.Label(sources_frame, text=h).grid(row=0, column=c, sticky="w", padx=4)

        self.mic_menus: list[tk.OptionMenu] = []
        for i, src in enumerate(self.sources, start=1):
            tk.Checkbutton(sources_frame, text=f"Source {i}", variable=src.enabled).grid(row=i, column=0, sticky="w", padx=4)
            tk.Entry(sources_frame, textvariable=src.speaker_id, width=18).grid(row=i, column=1, sticky="w", padx=4)
            tk.Spinbox(sources_frame, from_=1, to=64, textvariable=src.channel, width=6).grid(row=i, column=2, sticky="w", padx=4)
            m = tk.OptionMenu(sources_frame, src.mic, "(loading...)")
            m.grid(row=i, column=3, sticky="we", padx=4)
            self.mic_menus.append(m)

        controls = tk.Frame(self.root)
        controls.pack(fill=tk.X, padx=10, pady=6)

        self.btn_start = tk.Button(controls, text="Start", command=self.start)
        self.btn_start.pack(side=tk.LEFT)

        self.btn_stop = tk.Button(controls, text="Stop", command=self.stop, state=tk.DISABLED)
        self.btn_stop.pack(side=tk.LEFT, padx=8)

        self.btn_test_osc = tk.Button(controls, text="Test OSC", command=self.test_osc)
        self.btn_test_osc.pack(side=tk.LEFT, padx=8)

        self.btn_preset_latency = tk.Button(controls, text="Preset: Aggressive Latency", command=self.apply_preset_latency)
        self.btn_preset_latency.pack(side=tk.LEFT, padx=8)

        self.btn_preset_quality = tk.Button(controls, text="Preset: Quality", command=self.apply_preset_quality)
        self.btn_preset_quality.pack(side=tk.LEFT, padx=8)

        self.btn_preset_rtx2070_cuda = tk.Button(controls, text="Preset: RTX 2070 CUDA", command=self.apply_preset_rtx2070_cuda)
        self.btn_preset_rtx2070_cuda.pack(side=tk.LEFT, padx=8)

        self.log = ScrolledText(self.root, wrap=tk.WORD, height=26)
        self.log.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        self._log(f"[Python] {sys.version.split()[0]}")
        self._log(f"[Executable] {sys.executable}")
        self._log("Ready. Enable sources, assign mics, click Start.")
        self._log("Note: transcription calls are serialized across sources for stable GPU use.")

        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

    def _log(self, s: str) -> None:
        self.log.insert(tk.END, s + "\n")
        self.log.see(tk.END)

    def _list_mics(self) -> list[str]:
        items: list[str] = []
        devices = sd.query_devices()
        for i, d in enumerate(devices):
            if d.get("max_input_channels", 0) > 0:
                items.append(f"{i}: {d.get('name', '(unknown)')}")
        return items or ["(no input devices)"]

    def refresh_mics(self) -> None:
        items = self._list_mics()
        for src, menu in zip(self.sources, self.mic_menus):
            m = menu["menu"]
            m.delete(0, "end")
            for it in items:
                m.add_command(label=it, command=lambda v=it, s=src: s.mic.set(v))
            if not src.mic.get() or src.mic.get() not in items:
                src.mic.set(items[0])
        self._log("Mic list refreshed.")

    def _selected_mic_index(self, src: SourceState) -> int | None:
        sel = src.mic.get()
        try:
            return int(sel.split(":")[0]) if ":" in sel else None
        except Exception:
            return None

    def _payload_for(self, src: SourceState, transcript: str) -> list[object]:
        mode = self.payload_mode.get().strip()
        if mode == "channel_text":
            return [int(src.channel.get()), transcript]
        return [src.speaker_id.get().strip() or "unknown", transcript]

    def _safe_float(self, value: str, fallback: float) -> float:
        try:
            return float(value)
        except Exception:
            return fallback

    def _is_duplicate_for_source(self, src: SourceState, text: str) -> bool:
        norm = " ".join(text.lower().split())
        if not norm:
            return True
        if norm == src.last_sent_text:
            return True
        src.last_sent_text = norm
        return False

    def _try_load_model(self, model_id: str, device: str, compute_type: str) -> WhisperModel:
        self._log(f"Loading Whisper model '{model_id}' on {device} ({compute_type})...")
        return WhisperModel(model_id, device=device, compute_type=compute_type)

    def apply_preset_latency(self) -> None:
        self.model_name.set("small.en")
        self.device_var.set("auto")
        self.compute_var.set("auto")
        self.lang.set("en")
        self.beam_size.set(1)
        self.chunk_seconds.set(1)
        self.overlap_ms.set(350)
        self.no_speech_threshold.set("0.50")
        self.min_rms.set("0.012")
        self.low_latency.set(True)
        self.vad_filter.set(True)
        self._log("Applied preset: Aggressive Latency")

    def apply_preset_quality(self) -> None:
        self.model_name.set("medium.en")
        self.device_var.set("auto")
        self.compute_var.set("auto")
        self.lang.set("en")
        self.beam_size.set(3)
        self.chunk_seconds.set(2)
        self.overlap_ms.set(700)
        self.no_speech_threshold.set("0.60")
        self.min_rms.set("0.010")
        self.low_latency.set(True)
        self.vad_filter.set(True)
        self._log("Applied preset: Quality")

    def apply_preset_rtx2070_cuda(self) -> None:
        self.model_name.set("large-v3")
        self.device_var.set("cuda")
        self.compute_var.set("float16")
        self.lang.set("en")
        self.beam_size.set(2)
        self.chunk_seconds.set(2)
        self.overlap_ms.set(650)
        self.no_speech_threshold.set("0.60")
        self.min_rms.set("0.010")
        self.low_latency.set(True)
        self.vad_filter.set(True)
        self._log("Applied preset: RTX 2070 CUDA")

    def _load_model(self) -> WhisperModel | None:
        model_id = self.model_name.get().strip() or "medium.en"
        device_pref = self.device_var.get().strip() or "auto"
        compute_pref = self.compute_var.get().strip() or "auto"

        attempts: list[tuple[str, str]] = []
        if device_pref == "auto":
            if compute_pref == "auto":
                attempts.extend([
                    ("cuda", "float16"),
                    ("cuda", "int8_float16"),
                    ("cpu", "int8"),
                ])
            else:
                attempts.extend([
                    ("cuda", compute_pref),
                    ("cpu", compute_pref),
                ])
        else:
            ctype = "float16" if compute_pref == "auto" and device_pref == "cuda" else compute_pref
            if ctype == "auto":
                ctype = "int8" if device_pref == "cpu" else "float16"
            attempts.append((device_pref, ctype))
            if device_pref == "cuda":
                # Allow an automatic CPU fallback when CUDA is requested but unavailable.
                attempts.append(("cpu", "int8"))

        last_error: Exception | None = None
        for device, compute_type in attempts:
            try:
                return self._try_load_model(model_id, device, compute_type)
            except Exception as e:
                last_error = e
                self._log(f"[warn] model init failed on {device}/{compute_type}: {e}")

        self._log(f"[error] Could not load model '{model_id}'.")
        if last_error is not None:
            self._log(f"[error] Last error: {last_error}")
        return None

    def test_osc(self) -> None:
        try:
            ip = self.osc_ip.get().strip() or "127.0.0.1"
            port = int(self.osc_port.get().strip() or "3333")
            addr = self.osc_address.get().strip() or "/speech"
            payload = ["actor_1", "OSC_TEST"] if self.payload_mode.get() == "speaker_text" else [1, "OSC_TEST"]
            send_osc(ip, port, addr, payload)
            self._log(f"[OK] Sent test OSC to {ip}:{port} {addr} payload={payload}")
        except Exception as e:
            self._log(f"[FAIL] OSC test failed: {e}")

    def start(self) -> None:
        if self.running:
            return

        self.run_token += 1
        run_token = self.run_token

        self.model = self._load_model()
        if self.model is None:
            return

        self.running = True
        enabled_count = 0
        for i, src in enumerate(self.sources, start=1):
            if not src.enabled.get():
                continue

            mic_index = self._selected_mic_index(src)
            if mic_index is None:
                self._log(f"[warn] Source {i} has invalid mic selection, skipping.")
                continue

            try:
                src.audio_q = queue.Queue()
                src.last_sent_text = ""
                src.stream = sd.InputStream(
                    samplerate=SAMPLE_RATE,
                    channels=1,
                    dtype="int16",
                    callback=self._make_audio_callback(src, i, run_token),
                    device=mic_index,
                )
                src.stream.start()
                threading.Thread(target=self._worker, args=(src, i, run_token), daemon=True).start()
                enabled_count += 1
                self._log(
                    f"[OK] Source {i} started on mic={src.mic.get()} "
                    f"speaker_id={src.speaker_id.get().strip()} channel={src.channel.get()}"
                )
            except Exception as e:
                self._log(f"[error] Failed to start source {i}: {e}")

        if enabled_count == 0:
            self._log("[error] No sources started.")
            self.stop()
            return

        self.btn_start.config(state=tk.DISABLED)
        self.btn_stop.config(state=tk.NORMAL)
        self._log(f"Listening on {enabled_count} source(s)...")

    def _make_audio_callback(self, src: SourceState, index: int, run_token: int):
        def cb(indata, frames, time_info, status) -> None:
            if (not self.running) or (run_token != self.run_token):
                return
            if status:
                self.root.after(0, self._log, f"[audio status] source {index}: {status}")
            src.audio_q.put(bytes(indata))

        return cb

    def _worker(self, src: SourceState, index: int, run_token: int) -> None:
        model = self.model
        if model is None:
            return

        pending = bytearray()

        while True:
            if (not self.running) or (run_token != self.run_token):
                return

            try:
                data = src.audio_q.get(timeout=0.25)
            except queue.Empty:
                continue

            pending.extend(data)
            chunk_seconds = max(1, int(self.chunk_seconds.get()))
            target_size = SAMPLE_RATE * BYTES_PER_SAMPLE * chunk_seconds
            if len(pending) < target_size:
                continue

            if self.low_latency.get():
                overlap_ms = max(100, int(self.overlap_ms.get()))
                overlap_bytes = min(target_size - BYTES_PER_SAMPLE, int(SAMPLE_RATE * BYTES_PER_SAMPLE * (overlap_ms / 1000.0)))
                step_size = max(BYTES_PER_SAMPLE, target_size - overlap_bytes)
                chunk_bytes = bytes(pending[:target_size])
                del pending[:step_size]
            else:
                chunk_bytes = bytes(pending)
                pending.clear()

            audio_i16 = np.frombuffer(chunk_bytes, dtype=np.int16)
            if audio_i16.size == 0:
                continue

            audio_f32 = (audio_i16.astype(np.float32) / 32768.0).copy()
            min_rms = max(0.001, self._safe_float(self.min_rms.get(), 0.010))
            rms = float(np.sqrt(np.mean(audio_f32 * audio_f32)))
            if rms < min_rms:
                continue
            lang = self.lang.get().strip() or None
            beam_size = max(1, int(self.beam_size.get()))
            use_vad = bool(self.vad_filter.get())
            no_speech_threshold = min(0.99, max(0.05, self._safe_float(self.no_speech_threshold.get(), 0.60)))

            try:
                with self.model_lock:
                    segments, info = model.transcribe(
                        audio_f32,
                        language=lang,
                        beam_size=beam_size,
                        vad_filter=use_vad,
                        no_speech_threshold=no_speech_threshold,
                        condition_on_previous_text=False,
                        task="transcribe",
                    )
                    no_speech_prob = float(getattr(info, "no_speech_prob", 0.0) or 0.0)
                    if no_speech_prob >= no_speech_threshold:
                        continue
                    text = " ".join(seg.text.strip() for seg in segments if seg.text and seg.text.strip()).strip()
            except Exception as e:
                self.root.after(0, self._log, f"[error] Transcribe failed for source {index}: {e}")
                continue

            if not text:
                continue
            if len(text) <= 1:
                continue
            if text.lower() in {"huh", "uh", "um"}:
                continue
            if rms < (min_rms * 2.0) and text.strip().lower() in {
                "thank you",
                "thank you for watching",
                "hello",
                "hello?",
                "you",
            }:
                continue
            if self._is_duplicate_for_source(src, text):
                continue

            try:
                ip = self.osc_ip.get().strip() or "127.0.0.1"
                port = int(self.osc_port.get().strip() or "3333")
                addr = self.osc_address.get().strip() or "/speech"
                payload = self._payload_for(src, text)
                send_osc(ip, port, addr, payload)
                self.root.after(0, self._log, f"TRANSCRIPT[{index}] {src.speaker_id.get().strip() or 'unknown'}: {text}")
            except Exception as e:
                self.root.after(0, self._log, f"[error] OSC send failed for source {index}: {e}")

    def stop(self) -> None:
        self.running = False
        self.run_token += 1

        for src in self.sources:
            try:
                if src.stream:
                    src.stream.stop()
                    src.stream.close()
            except Exception:
                pass
            src.stream = None
            src.audio_q = queue.Queue()

        self.btn_start.config(state=tk.NORMAL)
        self.btn_stop.config(state=tk.DISABLED)
        self._log("Stopped.")

    def on_close(self) -> None:
        self.stop()
        self.root.destroy()


def main() -> None:
    root = tk.Tk()
    app = WhisperMultiSourceApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()

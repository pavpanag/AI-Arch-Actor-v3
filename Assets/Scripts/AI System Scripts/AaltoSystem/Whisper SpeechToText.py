"""
Whisper -> OSC Transcriber (Tkinter GUI)

What it does:
- Captures mic audio via sounddevice (PortAudio)
- Offline speech-to-text via Faster-Whisper (GPU/CPU)
- Sends each finalized transcript via OSC UDP:
    address (default): /speech
    args: (int32 channel, string text)
"""

from __future__ import annotations

import os
import sys
import queue
import threading
import socket
import struct
import tkinter as tk
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


OSC_ADDRESS = "/speech"
SAMPLE_RATE = 16000
BYTES_PER_SAMPLE = 2


def send_osc(ip: str, port: int, args: Sequence[object], address: str = OSC_ADDRESS) -> None:
    data = build_osc_packet(address, args)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.sendto(data, (ip, int(port)))


class WhisperOscApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        root.title("Whisper -> OSC Transcriber")
        root.geometry("900x700")

        self.audio_q: "queue.Queue[bytes]" = queue.Queue()
        self.running = False
        self.stream: sd.InputStream | None = None
        self.model: WhisperModel | None = None
        self.last_sent_text = ""
        self.run_token = 0

        cfg = tk.Frame(root)
        cfg.pack(fill=tk.X, padx=10, pady=8)

        tk.Label(cfg, text="Whisper model (name or path):").grid(row=0, column=0, sticky="w")
        self.model_name = tk.Entry(cfg, width=68)
        self.model_name.grid(row=0, column=1, columnspan=5, sticky="we", padx=6)
        self.model_name.insert(0, "medium.en")

        tk.Label(cfg, text="Device:").grid(row=1, column=0, sticky="w")
        self.device_var = tk.StringVar(value="cuda")
        tk.OptionMenu(cfg, self.device_var, "auto", "cuda", "cpu").grid(row=1, column=1, sticky="w", padx=6)

        tk.Label(cfg, text="Compute:").grid(row=1, column=2, sticky="w")
        self.compute_var = tk.StringVar(value="float16")
        tk.OptionMenu(cfg, self.compute_var, "auto", "float16", "int8", "int8_float16", "float32").grid(row=1, column=3, sticky="w", padx=6)

        tk.Label(cfg, text="Language:").grid(row=1, column=4, sticky="w")
        self.lang = tk.Entry(cfg, width=8)
        self.lang.grid(row=1, column=5, sticky="w", padx=6)
        self.lang.insert(0, "en")

        tk.Label(cfg, text="Mic:").grid(row=2, column=0, sticky="w")
        self.mic_var = tk.StringVar(value="")
        self.mic_menu = tk.OptionMenu(cfg, self.mic_var, "(loading...)")
        self.mic_menu.grid(row=2, column=1, sticky="we", padx=6)

        tk.Button(cfg, text="Refresh mics", command=self.refresh_mics).grid(row=2, column=2, sticky="w")

        tk.Label(cfg, text="OSC IP:").grid(row=3, column=0, sticky="w")
        self.osc_ip = tk.Entry(cfg, width=18)
        self.osc_ip.grid(row=3, column=1, sticky="w", padx=6)
        self.osc_ip.insert(0, "127.0.0.1")

        tk.Label(cfg, text="Port:").grid(row=3, column=2, sticky="w")
        self.osc_port = tk.Entry(cfg, width=8)
        self.osc_port.grid(row=3, column=3, sticky="w", padx=6)
        self.osc_port.insert(0, "3333")

        tk.Label(cfg, text="Payload:").grid(row=3, column=4, sticky="w")
        self.payload_modes = {
            "Channel + text (int,string)": "channel_text",
            "Text only (string)": "text_only",
        }
        payload_choices = tuple(self.payload_modes.keys())
        self.payload_var = tk.StringVar(value=payload_choices[0])
        tk.OptionMenu(cfg, self.payload_var, *payload_choices).grid(row=3, column=5, sticky="we", padx=6)

        tk.Label(cfg, text="Channel:").grid(row=4, column=0, sticky="w")
        self.channel = tk.Spinbox(cfg, from_=0, to=64, width=8)
        self.channel.grid(row=4, column=1, sticky="w", padx=6)
        self.channel.delete(0, "end")
        self.channel.insert(0, "1")

        tk.Label(cfg, text="Beam:").grid(row=4, column=2, sticky="w")
        self.beam = tk.Spinbox(cfg, from_=1, to=10, width=8)
        self.beam.grid(row=4, column=3, sticky="w", padx=6)
        self.beam.delete(0, "end")
        self.beam.insert(0, "2")

        tk.Label(cfg, text="Chunk sec:").grid(row=4, column=4, sticky="w")
        self.chunk_sec = tk.Spinbox(cfg, from_=1, to=8, width=8)
        self.chunk_sec.grid(row=4, column=5, sticky="w", padx=6)
        self.chunk_sec.delete(0, "end")
        self.chunk_sec.insert(0, "2")

        tk.Label(cfg, text="No-speech:").grid(row=4, column=6, sticky="w")
        self.no_speech_threshold = tk.Spinbox(cfg, from_=0.05, to=0.99, increment=0.05, width=8)
        self.no_speech_threshold.grid(row=4, column=7, sticky="w", padx=6)
        self.no_speech_threshold.delete(0, "end")
        self.no_speech_threshold.insert(0, "0.60")

        self.low_latency = tk.BooleanVar(value=True)
        tk.Checkbutton(cfg, text="Low-latency overlap", variable=self.low_latency).grid(row=5, column=0, columnspan=2, sticky="w")

        tk.Label(cfg, text="Overlap ms:").grid(row=5, column=2, sticky="w")
        self.overlap_ms = tk.Spinbox(cfg, from_=100, to=1500, increment=50, width=8)
        self.overlap_ms.grid(row=5, column=3, sticky="w", padx=6)
        self.overlap_ms.delete(0, "end")
        self.overlap_ms.insert(0, "600")

        tk.Label(cfg, text="Min RMS:").grid(row=5, column=6, sticky="w")
        self.min_rms = tk.Spinbox(cfg, from_=0.001, to=0.05, increment=0.001, width=8)
        self.min_rms.grid(row=5, column=7, sticky="w", padx=6)
        self.min_rms.delete(0, "end")
        self.min_rms.insert(0, "0.010")

        self.vad_filter = tk.BooleanVar(value=False)
        tk.Checkbutton(cfg, text="VAD filter", variable=self.vad_filter).grid(row=5, column=4, columnspan=2, sticky="w")

        self.btn_start = tk.Button(cfg, text="Start", command=self.start)
        self.btn_start.grid(row=6, column=2, sticky="w", padx=6)
        self.btn_stop = tk.Button(cfg, text="Stop", command=self.stop, state=tk.DISABLED)
        self.btn_stop.grid(row=6, column=3, sticky="w", padx=6)

        tk.Button(cfg, text="Test OSC", command=self.test_osc).grid(row=6, column=4, sticky="w", padx=6)
        tk.Button(cfg, text="Preset: Aggressive Latency", command=self.apply_preset_latency).grid(row=6, column=5, sticky="w", padx=6)
        tk.Button(cfg, text="Preset: Quality", command=self.apply_preset_quality).grid(row=6, column=6, sticky="w", padx=6)
        tk.Button(cfg, text="Preset: RTX 2070 CUDA", command=self.apply_preset_rtx2070_cuda).grid(row=6, column=7, sticky="w", padx=6)

        self.log = ScrolledText(root, wrap=tk.WORD, height=26)
        self.log.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        self._log(f"[Python] {sys.version.split()[0]}")
        self._log(f"[Executable] {sys.executable}")
        self._log("Ready. Choose model + mic, then click Start.")

        root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.refresh_mics()

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
        menu = self.mic_menu["menu"]
        menu.delete(0, "end")
        items = self._list_mics()
        for it in items:
            menu.add_command(label=it, command=lambda v=it: self.mic_var.set(v))
        self.mic_var.set(items[0])
        self._log("Mic list refreshed.")

    def _selected_mic_index(self) -> int | None:
        sel = self.mic_var.get()
        try:
            return int(sel.split(":")[0]) if ":" in sel else None
        except Exception:
            return None

    def _safe_channel(self) -> int:
        try:
            return int(self.channel.get())
        except Exception:
            return 0

    def _safe_int(self, value: str, fallback: int) -> int:
        try:
            return int(value)
        except Exception:
            return fallback

    def _safe_float(self, value: str, fallback: float) -> float:
        try:
            return float(value)
        except Exception:
            return fallback

    def _is_duplicate(self, text: str) -> bool:
        norm = " ".join(text.lower().split())
        if not norm:
            return True
        if norm == self.last_sent_text:
            return True
        self.last_sent_text = norm
        return False

    def _current_payload_values(self, transcript: str) -> list[object]:
        mode = self.payload_modes.get(self.payload_var.get(), "channel_text")
        if mode == "text_only":
            return [transcript]
        return [self._safe_channel(), transcript]

    def _describe_payload(self, payload: Sequence[object]) -> str:
        tags = "," + "".join(_osc_type_tag_for(v) for v in payload)
        values = ", ".join(repr(v) for v in payload) or "empty"
        return f"{tags} [{values}]"

    def _try_load_model(self, model_id: str, device: str, compute_type: str) -> WhisperModel:
        self._log(f"Loading Whisper model '{model_id}' on {device} ({compute_type})...")
        return WhisperModel(model_id, device=device, compute_type=compute_type)

    def apply_preset_latency(self) -> None:
        self.model_name.delete(0, "end")
        self.model_name.insert(0, "small.en")
        self.device_var.set("auto")
        self.compute_var.set("auto")
        self.lang.delete(0, "end")
        self.lang.insert(0, "en")
        self.beam.delete(0, "end")
        self.beam.insert(0, "1")
        self.chunk_sec.delete(0, "end")
        self.chunk_sec.insert(0, "1")
        self.overlap_ms.delete(0, "end")
        self.overlap_ms.insert(0, "350")
        self.no_speech_threshold.delete(0, "end")
        self.no_speech_threshold.insert(0, "0.50")
        self.min_rms.delete(0, "end")
        self.min_rms.insert(0, "0.012")
        self.low_latency.set(True)
        self.vad_filter.set(True)
        self._log("Applied preset: Aggressive Latency")

    def apply_preset_quality(self) -> None:
        self.model_name.delete(0, "end")
        self.model_name.insert(0, "medium.en")
        self.device_var.set("auto")
        self.compute_var.set("auto")
        self.lang.delete(0, "end")
        self.lang.insert(0, "en")
        self.beam.delete(0, "end")
        self.beam.insert(0, "3")
        self.chunk_sec.delete(0, "end")
        self.chunk_sec.insert(0, "2")
        self.overlap_ms.delete(0, "end")
        self.overlap_ms.insert(0, "700")
        self.no_speech_threshold.delete(0, "end")
        self.no_speech_threshold.insert(0, "0.60")
        self.min_rms.delete(0, "end")
        self.min_rms.insert(0, "0.010")
        self.low_latency.set(True)
        self.vad_filter.set(True)
        self._log("Applied preset: Quality")

    def apply_preset_rtx2070_cuda(self) -> None:
        self.model_name.delete(0, "end")
        self.model_name.insert(0, "large-v3")
        self.device_var.set("cuda")
        self.compute_var.set("float16")
        self.lang.delete(0, "end")
        self.lang.insert(0, "en")
        self.beam.delete(0, "end")
        self.beam.insert(0, "2")
        self.chunk_sec.delete(0, "end")
        self.chunk_sec.insert(0, "2")
        self.overlap_ms.delete(0, "end")
        self.overlap_ms.insert(0, "650")
        self.no_speech_threshold.delete(0, "end")
        self.no_speech_threshold.insert(0, "0.60")
        self.min_rms.delete(0, "end")
        self.min_rms.insert(0, "0.010")
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
            payload = self._current_payload_values("OSC_TEST")
            send_osc(ip, port, payload)
            self._log(f"[OK] Sent {self._describe_payload(payload)} -> {ip}:{port}")
        except Exception as e:
            self._log(f"[FAIL] OSC test failed: {e}")

    def audio_callback(self, indata, frames, time_info, status) -> None:
        if not self.running:
            return
        if status:
            self.root.after(0, self._log, f"[audio status] {status}")
        self.audio_q.put(bytes(indata))

    def start(self) -> None:
        if self.running:
            return

        self.run_token += 1
        run_token = self.run_token
        self.audio_q = queue.Queue()

        self.model = self._load_model()
        if self.model is None:
            return
        self.last_sent_text = ""

        mic_index = self._selected_mic_index()

        try:
            self.stream = sd.InputStream(
                samplerate=SAMPLE_RATE,
                channels=1,
                dtype="int16",
                callback=self.audio_callback,
                device=mic_index,
            )
            self.stream.start()
        except Exception as e:
            self._log(f"[error] Failed to start audio stream: {e}")
            return

        self.running = True
        self.btn_start.config(state=tk.DISABLED)
        self.btn_stop.config(state=tk.NORMAL)
        self._log("Listening...")
        threading.Thread(target=self.worker, args=(run_token,), daemon=True).start()

    def stop(self) -> None:
        self.running = False
        self.run_token += 1
        try:
            if self.stream:
                self.stream.stop()
                self.stream.close()
        except Exception:
            pass
        self.stream = None
        self.audio_q = queue.Queue()
        self.btn_start.config(state=tk.NORMAL)
        self.btn_stop.config(state=tk.DISABLED)
        self._log("Stopped.")

    def on_close(self) -> None:
        self.stop()
        self.root.destroy()

    def worker(self, run_token: int) -> None:
        model = self.model
        if model is None:
            return

        pending = bytearray()

        while self.running and run_token == self.run_token:
            try:
                data = self.audio_q.get(timeout=0.25)
            except queue.Empty:
                continue

            pending.extend(data)

            chunk_seconds = max(1, self._safe_int(self.chunk_sec.get(), 2))
            target_size = SAMPLE_RATE * BYTES_PER_SAMPLE * chunk_seconds
            if len(pending) < target_size:
                continue

            if self.low_latency.get():
                overlap_ms = max(100, self._safe_int(self.overlap_ms.get(), 600))
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

            try:
                beam_size = max(1, self._safe_int(self.beam.get(), 2))
                lang = self.lang.get().strip() or None
                no_speech_threshold = min(0.99, max(0.05, self._safe_float(self.no_speech_threshold.get(), 0.60)))
                segments, info = model.transcribe(
                    audio_f32,
                    language=lang,
                    beam_size=beam_size,
                    vad_filter=bool(self.vad_filter.get()),
                    no_speech_threshold=no_speech_threshold,
                    condition_on_previous_text=False,
                    task="transcribe",
                )
                no_speech_prob = float(getattr(info, "no_speech_prob", 0.0) or 0.0)
                if no_speech_prob >= no_speech_threshold:
                    continue
                text = " ".join(seg.text.strip() for seg in segments if seg.text and seg.text.strip()).strip()
            except Exception as e:
                self.root.after(0, self._log, f"[error] transcribe failed: {e}")
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
            if self._is_duplicate(text):
                continue

            try:
                ip = self.osc_ip.get().strip() or "127.0.0.1"
                port = int(self.osc_port.get().strip() or "3333")
                payload = self._current_payload_values(text)
                send_osc(ip, port, payload)
                self.root.after(0, self._log, f"TRANSCRIPT: {text}")
                self.root.after(0, self._log, f"SEND -> {ip}:{port} {self._describe_payload(payload)}")
            except Exception as e:
                self.root.after(0, self._log, f"[error] OSC send failed: {e}")


def main() -> None:
    r = tk.Tk()
    WhisperOscApp(r)
    r.mainloop()


if __name__ == "__main__":
    main()

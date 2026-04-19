"""
Vosk Multi-Source -> OSC Transcriber (Tkinter GUI)

What it does:
- Captures up to 4 microphone sources simultaneously
- Runs one Vosk recognizer per enabled source
- Sends finalized transcripts via OSC with speaker identity

Default OSC payload:
    address: /speech
    args: (string speaker_id, string text)

Optional payload mode:
    args: (int channel, string text)
"""

from __future__ import annotations

import os
import sys
import json
import queue
import threading
import socket
import struct
import tkinter as tk
from tkinter.scrolledtext import ScrolledText
from collections.abc import Sequence


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
require("vosk")

import sounddevice as sd
import vosk


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
SOURCE_COUNT = 4


class SourceState:
    def __init__(self) -> None:
        self.enabled = tk.BooleanVar(value=False)
        self.speaker_id = tk.StringVar(value="")
        self.channel = tk.IntVar(value=1)
        self.mic = tk.StringVar(value="")
        self.audio_q: "queue.Queue[bytes]" = queue.Queue()
        self.stream: sd.InputStream | None = None
        self.recognizer: vosk.KaldiRecognizer | None = None
        self.last_partial: str = ""


class VoskMultiSourceApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("Vosk Multi-Source -> OSC")
        self.root.geometry("980x760")

        self.running = False
        self.model: vosk.Model | None = None
        self.sources: list[SourceState] = [SourceState() for _ in range(SOURCE_COUNT)]

        for i, src in enumerate(self.sources, start=1):
            src.speaker_id.set(f"actor_{i}")
            src.channel.set(i)
            src.enabled.set(i <= 2)

        self.osc_ip = tk.StringVar(value="127.0.0.1")
        self.osc_port = tk.StringVar(value="3333")
        self.osc_address = tk.StringVar(value="/speech")
        self.payload_mode = tk.StringVar(value="speaker_text")
        self.show_partials = tk.BooleanVar(value=False)
        self.model_path = tk.StringVar(
            value=r"C:\Users\pavpa\AI Arch Actor v3\Assets\Scripts\AI System Scripts\System v2\vosk-model-small-en-us-0.15\vosk-model-small-en-us-0.15"
        )

        self._build_ui()
        self.refresh_mics()

    def _build_ui(self) -> None:
        cfg = tk.Frame(self.root)
        cfg.pack(fill=tk.X, padx=10, pady=8)

        tk.Label(cfg, text="Vosk model path:").grid(row=0, column=0, sticky="w")
        tk.Entry(cfg, textvariable=self.model_path, width=108).grid(row=0, column=1, columnspan=8, sticky="we", padx=6)

        tk.Label(cfg, text="OSC IP:").grid(row=1, column=0, sticky="w")
        tk.Entry(cfg, textvariable=self.osc_ip, width=20).grid(row=1, column=1, sticky="w", padx=6)

        tk.Label(cfg, text="Port:").grid(row=1, column=2, sticky="w")
        tk.Entry(cfg, textvariable=self.osc_port, width=8).grid(row=1, column=3, sticky="w", padx=6)

        tk.Label(cfg, text="Address:").grid(row=1, column=4, sticky="w")
        tk.Entry(cfg, textvariable=self.osc_address, width=14).grid(row=1, column=5, sticky="w", padx=6)

        tk.Label(cfg, text="Payload:").grid(row=1, column=6, sticky="w")
        tk.OptionMenu(cfg, self.payload_mode, "speaker_text", "channel_text").grid(row=1, column=7, sticky="we", padx=6)

        tk.Button(cfg, text="Refresh mics", command=self.refresh_mics).grid(row=1, column=8, sticky="e")

        tk.Checkbutton(
            cfg,
            text="Show partial preview",
            variable=self.show_partials).grid(row=2, column=0, columnspan=3, sticky="w")

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

        self.log = ScrolledText(self.root, wrap=tk.WORD, height=26)
        self.log.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        self._log(f"[Python] {sys.version.split()[0]}")
        self._log(f"[Executable] {sys.executable}")
        self._log(f"[CWD] {os.getcwd()}")
        self._log("Ready. Enable sources, assign mics, click Start.")

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

        model_path = self.model_path.get().strip()
        if not model_path or not os.path.isdir(model_path):
            self._log(f"[error] Invalid model path: {model_path}")
            return

        try:
            self._log("Loading Vosk model...")
            self.model = vosk.Model(model_path)
        except Exception as e:
            self._log(f"[error] Failed to load Vosk model: {e}")
            return

        # Set running before starting worker threads to avoid a startup race
        # where workers exit before the flag flips to True.
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
                src.recognizer = vosk.KaldiRecognizer(self.model, SAMPLE_RATE)
                src.audio_q = queue.Queue()
                src.stream = sd.InputStream(
                    samplerate=SAMPLE_RATE,
                    channels=1,
                    dtype="int16",
                    callback=self._make_audio_callback(src, i),
                    device=mic_index,
                )
                src.stream.start()
                threading.Thread(target=self._worker, args=(src, i), daemon=True).start()
                enabled_count += 1
                self._log(f"[OK] Source {i} started on mic={src.mic.get()} speaker_id={src.speaker_id.get().strip()} channel={src.channel.get()}")
            except Exception as e:
                self._log(f"[error] Failed to start source {i}: {e}")

        if enabled_count == 0:
            self._log("[error] No sources started.")
            self.stop()
            return

        self.btn_start.config(state=tk.DISABLED)
        self.btn_stop.config(state=tk.NORMAL)
        self._log(f"Listening on {enabled_count} source(s)...")

    def _make_audio_callback(self, src: SourceState, index: int):
        def cb(indata, frames, time_info, status) -> None:
            if status:
                self.root.after(0, self._log, f"[audio status] source {index}: {status}")
            src.audio_q.put(bytes(indata))

        return cb

    def _worker(self, src: SourceState, index: int) -> None:
        while True:
            if not self.running:
                return

            try:
                data = src.audio_q.get(timeout=0.25)
            except queue.Empty:
                continue

            rec = src.recognizer
            if rec is None:
                continue

            if rec.AcceptWaveform(data):
                result_raw = rec.Result()
                text = ""
                try:
                    text = (json.loads(result_raw).get("text") or "").strip()
                except Exception:
                    text = ""

                if text:
                    # Keep sentence-level behavior similar to the single-source script.
                    if len(text) <= 1:
                        continue
                    if text.lower() in {"huh", "uh", "um"}:
                        continue

                    src.last_partial = ""
                    try:
                        ip = self.osc_ip.get().strip() or "127.0.0.1"
                        port = int(self.osc_port.get().strip() or "3333")
                        addr = self.osc_address.get().strip() or "/speech"
                        payload = self._payload_for(src, text)
                        send_osc(ip, port, addr, payload)
                        self.root.after(0, self._log, f"TRANSCRIPT[{index}] {src.speaker_id.get().strip() or 'unknown'}: {text}")
                    except Exception as e:
                        self.root.after(0, self._log, f"[error] OSC send failed for source {index}: {e}")
            else:
                if not self.show_partials.get():
                    continue

                try:
                    partial_text = (json.loads(rec.PartialResult()).get("partial") or "").strip()
                except Exception:
                    partial_text = ""

                if partial_text and partial_text != src.last_partial:
                    src.last_partial = partial_text
                    self.root.after(
                        0,
                        self._log,
                        f"[{index}~] {src.speaker_id.get().strip() or 'unknown'} (partial): {partial_text}")

    def stop(self) -> None:
        self.running = False

        for src in self.sources:
            try:
                if src.stream:
                    src.stream.stop()
                    src.stream.close()
            except Exception:
                pass
            src.stream = None

        self.btn_start.config(state=tk.NORMAL)
        self.btn_stop.config(state=tk.DISABLED)
        self._log("Stopped.")

    def on_close(self) -> None:
        self.stop()
        self.root.destroy()


def main() -> None:
    root = tk.Tk()
    app = VoskMultiSourceApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()

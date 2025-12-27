#!/usr/bin/env python3
"""
Vosk → OSC Transcriber (Tkinter GUI)

What it does:
- Captures mic audio via sounddevice (PortAudio)
- Offline speech-to-text via Vosk
- Sends each finalized transcript via OSC UDP:
    address (default): /speech
    args: (int32 channel, string text)
"""

from __future__ import annotations

import os
import sys
import json
import queue
import threading
import time
import socket
import struct
import tkinter as tk
from tkinter.scrolledtext import ScrolledText
from collections.abc import Sequence


# -------------------- Minimal dependency checks --------------------

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


# -------------------- OSC helpers (no external deps) --------------------

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

def send_osc(ip: str, port: int, args: Sequence[object], address: str = OSC_ADDRESS) -> None:
    data = build_osc_packet(address, args)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.sendto(data, (ip, int(port)))


# -------------------- App --------------------

SAMPLE_RATE = 16000

class VoskOscApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        root.title("Vosk → OSC Transcriber")
        root.geometry("820x620")

        self.audio_q: "queue.Queue[bytes]" = queue.Queue()
        self.running = False
        self.stream: sd.InputStream | None = None
        self.model: vosk.Model | None = None
        self.recognizer: vosk.KaldiRecognizer | None = None

        # ---------- UI: config ----------
        cfg = tk.Frame(root)
        cfg.pack(fill=tk.X, padx=10, pady=8)

        tk.Label(cfg, text="Vosk model path:").grid(row=0, column=0, sticky="w")
        self.model_path = tk.Entry(cfg, width=86)
        self.model_path.grid(row=0, column=1, columnspan=7, sticky="we", padx=6)
        self.model_path.insert(0, "/Users/psxpp3/Desktop/Final Form/LLM architecture control/vosk-model-small-en-us-0.15")

        tk.Label(cfg, text="Mic:").grid(row=1, column=0, sticky="w")
        self.mic_var = tk.StringVar(value="")
        self.mic_menu = tk.OptionMenu(cfg, self.mic_var, "(loading...)")
        self.mic_menu.grid(row=1, column=1, sticky="we", padx=6)

        self.btn_refresh = tk.Button(cfg, text="Refresh mics", command=self.refresh_mics)
        self.btn_refresh.grid(row=1, column=2, sticky="w")

        tk.Label(cfg, text="OSC IP:").grid(row=2, column=0, sticky="w")
        self.osc_ip = tk.Entry(cfg, width=18)
        self.osc_ip.grid(row=2, column=1, sticky="w", padx=6)
        self.osc_ip.insert(0, "127.0.0.1")

        tk.Label(cfg, text="Port:").grid(row=2, column=2, sticky="w")
        self.osc_port = tk.Entry(cfg, width=8)
        self.osc_port.grid(row=2, column=3, sticky="w", padx=6)
        self.osc_port.insert(0, "3333")

        tk.Label(cfg, text="Payload:").grid(row=2, column=4, sticky="w")
        self.payload_modes = {
            "Channel + text (int,string)": "channel_text",
            "Text only (string)": "text_only",
        }
        payload_choices = tuple(self.payload_modes.keys())
        self.payload_var = tk.StringVar(value=payload_choices[0])
        self.payload_menu = tk.OptionMenu(cfg, self.payload_var, *payload_choices)
        self.payload_menu.grid(row=2, column=5, sticky="we", padx=6)

        tk.Label(cfg, text="Channel:").grid(row=3, column=0, sticky="w")
        self.channel = tk.Spinbox(cfg, from_=0, to=64, width=8)
        self.channel.grid(row=3, column=1, sticky="w", padx=6)
        self.channel.delete(0, "end")
        self.channel.insert(0, "1")

        self.btn_start = tk.Button(cfg, text="Start", command=self.start)
        self.btn_start.grid(row=3, column=2, sticky="w", padx=6)

        self.btn_stop = tk.Button(cfg, text="Stop", command=self.stop, state=tk.DISABLED)
        self.btn_stop.grid(row=3, column=3, sticky="w", padx=6)

        self.btn_test_imports = tk.Button(cfg, text="Test imports", command=self.test_imports)
        self.btn_test_imports.grid(row=3, column=4, sticky="w", padx=6)

        self.btn_test_osc = tk.Button(cfg, text="Test OSC", command=self.test_osc)
        self.btn_test_osc.grid(row=3, column=5, sticky="w", padx=6)

        # ---------- Log ----------
        self.log = ScrolledText(root, wrap=tk.WORD, height=24)
        self.log.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        self._log(f"[Python] {sys.version.split()[0]}")
        self._log(f"[Executable] {sys.executable}")
        self._log(f"[CWD] {os.getcwd()}")
        self._log("Ready. Set model path, choose mic, click Start.")
        self._log("If you get no audio on macOS: enable Microphone permission for Terminal/IDE.")

        root.protocol("WM_DELETE_WINDOW", self.on_close)

        self.refresh_mics()

    # ---------- Logging ----------
    def _log(self, s: str) -> None:
        self.log.insert(tk.END, s + "\n")
        self.log.see(tk.END)

    # ---------- Tests ----------
    def test_imports(self) -> None:
        try:
            import importlib.util  # just to prove it's fine
            self._log("[OK] importlib.util imports")
            import requests
            self._log("[OK] requests imports")
            self._log("[OK] vosk imports")
            self._log("[OK] sounddevice imports")
        except Exception as e:
            self._log(f"[FAIL] Import test failed: {e}")

    def test_osc(self) -> None:
        try:
            ip = self.osc_ip.get().strip() or "127.0.0.1"
            port = int(self.osc_port.get().strip() or "3333")
            payload = self._current_payload_values("OSC_TEST")
            send_osc(ip, port, payload)
            self._log(f"[OK] Sent {self._describe_payload(payload)} -> {ip}:{port}")
        except Exception as e:
            self._log(f"[FAIL] OSC test failed: {e}")

    # ---------- Mic listing ----------
    def _list_mics(self) -> list[str]:
        items: list[str] = []
        devices = sd.query_devices()
        for i, d in enumerate(devices):
            if d.get("max_input_channels", 0) > 0:
                items.append(f"{i}: {d.get('name','(unknown)')}")
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

    def _current_payload_values(self, transcript: str) -> list[object]:
        mode = self.payload_modes.get(self.payload_var.get(), "channel_text")
        if mode == "text_only":
            return [transcript]
        return [self._safe_channel(), transcript]

    def _describe_payload(self, payload: Sequence[object]) -> str:
        tags = "," + "".join(_osc_type_tag_for(v) for v in payload)
        values = ", ".join(repr(v) for v in payload) or "∅"
        return f"{tags} [{values}]"

    # ---------- Audio + worker ----------
    def audio_callback(self, indata, frames, time_info, status) -> None:
        # Keep callback minimal.
        if status:
            self.root.after(0, self._log, f"[audio status] {status}")
        self.audio_q.put(bytes(indata))

    def start(self) -> None:
        if self.running:
            return

        mp = self.model_path.get().strip()
        if not mp:
            self._log("[error] Model path is empty.")
            return
        if not os.path.isdir(mp):
            self._log(f"[error] Model path does not exist or is not a directory:\n  {mp}")
            return

        # Load Vosk model
        try:
            self._log("Loading Vosk model...")
            self.model = vosk.Model(mp)
            self.recognizer = vosk.KaldiRecognizer(self.model, SAMPLE_RATE)
        except Exception as e:
            self._log(f"[error] Failed to load Vosk model: {e}")
            return

        mic_index = self._selected_mic_index()

        # Start audio stream
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
            self._log("macOS note: check System Settings → Privacy & Security → Microphone → enable Terminal/IDE.")
            return

        self.running = True
        self.btn_start.config(state=tk.DISABLED)
        self.btn_stop.config(state=tk.NORMAL)
        self._log("Listening...")

        threading.Thread(target=self.worker, daemon=True).start()

    def stop(self) -> None:
        self.running = False
        try:
            if self.stream:
                self.stream.stop()
                self.stream.close()
        except Exception:
            pass
        self.stream = None
        self.btn_start.config(state=tk.NORMAL)
        self.btn_stop.config(state=tk.DISABLED)
        self._log("Stopped.")

    def on_close(self) -> None:
        self.stop()
        self.root.destroy()

    def worker(self) -> None:
        assert self.recognizer is not None

        while self.running:
            try:
                audio = self.audio_q.get(timeout=0.25)
            except queue.Empty:
                continue

            try:
                if self.recognizer.AcceptWaveform(audio):
                    res = json.loads(self.recognizer.Result())
                    text = (res.get("text") or "").strip()
                    if not text:
                        continue

                    # tiny spam filters
                    if len(text) <= 1:
                        continue
                    if text.lower() in {"huh", "uh", "um"}:
                        continue

                    ip = self.osc_ip.get().strip() or "127.0.0.1"
                    port = int(self.osc_port.get().strip() or "3333")
                    payload = self._current_payload_values(text)

                    self.root.after(0, self._log, f"TRANSCRIPT: {text}")
                    self.root.after(0, self._log, f"SEND -> {ip}:{port} {self._describe_payload(payload)}")
                    send_osc(ip, port, payload)
            except Exception as e:
                self.root.after(0, self._log, f"[error] worker: {e}")
                time.sleep(0.15)


def main() -> None:
    r = tk.Tk()
    VoskOscApp(r)
    r.mainloop()


if __name__ == "__main__":
    main()

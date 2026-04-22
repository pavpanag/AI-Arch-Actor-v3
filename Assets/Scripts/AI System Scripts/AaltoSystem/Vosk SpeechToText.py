"""
Vosk -> OSC Transcriber (Tkinter GUI)

What it does:
- Captures mic audio via sounddevice (PortAudio)
- Offline speech-to-text via Vosk
- Lets you choose/download Vosk models from a small curated list
- Optionally attempts CUDA/GPU initialization when the installed Vosk build supports it
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
import ctypes
import shutil
import tempfile
import zipfile
import tkinter as tk
from tkinter import filedialog
from tkinter import messagebox
from tkinter.scrolledtext import ScrolledText
from collections.abc import Callable, Sequence
from pathlib import Path
from urllib.error import URLError
from urllib.request import urlretrieve


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


# -------------------- Model helpers --------------------

SCRIPT_DIR = Path(__file__).resolve().parent
MODEL_BASE_URL = "https://alphacephei.com/vosk/models"
MODEL_INSTALL_DIR = SCRIPT_DIR
DEFAULT_MODEL_NAME = "vosk-model-small-en-us-0.15"
DEFAULT_MODEL_PRESET = "Small English US 0.15 (installed/default, 40 MB)"

# Curated from the official Vosk model list. Big models are more accurate but
# can require many GB of RAM and take a while to load/download.
VOSK_MODELS: dict[str, dict[str, str]] = {
    "Small English US 0.15 (installed/default, 40 MB)": {
        "name": "vosk-model-small-en-us-0.15",
        "url": f"{MODEL_BASE_URL}/vosk-model-small-en-us-0.15.zip",
        "notes": "Fast, light, good for real-time desktop work.",
        "min_free_gb": "1",
        "rec_free_gb": "2",
    },
    "English US 0.22 lgraph (better, 128 MB)": {
        "name": "vosk-model-en-us-0.22-lgraph",
        "url": f"{MODEL_BASE_URL}/vosk-model-en-us-0.22-lgraph.zip",
        "notes": "Better accuracy than small while still fairly practical.",
        "min_free_gb": "2",
        "rec_free_gb": "4",
    },
    "English US 0.22 accurate (1.8 GB)": {
        "name": "vosk-model-en-us-0.22",
        "url": f"{MODEL_BASE_URL}/vosk-model-en-us-0.22.zip",
        "notes": "Accurate generic English model; needs much more RAM.",
        "min_free_gb": "6",
        "rec_free_gb": "10",
    },
    "English US 0.42 GigaSpeech (2.3 GB)": {
        "name": "vosk-model-en-us-0.42-gigaspeech",
        "url": f"{MODEL_BASE_URL}/vosk-model-en-us-0.42-gigaspeech.zip",
        "notes": "Very accurate for podcasts/general speech; huge download.",
        "min_free_gb": "10",
        "rec_free_gb": "14",
    },
    "Custom path below": {
        "name": "",
        "url": "",
        "notes": "Use the model path field directly.",
    },
}


def model_dir_for(model_name: str) -> Path:
    return MODEL_INSTALL_DIR / model_name


def is_vosk_model_dir(path: Path) -> bool:
    return path.is_dir() and any((path / child).exists() for child in ("am", "conf"))


def find_local_model_dir(model_name: str) -> Path | None:
    direct = model_dir_for(model_name)
    if is_vosk_model_dir(direct):
        return direct

    # Some old/manual installs end up nested as name/name. Accept that too.
    nested = direct / model_name
    if is_vosk_model_dir(nested):
        return nested

    matches = list(SCRIPT_DIR.glob(f"**/{model_name}"))
    for match in matches:
        if is_vosk_model_dir(match):
            return match
    return None


def best_initial_model_path() -> str:
    found = find_local_model_dir(DEFAULT_MODEL_NAME)
    if found:
        return str(found)
    return str(model_dir_for(DEFAULT_MODEL_NAME))


def download_and_extract_model(model_name: str, url: str, log: Callable[[str], None]) -> Path:
    dest = model_dir_for(model_name)
    existing = find_local_model_dir(model_name)
    if existing:
        log(f"[model] Already installed: {existing}")
        return existing

    MODEL_INSTALL_DIR.mkdir(parents=True, exist_ok=True)
    tmp_dir = Path(tempfile.mkdtemp(prefix="vosk-model-download-", dir=str(MODEL_INSTALL_DIR)))
    zip_path = tmp_dir / f"{model_name}.zip"

    def report(blocks: int, block_size: int, total: int) -> None:
        if total <= 0:
            return
        downloaded = min(blocks * block_size, total)
        pct = int(downloaded * 100 / total)
        # Log only every 10% to avoid flooding Tkinter.
        if pct % 10 == 0 and report.last_pct != pct:
            report.last_pct = pct
            log(f"[download] {model_name}: {pct}%")

    report.last_pct = -1  # type: ignore[attr-defined]

    try:
        log(f"[download] Fetching {url}")
        urlretrieve(url, zip_path, reporthook=report)
        log("[download] Extracting model zip...")
        with zipfile.ZipFile(zip_path, "r") as zf:
            zf.extractall(MODEL_INSTALL_DIR)
    except (URLError, OSError, zipfile.BadZipFile) as e:
        raise RuntimeError(f"Download/extract failed: {e}") from e
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)

    installed = find_local_model_dir(model_name)
    if not installed:
        raise RuntimeError(f"Downloaded {model_name}, but no valid Vosk model folder was found after extraction.")

    log(f"[model] Installed: {installed}")
    return installed


# -------------------- Memory helpers --------------------

def _fmt_bytes(n: int) -> str:
    units = ["B", "KB", "MB", "GB", "TB"]
    x = float(max(0, int(n)))
    for u in units:
        if x < 1024.0 or u == units[-1]:
            return f"{x:.1f} {u}" if u != "B" else f"{int(x)} B"
        x /= 1024.0
    return f"{x:.1f} TB"


def _get_mem_status_windows() -> tuple[int, int, int, int] | None:
    class MEMORYSTATUSEX(ctypes.Structure):
        _fields_ = [
            ("dwLength", ctypes.c_ulong),
            ("dwMemoryLoad", ctypes.c_ulong),
            ("ullTotalPhys", ctypes.c_ulonglong),
            ("ullAvailPhys", ctypes.c_ulonglong),
            ("ullTotalPageFile", ctypes.c_ulonglong),
            ("ullAvailPageFile", ctypes.c_ulonglong),
            ("ullTotalVirtual", ctypes.c_ulonglong),
            ("ullAvailVirtual", ctypes.c_ulonglong),
            ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
        ]

    stat = MEMORYSTATUSEX()
    stat.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
    ok = ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(stat))  # type: ignore[attr-defined]
    if not ok:
        return None
    return (int(stat.ullAvailPhys), int(stat.ullTotalPhys), int(stat.ullAvailPageFile), int(stat.ullTotalPageFile))


def _dir_size_bytes(path: Path) -> int:
    total = 0
    try:
        for root, dirs, files in os.walk(path):
            for f in files:
                try:
                    total += (Path(root) / f).stat().st_size
                except OSError:
                    pass
    except Exception:
        return 0
    return total


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
        root.title("Vosk -> OSC Transcriber")
        root.geometry("920x700")

        self.audio_q: "queue.Queue[bytes]" = queue.Queue()
        self.running = False
        self.downloading = False
        self.cuda_initialized = False
        self.stream: sd.InputStream | None = None
        self.model: vosk.Model | None = None
        self.recognizer: vosk.KaldiRecognizer | None = None

        # ---------- UI: config ----------
        cfg = tk.Frame(root)
        cfg.pack(fill=tk.X, padx=10, pady=8)

        tk.Label(cfg, text="Model preset:").grid(row=0, column=0, sticky="w")
        self.model_choice = tk.StringVar(value=DEFAULT_MODEL_PRESET)
        self.model_menu = tk.OptionMenu(cfg, self.model_choice, *VOSK_MODELS.keys(), command=self.on_model_choice)
        self.model_menu.grid(row=0, column=1, columnspan=5, sticky="we", padx=6)

        self.btn_download = tk.Button(cfg, text="Download selected", command=self.download_selected_model)
        self.btn_download.grid(row=0, column=6, sticky="w", padx=6)

        self.try_cuda = tk.BooleanVar(value=False)
        tk.Checkbutton(cfg, text="Try CUDA/GPU", variable=self.try_cuda).grid(row=0, column=7, sticky="w")

        self.force_big_model = tk.BooleanVar(value=False)
        tk.Checkbutton(cfg, text="Force huge model", variable=self.force_big_model).grid(row=0, column=8, sticky="w")

        tk.Label(cfg, text="Vosk model path:").grid(row=1, column=0, sticky="w")
        self.model_path = tk.Entry(cfg, width=92)
        self.model_path.grid(row=1, column=1, columnspan=6, sticky="we", padx=6)
        self.model_path.insert(0, best_initial_model_path())

        self.btn_browse_model = tk.Button(cfg, text="Browse", command=self.browse_model_path)
        self.btn_browse_model.grid(row=1, column=7, sticky="w")

        self.model_status = tk.StringVar(value="")
        tk.Label(cfg, textvariable=self.model_status, anchor="w", fg="#555").grid(row=2, column=1, columnspan=7, sticky="we", padx=6)

        tk.Label(cfg, text="Mic:").grid(row=3, column=0, sticky="w")
        self.mic_var = tk.StringVar(value="")
        self.mic_menu = tk.OptionMenu(cfg, self.mic_var, "(loading...)")
        self.mic_menu.grid(row=3, column=1, sticky="we", padx=6)

        self.btn_refresh = tk.Button(cfg, text="Refresh mics", command=self.refresh_mics)
        self.btn_refresh.grid(row=3, column=2, sticky="w")

        tk.Label(cfg, text="OSC IP:").grid(row=4, column=0, sticky="w")
        self.osc_ip = tk.Entry(cfg, width=18)
        self.osc_ip.grid(row=4, column=1, sticky="w", padx=6)
        self.osc_ip.insert(0, "127.0.0.1")

        tk.Label(cfg, text="Port:").grid(row=4, column=2, sticky="w")
        self.osc_port = tk.Entry(cfg, width=8)
        self.osc_port.grid(row=4, column=3, sticky="w", padx=6)
        self.osc_port.insert(0, "3333")

        tk.Label(cfg, text="Payload:").grid(row=4, column=4, sticky="w")
        self.payload_modes = {
            "Channel + text (int,string)": "channel_text",
            "Text only (string)": "text_only",
        }
        payload_choices = tuple(self.payload_modes.keys())
        self.payload_var = tk.StringVar(value=payload_choices[0])
        self.payload_menu = tk.OptionMenu(cfg, self.payload_var, *payload_choices)
        self.payload_menu.grid(row=4, column=5, sticky="we", padx=6)

        tk.Label(cfg, text="Channel:").grid(row=5, column=0, sticky="w")
        self.channel = tk.Spinbox(cfg, from_=0, to=64, width=8)
        self.channel.grid(row=5, column=1, sticky="w", padx=6)
        self.channel.delete(0, "end")
        self.channel.insert(0, "1")

        self.btn_start = tk.Button(cfg, text="Start", command=self.start)
        self.btn_start.grid(row=5, column=2, sticky="w", padx=6)

        self.btn_stop = tk.Button(cfg, text="Stop", command=self.stop, state=tk.DISABLED)
        self.btn_stop.grid(row=5, column=3, sticky="w", padx=6)

        self.btn_test_imports = tk.Button(cfg, text="Test imports", command=self.test_imports)
        self.btn_test_imports.grid(row=5, column=4, sticky="w", padx=6)

        self.btn_test_osc = tk.Button(cfg, text="Test OSC", command=self.test_osc)
        self.btn_test_osc.grid(row=5, column=5, sticky="w", padx=6)

        cfg.columnconfigure(1, weight=1)
        cfg.columnconfigure(5, weight=1)

        # ---------- Log ----------
        self.log = ScrolledText(root, wrap=tk.WORD, height=26)
        self.log.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        self._log(f"[Python] {sys.version.split()[0]}")
        self._log(f"[Executable] {sys.executable}")
        self._log(f"[CWD] {os.getcwd()}")
        self._log("Ready. Choose/download a model, choose mic, click Start.")
        self._log("CUDA note: standard pip Vosk builds are usually CPU-only. If GPU init fails, the script falls back to CPU.")
        self._log("Official models: https://alphacephei.com/vosk/models")
        self._log("If the app dies when loading a huge model: you likely ran out of RAM/pagefile. Use lgraph or enable pagefile.")

        root.protocol("WM_DELETE_WINDOW", self.on_close)

        self.refresh_mics()
        self.on_model_choice(self.model_choice.get())

    # ---------- Logging ----------
    def _log(self, s: str) -> None:
        self.log.insert(tk.END, s + "\n")
        self.log.see(tk.END)

    def _threadsafe_log(self, s: str) -> None:
        self.root.after(0, self._log, s)

    # ---------- Model UI ----------
    def on_model_choice(self, choice: str | None = None) -> None:
        choice = choice or self.model_choice.get()
        info = VOSK_MODELS.get(choice, {})
        model_name = info.get("name", "")
        notes = info.get("notes", "")

        if model_name:
            found = find_local_model_dir(model_name)
            path = found or model_dir_for(model_name)
            self.model_path.delete(0, "end")
            self.model_path.insert(0, str(path))
            installed = "installed" if found else "not installed"
            self.model_status.set(f"{model_name}: {installed}. {notes}")
        else:
            self.model_status.set(notes)

    def browse_model_path(self) -> None:
        selected = filedialog.askdirectory(title="Choose Vosk model folder", initialdir=str(SCRIPT_DIR))
        if selected:
            self.model_choice.set("Custom path below")
            self.model_path.delete(0, "end")
            self.model_path.insert(0, selected)
            self.model_status.set("Using custom model path.")

    def download_selected_model(self) -> None:
        if self.running:
            self._log("[warn] Stop listening before downloading/changing models.")
            return
        if self.downloading:
            self._log("[warn] A model download is already running.")
            return

        choice = self.model_choice.get()
        info = VOSK_MODELS.get(choice, {})
        model_name = info.get("name", "")
        url = info.get("url", "")
        if not model_name or not url:
            self._log("[warn] Select a downloadable preset, or use Browse for a custom model folder.")
            return

        self.downloading = True
        self.btn_download.config(state=tk.DISABLED)
        self._log(f"[model] Preparing {model_name}...")

        def work() -> None:
            try:
                installed = download_and_extract_model(model_name, url, self._threadsafe_log)
                self.root.after(0, self._set_model_path_after_download, installed)
            except Exception as e:
                self.root.after(0, self._log, f"[error] {e}")
            finally:
                self.root.after(0, self._download_finished)

        threading.Thread(target=work, daemon=True).start()

    def _set_model_path_after_download(self, installed: Path) -> None:
        self.model_path.delete(0, "end")
        self.model_path.insert(0, str(installed))
        self.model_status.set(f"Installed and selected: {installed.name}")

    def _download_finished(self) -> None:
        self.downloading = False
        self.btn_download.config(state=tk.NORMAL)

    def _try_init_cuda(self) -> bool:
        if not self.try_cuda.get():
            return False
        if self.cuda_initialized:
            return True
        if not hasattr(vosk, "GpuInit"):
            self._log("[cuda] This vosk package does not expose GpuInit(); using CPU.")
            return False
        try:
            self._log("[cuda] Attempting vosk.GpuInit()...")
            vosk.GpuInit()
            self.cuda_initialized = True
            self._log("[cuda] GPU initialized. If libvosk was built with CUDA, recognition can use it.")
            return True
        except Exception as e:
            self._log(f"[cuda] GPU init failed; using CPU. Details: {e}")
            return False

    def _try_init_cuda_thread(self) -> None:
        if not self.cuda_initialized or not hasattr(vosk, "GpuThreadInit"):
            return
        try:
            vosk.GpuThreadInit()
            self.root.after(0, self._log, "[cuda] Worker thread GPU context initialized.")
        except Exception as e:
            self.root.after(0, self._log, f"[cuda] Worker thread GPU init failed; continuing. Details: {e}")

    def _selected_preset_info(self) -> dict[str, str]:
        return VOSK_MODELS.get(self.model_choice.get(), {})

    def _preflight_memory_for_model(self, model_path: Path) -> bool:
        info = self._selected_preset_info()
        min_free_gb = float(info.get("min_free_gb", "0") or "0")
        rec_free_gb = float(info.get("rec_free_gb", "0") or "0")

        if sys.platform == "win32":
            ms = _get_mem_status_windows()
            if ms:
                avail_phys, total_phys, avail_pf, total_pf = ms
                avail_gb = avail_phys / (1024**3)
                self._log(f"[mem] Free RAM { _fmt_bytes(avail_phys) } / Total { _fmt_bytes(total_phys) }")
                self._log(f"[mem] Free pagefile { _fmt_bytes(avail_pf) } / Total { _fmt_bytes(total_pf) }")

                if min_free_gb > 0 and avail_gb < min_free_gb and not self.force_big_model.get():
                    self._log(f"[mem] Not enough free RAM for this model (needs ~{min_free_gb:.0f} GB free).")
                    self._log("[mem] Use a smaller model (lgraph), close apps, or increase Windows pagefile; then retry.")
                    return False
                if rec_free_gb > 0 and avail_gb < rec_free_gb:
                    self._log(f"[mem] Warning: recommended free RAM for this model is ~{rec_free_gb:.0f} GB; you have ~{avail_gb:.1f} GB.")

        # Heuristic for custom paths: if model folder is huge and free RAM is unknown, warn.
        size = _dir_size_bytes(model_path)
        if size >= 2 * 1024**3:
            self._log(f"[mem] Model folder size is { _fmt_bytes(size) } (large). Loading may spike RAM.")
            if not self.force_big_model.get() and info.get("name", "") == "":
                # Custom model: only warn, don't block.
                pass
        return True

    # ---------- Tests ----------
    def test_imports(self) -> None:
        try:
            import importlib.util  # just to prove it's fine
            self._log("[OK] importlib.util imports")
            self._log("[OK] urllib/zipfile imports")
            self._log("[OK] vosk imports")
            self._log(f"[OK] vosk GPU hooks present: GpuInit={hasattr(vosk, 'GpuInit')} GpuThreadInit={hasattr(vosk, 'GpuThreadInit')}")
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
        values = ", ".join(repr(v) for v in payload) or "empty"
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
        if self.downloading:
            self._log("[warn] Wait for the model download to finish before starting.")
            return

        mp = self.model_path.get().strip().strip('"')
        if not mp:
            self._log("[error] Model path is empty.")
            return
        if not is_vosk_model_dir(Path(mp)):
            self._log(f"[error] Model path does not look like a Vosk model directory:\n  {mp}")
            self._log("Tip: choose a preset and click 'Download selected', or Browse to the folder containing 'am' and 'conf'.")
            return

        model_path = Path(mp)
        if not self._preflight_memory_for_model(model_path):
            try:
                messagebox.showwarning(
                    "Not enough free memory",
                    "This model is likely to crash your system due to RAM/pagefile pressure.\n\n"
                    "Try the lgraph model, close other apps, or increase Windows pagefile.\n"
                    "You can override with 'Force huge model' at your own risk.",
                )
            except Exception:
                pass
            return

        self._try_init_cuda()

        # Load Vosk model
        try:
            self._log(f"Loading Vosk model: {mp}")
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
            self._log("macOS note: check System Settings -> Privacy & Security -> Microphone -> enable Terminal/IDE.")
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
        self._try_init_cuda_thread()

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

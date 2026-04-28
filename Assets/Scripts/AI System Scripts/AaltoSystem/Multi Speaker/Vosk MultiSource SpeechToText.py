"""
Vosk Multi-Source -> OSC Transcriber (Tkinter GUI)

What it does:
- Captures up to 4 microphone sources simultaneously
- Runs one Vosk recognizer per enabled source
- Can split different physical input channels from the same multichannel device
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
import ctypes
import shutil
import tempfile
import zipfile
import time
import tkinter as tk
from tkinter import filedialog
from tkinter import messagebox
from tkinter.scrolledtext import ScrolledText
from collections.abc import Callable, Sequence
from pathlib import Path
from urllib.error import URLError
from urllib.request import urlretrieve


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
require("numpy")

import sounddevice as sd
import vosk
import numpy as np


# -------------------- Model helpers --------------------

SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_DIR = SCRIPT_DIR.parent  # keep models shared with the single-source script
MODEL_BASE_URL = "https://alphacephei.com/vosk/models"
MODEL_INSTALL_DIR = PROJECT_DIR
DEFAULT_MODEL_NAME = "vosk-model-en-us-0.22-lgraph"
DEFAULT_MODEL_PRESET = "English US 0.22 lgraph (better, 128 MB)"

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

    nested = direct / model_name
    if is_vosk_model_dir(nested):
        return nested

    for match in MODEL_INSTALL_DIR.glob(f"**/{model_name}"):
        if is_vosk_model_dir(match):
            return match
    return None


def best_initial_model_path() -> str:
    found = find_local_model_dir(DEFAULT_MODEL_NAME)
    if found:
        return str(found)
    return str(model_dir_for(DEFAULT_MODEL_NAME))


def download_and_extract_model(model_name: str, url: str, log: Callable[[str], None]) -> Path:
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
SAMPLE_RATE_CANDIDATES = [16000, 48000, 44100, 32000]


class SourceState:
    def __init__(self) -> None:
        self.enabled = tk.BooleanVar(value=False)
        self.speaker_id = tk.StringVar(value="")
        self.channel = tk.IntVar(value=1)
        self.input_channel = tk.IntVar(value=1)
        self.mic = tk.StringVar(value="")
        self.audio_q: "queue.Queue[bytes]" = queue.Queue()
        self.stream: sd.InputStream | None = None
        self.recognizer: vosk.KaldiRecognizer | None = None
        self.last_partial: str = ""
        self.sample_rate: int = SAMPLE_RATE
        self.last_rms: float = 0.0
        self.last_peak: int = 0
        self.last_debug_log_ts: float = 0.0


class VoskMultiSourceApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("Vosk Multi-Source -> OSC")
        self.root.geometry("980x760")

        self.running = False
        self.downloading = False
        self.cuda_initialized = False
        self.model: vosk.Model | None = None
        self.sources: list[SourceState] = [SourceState() for _ in range(SOURCE_COUNT)]

        for i, src in enumerate(self.sources, start=1):
            src.speaker_id.set(f"actor_{i}")
            src.channel.set(i)
            src.input_channel.set(i)
            src.enabled.set(i <= 2)

        self.osc_ip = tk.StringVar(value="127.0.0.1")
        self.osc_port = tk.StringVar(value="3333")
        self.osc_address = tk.StringVar(value="/speech")
        self.payload_mode = tk.StringVar(value="speaker_text")
        self.show_partials = tk.BooleanVar(value=False)
        self.audio_debug = tk.BooleanVar(value=False)
        self.only_active_mics = tk.BooleanVar(value=False)
        self.model_choice = tk.StringVar(value=DEFAULT_MODEL_PRESET)
        self.try_cuda = tk.BooleanVar(value=False)
        self.force_big_model = tk.BooleanVar(value=False)
        self.model_path = tk.StringVar(value=best_initial_model_path())
        self.model_status = tk.StringVar(value="")

        self._build_ui()
        self.refresh_mics()
        self.on_model_choice(self.model_choice.get())

    def _build_ui(self) -> None:
        cfg = tk.Frame(self.root)
        cfg.pack(fill=tk.X, padx=10, pady=8)

        tk.Label(cfg, text="Model preset:").grid(row=0, column=0, sticky="w")
        tk.OptionMenu(cfg, self.model_choice, *VOSK_MODELS.keys(), command=self.on_model_choice).grid(
            row=0, column=1, columnspan=4, sticky="we", padx=6
        )
        self.btn_download = tk.Button(cfg, text="Download selected", command=self.download_selected_model)
        self.btn_download.grid(row=0, column=5, sticky="w", padx=6)
        tk.Checkbutton(cfg, text="Try CUDA/GPU", variable=self.try_cuda).grid(row=0, column=6, sticky="w")
        tk.Checkbutton(cfg, text="Force huge model", variable=self.force_big_model).grid(row=0, column=7, sticky="w")

        tk.Label(cfg, text="Vosk model path:").grid(row=1, column=0, sticky="w")
        tk.Entry(cfg, textvariable=self.model_path, width=92).grid(row=1, column=1, columnspan=5, sticky="we", padx=6)
        tk.Button(cfg, text="Browse", command=self.browse_model_path).grid(row=1, column=6, sticky="w")

        tk.Label(cfg, textvariable=self.model_status, anchor="w", fg="#555").grid(row=2, column=1, columnspan=6, sticky="we", padx=6)

        tk.Label(cfg, text="OSC IP:").grid(row=3, column=0, sticky="w")
        tk.Entry(cfg, textvariable=self.osc_ip, width=20).grid(row=3, column=1, sticky="w", padx=6)

        tk.Label(cfg, text="Port:").grid(row=3, column=2, sticky="w")
        tk.Entry(cfg, textvariable=self.osc_port, width=8).grid(row=3, column=3, sticky="w", padx=6)

        tk.Label(cfg, text="Address:").grid(row=3, column=4, sticky="w")
        tk.Entry(cfg, textvariable=self.osc_address, width=14).grid(row=3, column=5, sticky="w", padx=6)

        tk.Label(cfg, text="Payload:").grid(row=3, column=6, sticky="w")
        tk.OptionMenu(cfg, self.payload_mode, "speaker_text", "channel_text").grid(row=3, column=7, sticky="we", padx=6)

        tk.Button(cfg, text="Refresh mics", command=self.refresh_mics).grid(row=3, column=8, sticky="e")

        tk.Checkbutton(
            cfg,
            text="Show partial preview",
            variable=self.show_partials).grid(row=4, column=0, columnspan=3, sticky="w")
        tk.Checkbutton(
            cfg,
            text="Audio debug logs",
            variable=self.audio_debug).grid(row=4, column=3, columnspan=2, sticky="w")
        tk.Checkbutton(
            cfg,
            text="Only show active mics",
            variable=self.only_active_mics).grid(row=4, column=5, columnspan=2, sticky="w")

        cfg.columnconfigure(1, weight=1)
        cfg.columnconfigure(7, weight=1)

        sources_frame = tk.LabelFrame(self.root, text="Sources (up to 4)")
        sources_frame.pack(fill=tk.X, padx=10, pady=6)

        headers = ["Use", "Speaker ID", "OSC Chan", "Input Ch", "Mic device"]
        for c, h in enumerate(headers):
            tk.Label(sources_frame, text=h).grid(row=0, column=c, sticky="w", padx=4)

        self.mic_menus: list[tk.OptionMenu] = []
        for i, src in enumerate(self.sources, start=1):
            tk.Checkbutton(sources_frame, text=f"Source {i}", variable=src.enabled).grid(row=i, column=0, sticky="w", padx=4)
            tk.Entry(sources_frame, textvariable=src.speaker_id, width=18).grid(row=i, column=1, sticky="w", padx=4)
            tk.Spinbox(sources_frame, from_=1, to=64, textvariable=src.channel, width=6).grid(row=i, column=2, sticky="w", padx=4)
            tk.Spinbox(sources_frame, from_=1, to=64, textvariable=src.input_channel, width=6).grid(row=i, column=3, sticky="w", padx=4)
            m = tk.OptionMenu(sources_frame, src.mic, "(loading...)")
            m.grid(row=i, column=4, sticky="we", padx=4)
            self.mic_menus.append(m)

        controls = tk.Frame(self.root)
        controls.pack(fill=tk.X, padx=10, pady=6)

        self.btn_start = tk.Button(controls, text="Start", command=self.start)
        self.btn_start.pack(side=tk.LEFT)

        self.btn_stop = tk.Button(controls, text="Stop", command=self.stop, state=tk.DISABLED)
        self.btn_stop.pack(side=tk.LEFT, padx=8)

        self.btn_test_imports = tk.Button(controls, text="Test imports", command=self.test_imports)
        self.btn_test_imports.pack(side=tk.LEFT, padx=8)

        self.btn_test_osc = tk.Button(controls, text="Test OSC", command=self.test_osc)
        self.btn_test_osc.pack(side=tk.LEFT, padx=8)

        self.btn_auto_assign = tk.Button(controls, text="Auto assign inputs", command=self.auto_assign_inputs)
        self.btn_auto_assign.pack(side=tk.LEFT, padx=8)

        self.btn_probe_audio = tk.Button(controls, text="Probe channels", command=self.probe_selected_channels)
        self.btn_probe_audio.pack(side=tk.LEFT, padx=8)
        self.btn_auto_assign_available = tk.Button(
            controls, text="Auto assign available", command=self.auto_assign_available_inputs
        )
        self.btn_auto_assign_available.pack(side=tk.LEFT, padx=8)

        self.log = ScrolledText(self.root, wrap=tk.WORD, height=26)
        self.log.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        self._log(f"[Python] {sys.version.split()[0]}")
        self._log(f"[Executable] {sys.executable}")
        self._log(f"[CWD] {os.getcwd()}")
        self._log("Ready. Enable sources, assign mics, click Start.")
        self._log("If the app dies when loading a huge model: you likely ran out of RAM/pagefile. Use lgraph or enable pagefile.")
        self._log("CUDA note: standard pip Vosk builds are usually CPU-only. If GPU init fails, the script falls back to CPU.")
        self._log("Official models: https://alphacephei.com/vosk/models")

        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

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
            self.model_path.set(str(path))
            installed = "installed" if found else "not installed"
            self.model_status.set(f"{model_name}: {installed}. {notes}")
        else:
            self.model_status.set(notes)

    def browse_model_path(self) -> None:
        selected = filedialog.askdirectory(title="Choose Vosk model folder", initialdir=str(MODEL_INSTALL_DIR))
        if selected:
            self.model_choice.set("Custom path below")
            self.model_path.set(selected)
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
                self.root.after(0, self.model_path.set, str(installed))
                self.root.after(0, self.model_status.set, f"Installed and selected: {installed.name}")
            except Exception as e:
                self.root.after(0, self._log, f"[error] {e}")
            finally:
                self.root.after(0, self._download_finished)

        threading.Thread(target=work, daemon=True).start()

    def _download_finished(self) -> None:
        self.downloading = False
        self.btn_download.config(state=tk.NORMAL)

    # ---------- CUDA ----------
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

    def _try_init_cuda_thread(self, index: int) -> None:
        if not self.cuda_initialized or not hasattr(vosk, "GpuThreadInit"):
            return
        try:
            vosk.GpuThreadInit()
            self.root.after(0, self._log, f"[cuda] Worker thread GPU context initialized (source {index}).")
        except Exception as e:
            self.root.after(0, self._log, f"[cuda] Worker thread GPU init failed (source {index}); continuing. Details: {e}")

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

        size = _dir_size_bytes(model_path)
        if size >= 2 * 1024**3:
            self._log(f"[mem] Model folder size is { _fmt_bytes(size) } (large). Loading may spike RAM.")
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

    def _list_mics(self) -> list[str]:
        items: list[str] = []
        devices = sd.query_devices()
        for i, d in enumerate(devices):
            if d.get("max_input_channels", 0) > 0:
                ch = int(d.get("max_input_channels", 0) or 0)
                label = f"{i}: {d.get('name', '(unknown)')} [in:{ch}]"

                if self.only_active_mics.get():
                    try:
                        active = self._probe_device_activity(i, max_channels=min(ch, 8), duration=0.4)
                    except Exception:
                        active = []

                    if not active:
                        continue
                    # show active channels in label
                    act_str = ",".join(str(x) for x in active)
                    label = f"{i}: {d.get('name', '(unknown)')} [in:{ch} active:{len(active)} ch:{act_str}]"

                items.append(label)
        return items or ["(no input devices)"]

    def _probe_device_activity(self, mic_index: int, max_channels: int = 2, duration: float = 0.4, threshold: float = 500.0) -> list[int]:
        # Returns 1-based channel indices that have RMS above threshold
        info = sd.query_devices(mic_index)
        max_in = int(info.get("max_input_channels", 0) or 0)
        if max_in <= 0:
            return []

        channels = min(max_in, int(max_channels))
        sr = self._pick_working_samplerate(mic_index, channels)
        if sr is None:
            return []

        frames = int(sr * max(0.1, min(duration, 1.0)))

        try:
            with sd.InputStream(device=mic_index, channels=channels, samplerate=sr, dtype="int16") as stream:
                data, _ = stream.read(frames)
        except Exception:
            return []

        try:
            arr = np.asarray(data, dtype=np.int16).astype(np.float32)
            if arr.ndim == 1:
                arr = arr.reshape(-1, 1)
            rms = np.sqrt(np.mean(arr * arr, axis=0))
            active = [i + 1 for i, v in enumerate(rms) if float(v) >= float(threshold)]
            return active
        except Exception:
            return []

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

    def auto_assign_inputs(self) -> None:
        for i, src in enumerate(self.sources, start=1):
            src.input_channel.set(i)
        self._log("Auto assign: Input Ch mapped to Source index (1->1, 2->2, ...).")

    def auto_assign_available_inputs(self) -> None:
        # Build available candidates: (mic_index, ch, device_name, max_in, active_flag)
        devices = sd.query_devices()
        candidates: list[tuple[int, int, str, int, bool]] = []

        for i, d in enumerate(devices):
            max_in = int(d.get("max_input_channels", 0) or 0)
            if max_in <= 0:
                continue
            name = str(d.get("name", "(unknown)"))

            # Prefer active channels if requested
            active_channels: list[int] = []
            if self.only_active_mics.get():
                try:
                    active_channels = self._probe_device_activity(i, max_channels=min(max_in, 8), duration=0.3)
                except Exception:
                    active_channels = []

            if active_channels:
                for ch in active_channels:
                    candidates.append((i, ch, name, max_in, True))
            else:
                # Add all channels up to a sensible limit
                for ch in range(1, min(max_in, 8) + 1):
                    candidates.append((i, ch, name, max_in, False))

        if not candidates:
            self._log("[autoassign] No available input channels found.")
            return

        # Remove duplicates and prefer active ones first
        candidates_sorted = sorted(candidates, key=lambda x: (not x[4], x[0], x[1]))

        # Refresh current mic menu items to find labels
        menu_items = self._list_mics()

        used: set[tuple[int, int]] = set()
        assign_count = 0
        for src in self.sources:
            if not src.enabled.get():
                continue
            # find first unused candidate
            picked = None
            for cand in candidates_sorted:
                mic_idx, ch, name, max_in, active = cand
                if (mic_idx, ch) in used:
                    continue
                picked = cand
                break

            if picked is None:
                break

            mic_idx, ch, name, max_in, active = picked
            # find a menu item that starts with "{mic_idx}:"
            match_label = next((it for it in menu_items if it.startswith(f"{mic_idx}:")), None)
            if match_label is None:
                match_label = f"{mic_idx}: {name} [in:{max_in}]"

            src.mic.set(match_label)
            src.input_channel.set(ch)
            used.add((mic_idx, ch))
            assign_count += 1
            self._log(f"[autoassign] Assigned source -> mic={mic_idx} ({name}) input_ch={ch} active={active}")

        if assign_count == 0:
            self._log("[autoassign] No enabled sources were assigned.")
        elif assign_count < sum(1 for s in self.sources if s.enabled.get()):
            self._log("[autoassign] Partial assignment: not enough distinct input channels for all enabled sources.")
        else:
            self._log(f"[autoassign] Assigned {assign_count} sources.")

    def _selected_mic_index(self, src: SourceState) -> int | None:
        sel = src.mic.get()
        try:
            return int(sel.split(":")[0]) if ":" in sel else None
        except Exception:
            return None

    def _device_info(self, mic_index: int) -> dict[str, object]:
        info = sd.query_devices(mic_index)
        hostapis = sd.query_hostapis()
        host_idx = int(info.get("hostapi", -1) or -1)
        host_name = "unknown"
        if 0 <= host_idx < len(hostapis):
            host_name = str(hostapis[host_idx].get("name", "unknown"))
        return {
            "name": str(info.get("name", "(unknown)")),
            "hostapi": host_name,
            "max_input_channels": int(info.get("max_input_channels", 0) or 0),
            "default_samplerate": int(float(info.get("default_samplerate", 0) or 0)),
        }

    def _mic_input_channel_count(self, mic_index: int) -> int:
        try:
            info = sd.query_devices(mic_index)
            return int(info.get("max_input_channels", 0) or 0)
        except Exception:
            return 0

    def _resample_int16(self, arr: "np.ndarray", src_sr: int, tgt_sr: int) -> "np.ndarray":
        # arr: 1-D int16 numpy array
        if src_sr == tgt_sr:
            return arr
        if arr.size == 0:
            return arr
        try:
            src_len = arr.shape[0]
            duration = src_len / float(src_sr)
            tgt_len = int(round(duration * float(tgt_sr)))
            if tgt_len <= 0:
                return np.array([], dtype=np.int16)
            # Linear interpolation resampling
            old_pos = np.linspace(0.0, 1.0, num=src_len, endpoint=False)
            new_pos = np.linspace(0.0, 1.0, num=tgt_len, endpoint=False)
            res = np.interp(new_pos, old_pos, arr.astype(np.float32)).astype(np.int16)
            return res
        except Exception:
            return arr

    def _samplerate_candidates_for(self, mic_index: int) -> list[int]:
        info = self._device_info(mic_index)
        default_sr = int(info.get("default_samplerate", 0) or 0)
        candidates: list[int] = []
        if default_sr > 0:
            candidates.append(default_sr)
        candidates.extend(SAMPLE_RATE_CANDIDATES)

        uniq: list[int] = []
        for sr in candidates:
            if sr > 0 and sr not in uniq:
                uniq.append(sr)
        return uniq

    def _pick_working_samplerate(self, mic_index: int, channels: int) -> int | None:
        for sr in self._samplerate_candidates_for(mic_index):
            try:
                sd.check_input_settings(device=mic_index, channels=channels, samplerate=sr, dtype="int16")
                if self.audio_debug.get():
                    self._log(f"[audio dbg] mic={mic_index} accepts sr={sr} ch={channels}")
                return sr
            except Exception as e:
                if self.audio_debug.get():
                    self._log(f"[audio dbg] mic={mic_index} rejects sr={sr} ch={channels}: {e}")
        return None

    def _log_duplicate_input_channel_warnings(self) -> None:
        used: dict[tuple[int, int], list[int]] = {}
        for i, src in enumerate(self.sources, start=1):
            if not src.enabled.get():
                continue
            mic_index = self._selected_mic_index(src)
            if mic_index is None:
                continue
            input_channel = int(src.input_channel.get())
            key = (mic_index, input_channel)
            used.setdefault(key, []).append(i)

        for (mic_index, input_channel), source_ids in used.items():
            if len(source_ids) > 1:
                self._log(
                    f"[warn] Sources {source_ids} use same mic index={mic_index} input_ch={input_channel}. "
                    "They will transcribe the same physical input."
                )

    def probe_selected_channels(self) -> None:
        if self.running:
            self._log("[warn] Stop listening before probing channels.")
            return

        selected_src = next((s for s in self.sources if s.enabled.get()), self.sources[0])
        mic_index = self._selected_mic_index(selected_src)
        if mic_index is None:
            self._log("[probe] No valid mic selected.")
            return

        def work() -> None:
            try:
                info = self._device_info(mic_index)
                max_in = int(info["max_input_channels"])
                self._threadsafe_log(
                    f"[probe] mic={mic_index} name={info['name']} host={info['hostapi']} "
                    f"max_in={max_in} default_sr={info['default_samplerate']}"
                )

                if max_in < 2:
                    self._threadsafe_log("[probe] Device exposes fewer than 2 input channels. No split test possible.")
                    return

                sr = self._pick_working_samplerate(mic_index, min(max_in, 2))
                if sr is None:
                    self._threadsafe_log("[probe] No working samplerate found for 2-channel probe.")
                    return

                self._threadsafe_log(
                    "[probe] Capturing 2-channel audio for 3 seconds. Speak into input 1, then input 2."
                )

                frames = int(sr * 3)
                with sd.InputStream(device=mic_index, channels=2, samplerate=sr, dtype="int16") as stream:
                    data, _ = stream.read(frames)

                ch1 = data[:, 0].astype(np.float32)
                ch2 = data[:, 1].astype(np.float32)
                rms1 = float(np.sqrt(np.mean(ch1 * ch1)))
                rms2 = float(np.sqrt(np.mean(ch2 * ch2)))
                peak1 = int(np.max(np.abs(ch1))) if ch1.size else 0
                peak2 = int(np.max(np.abs(ch2))) if ch2.size else 0

                corr = 0.0
                if rms1 > 5.0 and rms2 > 5.0:
                    corr = float(np.corrcoef(ch1, ch2)[0, 1])

                self._threadsafe_log(
                    f"[probe] sr={sr} rms1={rms1:.1f} peak1={peak1} rms2={rms2:.1f} peak2={peak2} corr={corr:.3f}"
                )

                if rms1 < 5.0 and rms2 < 5.0:
                    self._threadsafe_log("[probe] Both channels are nearly silent. Repeat probe while speaking louder.")
                elif corr > 0.98 and rms1 > 20.0 and rms2 > 20.0:
                    self._threadsafe_log(
                        "[probe] Channels look strongly mirrored. Driver routing is likely duplicating the same signal on ch1/ch2."
                    )
                else:
                    self._threadsafe_log(
                        "[probe] Channels are not strongly mirrored. Independent channel capture appears possible."
                    )
            except Exception as e:
                self._threadsafe_log(f"[probe] Failed: {e}")

        threading.Thread(target=work, daemon=True).start()

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
        if self.downloading:
            self._log("[warn] Wait for the model download to finish before starting.")
            return

        model_path = self.model_path.get().strip().strip('"')
        if not model_path or not is_vosk_model_dir(Path(model_path)):
            self._log(f"[error] Model path does not look like a Vosk model directory: {model_path}")
            self._log("Tip: choose a preset and click 'Download selected', or Browse to the folder containing 'am' and 'conf'.")
            return

        if not self._preflight_memory_for_model(Path(model_path)):
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
        self._log_duplicate_input_channel_warnings()

        try:
            self._log(f"Loading Vosk model: {model_path}")
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
                input_channel = int(src.input_channel.get())
                if input_channel < 1:
                    self._log(f"[warn] Source {i} has invalid Input Ch={input_channel}, skipping.")
                    continue

                device_inputs = self._mic_input_channel_count(mic_index)
                if device_inputs <= 0:
                    self._log(f"[warn] Source {i} device reports no input channels, skipping.")
                    continue

                if input_channel > device_inputs:
                    self._log(
                        f"[warn] Source {i} Input Ch={input_channel} exceeds device max in={device_inputs}. "
                        f"Pick 1..{device_inputs}. Skipping."
                    )
                    continue

                mic_info = self._device_info(mic_index)
                if self.audio_debug.get():
                    self._log(
                        f"[audio dbg] Source {i} mic={mic_index} name={mic_info['name']} host={mic_info['hostapi']} "
                        f"max_in={mic_info['max_input_channels']} default_sr={mic_info['default_samplerate']}"
                    )

                stream_sr = self._pick_working_samplerate(mic_index, device_inputs)
                if stream_sr is None:
                    self._log(
                        f"[error] Source {i} no working samplerate found for mic={mic_index} channels={device_inputs}."
                    )
                    continue

                src.sample_rate = int(stream_sr)
                # Use model sample rate for recognizer (resample audio to this rate in worker)
                src.recognizer = vosk.KaldiRecognizer(self.model, float(SAMPLE_RATE))
                src.audio_q = queue.Queue()
                src.stream = sd.InputStream(
                    samplerate=src.sample_rate,
                    channels=device_inputs,
                    dtype="int16",
                    callback=self._make_audio_callback(src, i, input_channel),
                    device=mic_index,
                )
                src.stream.start()
                threading.Thread(target=self._worker, args=(src, i), daemon=True).start()
                enabled_count += 1
                self._log(
                    f"[OK] Source {i} started on mic={src.mic.get()} input_ch={input_channel} "
                    f"speaker_id={src.speaker_id.get().strip()} osc_channel={src.channel.get()} sr={src.sample_rate}"
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

    def _make_audio_callback(self, src: SourceState, index: int, input_channel: int):
        def cb(indata, frames, time_info, status) -> None:
            if status:
                self.root.after(0, self._log, f"[audio status] source {index}: {status}")
            try:
                # Pick one physical input channel from a multichannel capture block.
                if getattr(indata, "ndim", 1) <= 1:
                    mono = indata
                else:
                    ch_idx = max(0, min(int(input_channel) - 1, int(indata.shape[1]) - 1))
                    mono = indata[:, ch_idx]

                if self.audio_debug.get():
                    m = mono.astype(np.float32)
                    src.last_peak = int(np.max(np.abs(m))) if m.size else 0
                    src.last_rms = float(np.sqrt(np.mean(m * m))) if m.size else 0.0

                # Enqueue tuple (bytes, source_samplerate) so worker can resample to model rate
                src.audio_q.put((mono.tobytes(), int(src.sample_rate)))
            except Exception as e:
                self.root.after(0, self._log, f"[audio status] source {index} channel split error: {e}")

        return cb

    def _worker(self, src: SourceState, index: int) -> None:
        self._try_init_cuda_thread(index)
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

            if self.audio_debug.get():
                now = time.time()
                if now - src.last_debug_log_ts >= 1.0:
                    src.last_debug_log_ts = now
                    self.root.after(
                        0,
                        self._log,
                        f"[audio dbg] source {index} q={src.audio_q.qsize()} rms={src.last_rms:.1f} peak={src.last_peak} sr={src.sample_rate}",
                    )

            # data can be raw bytes or (bytes, src_samplerate)
            raw = None
            src_sr = int(src.sample_rate)
            if isinstance(data, tuple) or isinstance(data, list):
                try:
                    raw_bytes, src_sr = data
                    raw = raw_bytes
                except Exception:
                    raw = None
            else:
                raw = data

            if raw is None:
                continue

            try:
                arr = np.frombuffer(raw, dtype=np.int16)
                if int(src_sr) != int(SAMPLE_RATE):
                    arr = self._resample_int16(arr, int(src_sr), int(SAMPLE_RATE))
                data_to_feed = arr.tobytes()
            except Exception:
                data_to_feed = raw

            if rec.AcceptWaveform(data_to_feed):
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

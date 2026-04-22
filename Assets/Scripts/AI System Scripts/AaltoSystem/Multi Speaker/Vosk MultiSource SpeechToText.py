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
        self.downloading = False
        self.cuda_initialized = False
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

        cfg.columnconfigure(1, weight=1)
        cfg.columnconfigure(7, weight=1)

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

        self.btn_test_imports = tk.Button(controls, text="Test imports", command=self.test_imports)
        self.btn_test_imports.pack(side=tk.LEFT, padx=8)

        self.btn_test_osc = tk.Button(controls, text="Test OSC", command=self.test_osc)
        self.btn_test_osc.pack(side=tk.LEFT, padx=8)

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

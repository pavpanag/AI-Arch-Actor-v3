"""
Vosk Audio-Note Transcriber (sidecar)

Why this exists:
- Unity C# does not run Vosk directly in this project setup.
- Audio notes are saved as WAV by AaltoLaunchSessionLogger.
- This script is designed for end-of-session processing and runs only when manually triggered.

Outputs written into each session folder:
- audio-note-transcripts.jsonl      (canonical transcript sidecar; source-of-truth for transcripts)
- _derived/audio-note-transcripts.csv        (flat export)
- audio-notes/transcripts/*.txt     (one plain transcript file per WAV)
- _derived/audio-note-transcriber-state.json (processed-file cache)
- _derived/events.audio-note-transcripts.jsonl (synthetic transcript events)
- events.enriched.jsonl             (base events + transcript events, sorted)
- _derived/events.enriched.csv               (flat enriched export)

This keeps transcription metadata in the same session folder and inserts transcript
events at timing-aligned positions in enriched datasets.
"""

from __future__ import annotations

import csv
import json
import os
import re
import socket
import struct
import sys
import time
import wave
import audioop
import tkinter as tk
from tkinter import filedialog
from tkinter.scrolledtext import ScrolledText
from collections.abc import Sequence

try:
    import winsound
except Exception:
    winsound = None


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


require("vosk")
import vosk


TARGET_SAMPLE_RATE = 16000
CHUNK_FRAMES = 4000
SESSION_PREFIX = "session_"
SESSION_NAME_PATTERN = re.compile(r"^(?:RUN_\d+__)?session_", re.IGNORECASE)
LAUNCH_LOGS_FOLDER_NAME = "AaltoLaunchLogs"
DERIVED_DIR_NAME = "_derived"
STATE_FILE_NAME = "audio-note-transcriber-state.json"
TRANSCRIPTS_JSONL = "audio-note-transcripts.jsonl"
TRANSCRIPTS_CSV = "audio-note-transcripts.csv"
OSC_ADDRESS_TRANSCRIPT = "/audio_note_transcript"
BASE_EVENTS_JSONL = "events.jsonl"
ENRICHED_EVENTS_JSONL = "events.enriched.jsonl"
ENRICHED_EVENTS_CSV = "events.enriched.csv"
TRANSCRIPT_EVENTS_JSONL = "events.audio-note-transcripts.jsonl"
OUTPUT_GUIDE_MD = "README-outputs.md"
LOGS_WITHOUT_TRANSCRIPTS_JSONL = "logs.without-transcription.jsonl"
LOGS_WITH_TRANSCRIPTS_JSONL = "logs.with-transcription.jsonl"
SESSION_SUMMARY_MD = "session-summary.md"


def _osc_pad4(n: int) -> int:
    return (4 - (n % 4)) % 4


def _osc_pack_str(s: str) -> bytes:
    b = (s or "").encode("utf-8") + b"\x00"
    return b + (b"\x00" * _osc_pad4(len(b)))


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
        return struct.pack(">i", int(value))
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


def utc_now_iso() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def make_dir(path: str) -> None:
    if path and not os.path.isdir(path):
        os.makedirs(path, exist_ok=True)


def session_derived_dir(session_path: str) -> str:
    return os.path.join(session_path, DERIVED_DIR_NAME)


def session_derived_file(session_path: str, file_name: str) -> str:
    return os.path.join(session_derived_dir(session_path), file_name)


def normalize_run_id(run_number: int, run_tag: str) -> str:
    if isinstance(run_number, int) and run_number > 0:
        return f"RUN_{run_number:03d}"

    text = (run_tag or "").strip().upper().replace(" ", "_")
    if text.startswith("RUN_"):
        return text
    if text.startswith("RUN"):
        suffix = text[3:].strip(" _")
        if suffix.isdigit():
            return f"RUN_{int(suffix):03d}"
    return "RUN_000"


def canonical_transcript_txt_name(wav_file: str) -> str:
    base = os.path.splitext(os.path.basename(wav_file or ""))[0]
    match = re.match(r"^audio_note_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})__(\d+)(?:__.*)?$", base)
    if match:
        stamp = match.group(1)
        note_id = int(match.group(2))
        return f"audio_note_{stamp}__{note_id:03d}__audio_note.txt"
    return f"{base}.txt"


def is_session_folder_name(name: str) -> bool:
    return bool(SESSION_NAME_PATTERN.match((name or "").strip()))


def parse_audio_note_id(file_name: str) -> int:
    # Expected pattern from logger: audio_note_<stamp>__<id>__<label>.wav
    m = re.search(r"__([0-9]{3,})__", file_name)
    if not m:
        return -1
    try:
        return int(m.group(1))
    except Exception:
        return -1


def transcribe_wav(model: vosk.Model, wav_path: str) -> str:
    with wave.open(wav_path, "rb") as wf:
        channels = wf.getnchannels()
        sample_width = wf.getsampwidth()
        sample_rate = wf.getframerate()

        if sample_width != 2:
            raise RuntimeError(f"Unsupported WAV sample width ({sample_width * 8}-bit). Expected 16-bit PCM.")

        recognizer = vosk.KaldiRecognizer(model, TARGET_SAMPLE_RATE)
        collected_parts: list[str] = []

        while True:
            raw = wf.readframes(CHUNK_FRAMES)
            if not raw:
                break

            # Convert to mono 16-bit
            if channels > 1:
                raw = audioop.tomono(raw, sample_width, 0.5, 0.5)

            # Resample to 16k for Vosk model
            if sample_rate != TARGET_SAMPLE_RATE:
                raw, _ = audioop.ratecv(raw, sample_width, 1, sample_rate, TARGET_SAMPLE_RATE, None)

            if recognizer.AcceptWaveform(raw):
                r = json.loads(recognizer.Result())
                text = (r.get("text") or "").strip()
                if text:
                    collected_parts.append(text)

        final_r = json.loads(recognizer.FinalResult())
        final_text = (final_r.get("text") or "").strip()
        if final_text:
            collected_parts.append(final_text)

        return " ".join(part for part in collected_parts if part).strip()


class AudioNoteTranscriberApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("Vosk Audio-Note Transcriber")
        self.root.geometry("980x720")

        self.running = False
        self.model: vosk.Model | None = None
        self.processed_by_session: dict[str, dict[str, dict[str, object]]] = {}
        self._resolved_root_path = ""

        self.root_path_var = tk.StringVar(value=os.path.join(os.path.expanduser("~"), "AppData", "LocalLow"))
        self.model_path_var = tk.StringVar(value=r"C:\Users\pavpa\AI Arch Actor v3\Assets\Scripts\AI System Scripts\AaltoSystem\vosk-model-small-en-us-0.15\vosk-model-small-en-us-0.15")
        self.poll_seconds_var = tk.StringVar(value="2.0")
        self.send_live_osc_var = tk.BooleanVar(value=False)
        self.osc_ip_var = tk.StringVar(value="127.0.0.1")
        self.osc_port_var = tk.StringVar(value="3333")
        self.osc_address_var = tk.StringVar(value=OSC_ADDRESS_TRANSCRIPT)

        self.selected_record_label_var = tk.StringVar(value="No transcript record selected.")
        self.correction_records: list[dict[str, object]] = []
        self.selected_correction_index: int | None = None

        self._build_ui()
        self._log(f"[Python] {sys.version.split()[0]}")
        self._log(f"[Executable] {sys.executable}")
        self._log("Set Launch Logs root + Vosk model path, then click Start.")

        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

    def _build_ui(self) -> None:
        cfg = tk.Frame(self.root)
        cfg.pack(fill=tk.X, padx=10, pady=8)

        tk.Label(cfg, text="Launch Logs Root:").grid(row=0, column=0, sticky="w")
        tk.Entry(cfg, textvariable=self.root_path_var, width=92).grid(row=0, column=1, sticky="we", padx=6)
        tk.Button(cfg, text="Browse", command=self.browse_root).grid(row=0, column=2, sticky="w")
        tk.Button(cfg, text="Auto Detect", command=self.auto_detect_root).grid(row=0, column=3, sticky="w")

        tk.Label(cfg, text="Vosk Model Path:").grid(row=1, column=0, sticky="w")
        tk.Entry(cfg, textvariable=self.model_path_var, width=92).grid(row=1, column=1, sticky="we", padx=6)
        tk.Button(cfg, text="Browse", command=self.browse_model).grid(row=1, column=2, sticky="w")

        tk.Label(cfg, text="Poll seconds:").grid(row=2, column=0, sticky="w")
        tk.Entry(cfg, textvariable=self.poll_seconds_var, width=8).grid(row=2, column=1, sticky="w", padx=6)
        tk.Button(cfg, text="Validate Root", command=self.validate_root).grid(row=2, column=2, sticky="w")

        tk.Checkbutton(cfg, text="Optional live OSC transcripts", variable=self.send_live_osc_var).grid(row=3, column=0, sticky="w")
        tk.Label(cfg, text="OSC IP:").grid(row=3, column=1, sticky="e")
        tk.Entry(cfg, textvariable=self.osc_ip_var, width=14).grid(row=3, column=2, sticky="w", padx=6)
        tk.Label(cfg, text="Port:").grid(row=3, column=3, sticky="e")
        tk.Entry(cfg, textvariable=self.osc_port_var, width=8).grid(row=3, column=4, sticky="w", padx=6)
        tk.Label(cfg, text="Address:").grid(row=3, column=5, sticky="e")
        tk.Entry(cfg, textvariable=self.osc_address_var, width=26).grid(row=3, column=6, sticky="w", padx=6)

        controls = tk.Frame(self.root)
        controls.pack(fill=tk.X, padx=10, pady=6)

        self.btn_start = tk.Button(controls, text="Transcribe Now", command=self.start)
        self.btn_start.pack(side=tk.LEFT)

        self.btn_scan_once = tk.Button(controls, text="Scan Once", command=self.scan_once)
        self.btn_scan_once.pack(side=tk.LEFT, padx=8)

        self.btn_test_osc = tk.Button(controls, text="Test OSC", command=self.test_osc)
        self.btn_test_osc.pack(side=tk.LEFT, padx=8)

        correction = tk.LabelFrame(self.root, text="Transcript Review / Correction")
        correction.pack(fill=tk.BOTH, expand=False, padx=10, pady=6)

        correction_controls = tk.Frame(correction)
        correction_controls.pack(fill=tk.X, padx=8, pady=6)
        tk.Button(correction_controls, text="Refresh Records", command=self._refresh_correction_records).pack(side=tk.LEFT)
        tk.Button(correction_controls, text="Play Audio", command=self._play_selected_audio).pack(side=tk.LEFT, padx=8)
        tk.Button(correction_controls, text="Stop Audio", command=self._stop_audio).pack(side=tk.LEFT)
        tk.Button(correction_controls, text="Approve Correction", command=self._approve_selected_correction).pack(side=tk.RIGHT)

        tk.Label(correction, textvariable=self.selected_record_label_var, anchor="w").pack(fill=tk.X, padx=8)

        correction_body = tk.Frame(correction)
        correction_body.pack(fill=tk.BOTH, expand=True, padx=8, pady=6)

        list_col = tk.Frame(correction_body)
        list_col.pack(side=tk.LEFT, fill=tk.BOTH, expand=False)
        self.record_listbox = tk.Listbox(list_col, width=58, height=8, exportselection=False)
        self.record_listbox.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        list_scroll = tk.Scrollbar(list_col, orient=tk.VERTICAL, command=self.record_listbox.yview)
        list_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.record_listbox.config(yscrollcommand=list_scroll.set)
        self.record_listbox.bind("<<ListboxSelect>>", self._on_correction_record_select)

        editor_col = tk.Frame(correction_body)
        editor_col.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(10, 0))

        tk.Label(editor_col, text="Original transcript (read-only):", anchor="w").pack(fill=tk.X)
        self.original_transcript_box = tk.Text(editor_col, height=4, wrap=tk.WORD)
        self.original_transcript_box.pack(fill=tk.BOTH, expand=True)

        tk.Label(editor_col, text="Corrected transcript (editable):", anchor="w").pack(fill=tk.X, pady=(6, 0))
        self.corrected_transcript_box = tk.Text(editor_col, height=4, wrap=tk.WORD)
        self.corrected_transcript_box.pack(fill=tk.BOTH, expand=True)

        self.log = ScrolledText(self.root, wrap=tk.WORD, height=18)
        self.log.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        self.auto_detect_root(log_when_found=False)
        self._refresh_correction_records()

    def _set_text_widget(self, widget: tk.Text, text: str, read_only: bool) -> None:
        widget.config(state=tk.NORMAL)
        widget.delete("1.0", tk.END)
        widget.insert(tk.END, text or "")
        if read_only:
            widget.config(state=tk.DISABLED)

    def _collect_correction_records(self) -> list[dict[str, object]]:
        root_path = self._resolve_and_cache_logs_root(self.root_path_var.get().strip())
        if not root_path:
            return []

        sessions = self._list_session_folders(root_path)
        out: list[dict[str, object]] = []
        for session_path in sessions:
            session_name = os.path.basename(session_path)
            for rec in self._load_transcript_records(session_path):
                sort_ts = str(rec.get("position_ts_utc", "") or rec.get("ts_utc", "") or "")
                out.append({
                    "session_path": session_path,
                    "session_name": session_name,
                    "record": rec,
                    "sort_ts": sort_ts,
                })

        out.sort(key=lambda r: str(r.get("sort_ts", "")), reverse=True)
        return out

    def _refresh_correction_records(self) -> None:
        self.correction_records = self._collect_correction_records()

        self.record_listbox.delete(0, tk.END)
        for item in self.correction_records:
            rec = item.get("record", {})
            status = str(rec.get("transcript_status", "pending"))
            correction_status = str(rec.get("correction_status", "none"))
            wav_file = str(rec.get("wav_file", ""))
            session_name = str(item.get("session_name", ""))
            label = f"{session_name} | {wav_file} | transcript={status} | correction={correction_status}"
            self.record_listbox.insert(tk.END, label)

        if not self.correction_records:
            self.selected_correction_index = None
            self.selected_record_label_var.set("No transcript records found.")
            self._set_text_widget(self.original_transcript_box, "", read_only=True)
            self._set_text_widget(self.corrected_transcript_box, "", read_only=False)
            return

        self.record_listbox.selection_clear(0, tk.END)
        self.record_listbox.selection_set(0)
        self.record_listbox.activate(0)
        self._on_correction_record_select()

    def _on_correction_record_select(self, _event: object | None = None) -> None:
        selection = self.record_listbox.curselection()
        if not selection:
            return

        idx = int(selection[0])
        if idx < 0 or idx >= len(self.correction_records):
            return

        self.selected_correction_index = idx
        item = self.correction_records[idx]
        rec = dict(item.get("record", {}))
        session_name = str(item.get("session_name", ""))
        wav_file = str(rec.get("wav_file", ""))
        status = str(rec.get("transcript_status", "pending"))
        correction_status = str(rec.get("correction_status", "none"))
        self.selected_record_label_var.set(
            f"Selected: {wav_file} | session={session_name} | transcript={status} | correction={correction_status}"
        )

        original = str(rec.get("transcript_text", "") or "")
        corrected = rec.get("corrected_transcript_text")
        corrected_text = "" if corrected is None else str(corrected)
        self._set_text_widget(self.original_transcript_box, original, read_only=True)
        self._set_text_widget(self.corrected_transcript_box, corrected_text, read_only=False)

    def _selected_item(self) -> dict[str, object] | None:
        if self.selected_correction_index is None:
            return None
        if self.selected_correction_index < 0 or self.selected_correction_index >= len(self.correction_records):
            return None
        return self.correction_records[self.selected_correction_index]

    def _selected_audio_path(self) -> str:
        item = self._selected_item()
        if not isinstance(item, dict):
            return ""

        rec = dict(item.get("record", {}))
        session_path = str(item.get("session_path", ""))
        audio_path = str(rec.get("audio_file_path", "") or "").strip()
        if audio_path:
            return audio_path

        wav_file = str(rec.get("wav_file", "") or "").strip()
        if not wav_file:
            return ""
        return os.path.join(session_path, "audio-notes", wav_file)

    def _play_selected_audio(self) -> None:
        audio_path = self._selected_audio_path()
        if not audio_path:
            self._log("[error] Select a transcript record first.")
            return
        if not os.path.isfile(audio_path):
            self._log(f"[error] Audio file not found: {audio_path}")
            return

        try:
            if winsound is not None:
                winsound.PlaySound(audio_path, winsound.SND_FILENAME | winsound.SND_ASYNC)
            elif hasattr(os, "startfile"):
                os.startfile(audio_path)
            else:
                self._log("[warn] Audio playback is not supported in this environment.")
                return
            self._log(f"[ok] Playing audio: {os.path.basename(audio_path)}")
        except Exception as e:
            self._log(f"[error] Failed to play audio: {e}")

    def _stop_audio(self) -> None:
        try:
            if winsound is not None:
                winsound.PlaySound(None, winsound.SND_PURGE)
                self._log("[ok] Audio playback stopped.")
            else:
                self._log("[info] Stop playback is only available on Windows winsound backend.")
        except Exception as e:
            self._log(f"[error] Failed to stop audio: {e}")

    def _approve_selected_correction(self) -> None:
        item = self._selected_item()
        if not isinstance(item, dict):
            self._log("[error] Select a transcript record before approving correction.")
            return

        rec = dict(item.get("record", {}))
        session_path = str(item.get("session_path", ""))
        if not session_path:
            self._log("[error] Missing session context for selected record.")
            return

        corrected = self.corrected_transcript_box.get("1.0", tk.END).strip()
        if corrected:
            rec["corrected_transcript_text"] = corrected
            rec["correction_status"] = "corrected"
            rec["corrected_utc"] = utc_now_iso()
        else:
            rec["corrected_transcript_text"] = None
            rec["correction_status"] = "none"
            rec["corrected_utc"] = None

        self._upsert_transcript_record(session_path, rec)

        # Refresh derived outputs so corrected fields flow into enriched datasets.
        context = self._build_session_context(session_path)
        self._rebuild_enriched_datasets(session_path, context)
        self._write_enhanced_session_summary(session_path, self._load_events_for_summary(session_path))
        self._write_outputs_guide(session_path)

        self._log(f"[ok] Correction approved for: {rec.get('wav_file', '')}")
        self._refresh_correction_records()

    def _log(self, msg: str) -> None:
        self.log.insert(tk.END, msg + "\n")
        self.log.see(tk.END)

    def _safe_osc_port(self) -> int:
        try:
            return int(self.osc_port_var.get().strip() or "3333")
        except Exception:
            return 3333

    def test_osc(self) -> None:
        payload = {
            "ts_utc": utc_now_iso(),
            "status": "ok",
            "engine": "vosk",
            "audio_note_id": -1,
            "wav_file": "OSC_TEST",
            "transcript_text": "OSC_TEST_TRANSCRIPT"
        }
        try:
            send_osc(
                self.osc_ip_var.get().strip() or "127.0.0.1",
                self._safe_osc_port(),
                self.osc_address_var.get().strip() or OSC_ADDRESS_TRANSCRIPT,
                [json.dumps(payload, ensure_ascii=False)])
            self._log("[ok] Sent OSC test transcript payload.")
        except Exception as e:
            self._log(f"[error] OSC test failed: {e}")

    def browse_root(self) -> None:
        path = filedialog.askdirectory(title="Select AaltoLaunchLogs root")
        if path:
            self.root_path_var.set(path)
            self._resolved_root_path = ""

    def browse_model(self) -> None:
        path = filedialog.askdirectory(title="Select Vosk model folder")
        if path:
            self.model_path_var.set(path)

    def _load_model(self) -> bool:
        model_path = self.model_path_var.get().strip()
        if not model_path or not os.path.isdir(model_path):
            self._log(f"[error] Invalid model path: {model_path}")
            return False

        try:
            self.model = vosk.Model(model_path)
            self._log("[ok] Vosk model loaded.")
            return True
        except Exception as e:
            self._log(f"[error] Failed to load model: {e}")
            self.model = None
            return False

    def _poll_seconds(self) -> float:
        try:
            v = float(self.poll_seconds_var.get().strip())
            if v < 0.2:
                return 0.2
            return v
        except Exception:
            return 2.0

    def _is_session_folder(self, path: str) -> bool:
        if not os.path.isdir(path):
            return False
        name = os.path.basename(path.rstrip("/\\"))
        if not is_session_folder_name(name):
            return False
        return os.path.isdir(os.path.join(path, "audio-notes"))

    def _looks_like_launch_logs_root(self, path: str) -> bool:
        if not os.path.isdir(path):
            return False
        for name in os.listdir(path):
            if is_session_folder_name(name) and os.path.isdir(os.path.join(path, name)):
                return True
        return False

    def _resolve_logs_root(self, raw_path: str) -> str:
        candidate = (raw_path or "").strip()
        if not candidate:
            return ""

        candidate = os.path.abspath(candidate)
        if not os.path.isdir(candidate):
            return ""

        # If the user pointed directly at a session folder, use it as-is.
        if self._is_session_folder(candidate):
            return candidate

        # If the user pointed at the launch root already, use it.
        if self._looks_like_launch_logs_root(candidate):
            return candidate

        # If the launch root exists one level below, use it.
        direct_child = os.path.join(candidate, LAUNCH_LOGS_FOLDER_NAME)
        if self._looks_like_launch_logs_root(direct_child):
            return direct_child

        # Fast path for common Unity LocalLow layout:
        # <LocalLow>/<Company>/<Product>/AaltoLaunchLogs
        try:
            for company in os.listdir(candidate):
                company_path = os.path.join(candidate, company)
                if not os.path.isdir(company_path):
                    continue
                for product in os.listdir(company_path):
                    product_path = os.path.join(company_path, product)
                    if not os.path.isdir(product_path):
                        continue
                    alt = os.path.join(product_path, LAUNCH_LOGS_FOLDER_NAME)
                    if self._looks_like_launch_logs_root(alt):
                        return alt
        except Exception:
            pass

        # Bounded recursive search (prevents long hangs scanning large user directories).
        best_match = ""
        best_mtime = -1.0

        max_depth = 4
        queue: list[tuple[str, int]] = [(candidate, 0)]
        visited: set[str] = set()
        while queue:
            root, depth = queue.pop(0)
            if root in visited:
                continue
            visited.add(root)

            base = os.path.basename(root.rstrip("/\\"))
            if base != LAUNCH_LOGS_FOLDER_NAME:
                if depth >= max_depth:
                    continue
                try:
                    with os.scandir(root) as entries:
                        for entry in entries:
                            if entry.is_dir(follow_symlinks=False):
                                queue.append((entry.path, depth + 1))
                except Exception:
                    continue
                continue

            if not self._looks_like_launch_logs_root(root):
                continue
            try:
                mtime = os.path.getmtime(root)
            except Exception:
                mtime = 0.0
            if mtime >= best_mtime:
                best_mtime = mtime
                best_match = root

        return best_match

    def _resolve_and_cache_logs_root(self, raw_path: str) -> str:
        resolved = self._resolve_logs_root(raw_path)
        self._resolved_root_path = resolved
        return resolved

    def auto_detect_root(self, log_when_found: bool = True) -> None:
        local_low = os.path.join(os.path.expanduser("~"), "AppData", "LocalLow")
        resolved = self._resolve_and_cache_logs_root(local_low)
        if resolved:
            self.root_path_var.set(resolved)
            if log_when_found:
                self._log(f"[ok] Auto-detected logs root: {resolved}")
        elif log_when_found:
            self._log("[warn] Could not auto-detect AaltoLaunchLogs. Use Browse and pick either AaltoLaunchLogs or a session_* folder.")

    def validate_root(self) -> None:
        resolved = self._resolve_and_cache_logs_root(self.root_path_var.get())
        if not resolved:
            self._log("[error] Root is invalid. Pick AaltoLaunchLogs or a session_* folder.")
            return
        self.root_path_var.set(resolved)
        sessions = self._list_session_folders(resolved)
        self._log(f"[ok] Root valid: {resolved} | sessions found: {len(sessions)}")

    def _list_session_folders(self, root_path: str) -> list[str]:
        if not os.path.isdir(root_path):
            return []

        if self._is_session_folder(root_path):
            return [root_path]

        out: list[str] = []
        for name in os.listdir(root_path):
            p = os.path.join(root_path, name)
            if os.path.isdir(p) and is_session_folder_name(name):
                out.append(p)
        out.sort()
        return out

    def _state_path(self, session_path: str) -> str:
        return session_derived_file(session_path, STATE_FILE_NAME)

    def _legacy_state_path(self, session_path: str) -> str:
        return os.path.join(session_path, STATE_FILE_NAME)

    def _ensure_session_layout(self, session_path: str) -> None:
        derived = session_derived_dir(session_path)
        make_dir(derived)

        # Auto-migrate older auxiliary outputs from session root into _derived.
        aux_files = [
            STATE_FILE_NAME,
            TRANSCRIPTS_CSV,
            TRANSCRIPT_EVENTS_JSONL,
            ENRICHED_EVENTS_CSV,
        ]
        for name in aux_files:
            src = os.path.join(session_path, name)
            dst = session_derived_file(session_path, name)
            if os.path.isfile(src) and not os.path.isfile(dst):
                try:
                    os.replace(src, dst)
                except Exception:
                    # Best-effort migration. If move fails, continue using source.
                    pass

    def _write_outputs_guide(self, session_path: str) -> None:
        self._ensure_session_layout(session_path)

        lines = [
            "# Session Outputs Guide",
            "",
            f"Generated by transcriber at: {utc_now_iso()}",
            "",
            "## Use These Primary Files (session root)",
            "- events.jsonl: Original Unity structured events.",
            "- logs.without-transcription.jsonl: Explicit copy of original logs without transcript events.",
            "- events.csv: Original Unity structured events in CSV.",
            "- session.db: Original Unity SQLite database (when enabled).",
            "- session-manifest.json: Session metadata from Unity.",
            "- session-summary.md: Session summary from Unity.",
            "- audio-note-transcripts.jsonl: Canonical transcript records with lifecycle and correction fields.",
            "- events.enriched.jsonl: Original events + transcript-ready events merged in timeline order.",
            "- logs.with-transcription.jsonl: Explicit log stream including transcript-ready events.",
            "- audio-notes/: WAV audio notes and transcript txt files.",
            "",
            "## Auxiliary / Technical Files (_derived)",
            "- _derived/audio-note-transcriber-state.json: Cache for incremental processing.",
            "- _derived/audio-note-transcripts.csv: CSV mirror of transcript records.",
            "- _derived/events.audio-note-transcripts.jsonl: Synthetic transcript-ready events only.",
            "- _derived/events.enriched.csv: CSV mirror of enriched events.",
            "",
            "## Notes",
            "- Re-running transcriber is safe: transcript outputs are de-duplicated by audio file.",
            "- If a WAV file changes (mtime/size), that WAV is reprocessed.",
            "- Manual correction is prepared via corrected_transcript_text/correction_status/corrected_utc fields in audio-note-transcripts.jsonl.",
        ]

        with open(os.path.join(session_path, OUTPUT_GUIDE_MD), "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")

    def _event_payload(self, event: dict[str, object]) -> dict[str, object]:
        payload = event.get("payload")
        return payload if isinstance(payload, dict) else {}

    def _event_sort_key(self, event: dict[str, object]) -> tuple[str, int]:
        ts = str(event.get("ts_utc", "") or "")
        try:
            eid = int(event.get("event_id", 0) or 0)
        except Exception:
            eid = 0
        return (ts, eid)

    def _load_events_for_summary(self, session_path: str) -> list[dict[str, object]]:
        enriched_path = os.path.join(session_path, ENRICHED_EVENTS_JSONL)
        base_path = os.path.join(session_path, BASE_EVENTS_JSONL)

        events = self._safe_load_jsonl(enriched_path)
        if not events:
            events = self._safe_load_jsonl(base_path)

        out: list[dict[str, object]] = []
        for e in events:
            if not isinstance(e, dict):
                continue
            out.append(e)

        out.sort(key=self._event_sort_key)
        return out

    def _latest_event(self, events: list[dict[str, object]], event_type: str) -> dict[str, object] | None:
        latest: dict[str, object] | None = None
        for e in events:
            if str(e.get("event_type", "")) != event_type:
                continue
            latest = e
        return latest

    def _latest_interview_questions(self, events: list[dict[str, object]]) -> str:
        changed = self._latest_event(events, "interview.followup_questions_changed")
        if changed:
            return str(self._event_payload(changed).get("new_questions", "") or "").strip()

        generated = self._latest_event(events, "interview.followup_questions_generated")
        if generated:
            return str(self._event_payload(generated).get("questions", "") or "").strip()

        return ""

    def _latest_interview_summary(self, events: list[dict[str, object]]) -> str:
        changed = self._latest_event(events, "interview.summary_changed")
        if changed:
            return str(self._event_payload(changed).get("new_summary", "") or "").strip()

        generated = self._latest_event(events, "interview.summary_generated")
        if generated:
            return str(self._event_payload(generated).get("summary", "") or "").strip()

        return ""

    def _build_turn_trace(self, events: list[dict[str, object]]) -> list[dict[str, object]]:
        """Reconstruct performer turns from event stream.
        
        Handles both single-speaker and multi-speaker patterns:
        - Single-speaker: turn_submitted -> decision_resolved -> action_dispatch
        - Multi-speaker: dialogue_received -> decision_result -> action_applied
        """
        structured_turns: list[dict[str, object]] = []
        for e in events:
            if str(e.get("event_type", "") or "") != "performer.turn_record":
                continue
            payload = self._event_payload(e)
            character_state = payload.get("character_state", {})
            execution = payload.get("execution", {})
            structured_turns.append({
                "input_text": str(payload.get("actor_input", "") or payload.get("latest_move", "") or "").strip(),
                "input_source": str(payload.get("actor_source", "") or payload.get("turn_type", "") or "").strip(),
                "interpreted_intent": str(payload.get("interpreted_intent", "") or "").strip(),
                "action": str(payload.get("selected_action", "") or payload.get("selected_action_label", "") or "").strip(),
                "justification": str(payload.get("decision_note", "") or payload.get("reason", "") or "").strip(),
                "character_summary": str(character_state.get("summary", "") or payload.get("character_summary", "") or "").strip() if isinstance(character_state, dict) else str(payload.get("character_summary", "") or "").strip(),
                "objective": str(character_state.get("objective", "") or payload.get("objective", "") or "").strip() if isinstance(character_state, dict) else str(payload.get("objective", "") or "").strip(),
                "stance": str(character_state.get("stance", "") or payload.get("stance", "") or "").strip() if isinstance(character_state, dict) else str(payload.get("stance", "") or "").strip(),
                "available_actions": payload.get("available_actions", []),
                "react_now": payload.get("react_now", None),
                "execution_succeeded": execution.get("succeeded", payload.get("execution_succeeded", None)) if isinstance(execution, dict) else payload.get("execution_succeeded", None),
                "execution_result": str(execution.get("result", "") or payload.get("execution_result", "") or "").strip() if isinstance(execution, dict) else str(payload.get("execution_result", "") or "").strip(),
            })

        if structured_turns:
            return structured_turns

        turns: list[dict[str, object]] = []
        pending_decision_idx: list[int] = []
        pending_action_idx: list[int] = []

        for e in events:
            et = str(e.get("event_type", "") or "")
            payload = self._event_payload(e)

            # Input events: single-speaker or multi-speaker dialogue received
            if et == "performer.turn_submitted":
                turn = {
                    "input_text": str(payload.get("text", "") or "").strip(),
                    "input_source": str(payload.get("source", "") or "").strip(),
                    "action": "",
                    "justification": "",
                }
                turns.append(turn)
                idx = len(turns) - 1
                pending_decision_idx.append(idx)
                pending_action_idx.append(idx)
                continue

            if et == "performer.dialogue_received":
                speaker = str(payload.get("speaker_id", "") or "").strip()
                text = str(payload.get("text", "") or "").strip()
                turn = {
                    "input_text": text,
                    "input_source": speaker,
                    "action": "",
                    "justification": "",
                }
                turns.append(turn)
                idx = len(turns) - 1
                pending_decision_idx.append(idx)
                pending_action_idx.append(idx)
                continue

            # Decision events: single-speaker (decision_resolved) or multi-speaker (decision_result)
            if et == "performer.decision_resolved" and pending_decision_idx:
                idx = pending_decision_idx.pop(0)
                turns[idx]["action"] = str(payload.get("chosen_action", "") or "").strip()
                turns[idx]["justification"] = str(payload.get("justification", "") or "").strip()
                continue

            if et == "performer.decision_result" and pending_decision_idx:
                idx = pending_decision_idx.pop(0)
                # Multi-speaker may use "chosen_action_raw" or "chosen_action"
                action = str(payload.get("chosen_action_raw", "") or payload.get("chosen_action", "") or "").strip()
                turns[idx]["action"] = action
                turns[idx]["justification"] = str(payload.get("justification", "") or "").strip()
                continue

            # Action events: single-speaker (action_dispatch) or multi-speaker (action_applied)
            if et == "performer.action_dispatch" and pending_action_idx:
                idx = pending_action_idx.pop(0)
                dispatched = str(payload.get("chosen_action", "") or "").strip()
                if dispatched:
                    turns[idx]["action"] = dispatched
                continue

            if et == "performer.action_applied" and pending_action_idx:
                idx = pending_action_idx.pop(0)
                dispatched = str(payload.get("chosen_action", "") or "").strip()
                if dispatched:
                    turns[idx]["action"] = dispatched

        return turns

    def _write_enhanced_session_summary(self, session_path: str, events: list[dict[str, object]]) -> None:
        manifest_path = os.path.join(session_path, "session-manifest.json")
        manifest: dict[str, object] = {}
        if os.path.isfile(manifest_path):
            try:
                with open(manifest_path, "r", encoding="utf-8") as f:
                    raw = json.load(f)
                if isinstance(raw, dict):
                    manifest = raw
            except Exception:
                manifest = {}

        session_id = str(manifest.get("session_id", "") or "")
        run_id = str(manifest.get("run_id", "") or "")
        run_tag = str(manifest.get("run_tag", "") or "")
        if not run_id:
            started = self._latest_event(events, "session_started")
            payload = self._event_payload(started or {})
            run_id = str(payload.get("run_id", "") or "")
            run_tag = str(payload.get("run_tag", "") or "")

        start_local = str(manifest.get("start_local", "") or "")
        end_local = str(manifest.get("end_local", "") or "")
        start_utc = str(manifest.get("start_utc", "") or "")
        end_utc = str(manifest.get("end_utc", "") or "")
        structured_count = int(manifest.get("structured_event_count", len(events)) or len(events))
        raw_count = int(manifest.get("raw_log_count", 0) or 0)

        files_present = sorted(os.listdir(session_path)) if os.path.isdir(session_path) else []

        bootstrap = self._latest_event(events, "bootstrap.generated")
        bootstrap_payload = self._event_payload(bootstrap or {})
        objective = str(bootstrap_payload.get("objective", "") or "").strip()
        stance = str(bootstrap_payload.get("stance", "") or "").strip()

        final_questions = self._latest_interview_questions(events)
        final_summary = self._latest_interview_summary(events)

        turns = self._build_turn_trace(events)

        dispatched_actions: list[str] = []
        action_counts: dict[str, int] = {}
        for e in events:
            if str(e.get("event_type", "")) != "performer.action_dispatch":
                continue
            action = str(self._event_payload(e).get("chosen_action", "") or "").strip()
            if not action:
                continue
            dispatched_actions.append(action)
            action_counts[action] = int(action_counts.get(action, 0)) + 1

        notes: list[str] = []
        for e in events:
            if str(e.get("event_type", "")) != "annotation.note":
                continue
            p = self._event_payload(e)
            label = str(p.get("label", "") or "").strip()
            body = str(p.get("body", "") or "").strip()
            if body:
                notes.append(f"{label}: {body}" if label else body)

        transcript_records = self._load_transcript_records(session_path)
        audio_note_lines: list[str] = []
        for rec in transcript_records:
            wav_file = str(rec.get("wav_file", "") or "").strip()
            corrected = str(rec.get("corrected_transcript_text", "") or "").strip()
            raw_text = str(rec.get("transcript_text", "") or "").strip()
            text = corrected if corrected else raw_text
            if not text:
                continue
            status = str(rec.get("correction_status", "none") or "none").strip().lower()
            suffix = " (corrected)" if status == "corrected" and corrected else ""
            label = wav_file if wav_file else f"audio_note_{int(rec.get('audio_note_id', -1) or -1)}"
            audio_note_lines.append(f"{label}{suffix}: {text}")

        external_input_count = 0
        action_dispatch_count = 0
        has_text_notes = False
        has_audio_notes = False
        for e in events:
            et = str(e.get("event_type", "") or "")
            if et == "performer.external_speech_received":
                external_input_count += 1
            elif et == "performer.action_dispatch":
                action_dispatch_count += 1
            elif et == "annotation.note":
                has_text_notes = True
            elif et == "audio_note_saved":
                has_audio_notes = True

        lines: list[str] = []
        lines.append("# Aalto Launch Session Summary")
        lines.append("")
        lines.append("## Session")
        if session_id:
            lines.append(f"- Session ID: {session_id}")
        if run_id or run_tag:
            lines.append(f"- Run: {(run_tag or run_id).strip()}")
        if start_local:
            lines.append(f"- Start (local): {start_local.replace('T', ' ')}")
        if end_local:
            lines.append(f"- End (local): {end_local.replace('T', ' ')}")
        if start_utc:
            lines.append(f"- Start (UTC): {start_utc}")
        if end_utc:
            lines.append(f"- End (UTC): {end_utc}")
        lines.append(f"- Structured events: {structured_count}")
        lines.append(f"- Raw Unity logs: {raw_count}")
        lines.append("")

        lines.append("## Files")
        preferred_files = [
            BASE_EVENTS_JSONL,
            "events.csv",
            "unity-raw.jsonl",
            "unity-raw.csv",
            "audio-notes/",
            TRANSCRIPTS_JSONL,
            ENRICHED_EVENTS_JSONL,
            LOGS_WITHOUT_TRANSCRIPTS_JSONL,
            LOGS_WITH_TRANSCRIPTS_JSONL,
            "session.db",
            "session-manifest.json",
            SESSION_SUMMARY_MD,
        ]
        for item in preferred_files:
            if item.endswith("/"):
                present = os.path.isdir(os.path.join(session_path, item.rstrip("/")))
            else:
                present = item in files_present
            if present:
                lines.append(f"- {item}")
        lines.append("")

        lines.append("## Character Setup")
        if objective or stance:
            if objective:
                lines.append(f"- Objective: {objective}")
            if stance:
                lines.append(f"- Stance: {stance}")
            short_result = "; ".join(x for x in [objective, stance] if x)
            lines.append(f"- Bootstrap result: {short_result}")
        else:
            lines.append("- None")
        lines.append("")

        lines.append("## Final Interview Outputs")
        if final_questions:
            lines.append("- Final follow-up questions:")
            for q in [x.strip() for x in final_questions.splitlines() if x.strip()]:
                lines.append(f"  - {q}")
        else:
            lines.append("- Final follow-up questions: None")

        if final_summary:
            lines.append(f"- Final character summary: {final_summary}")
        else:
            lines.append("- Final character summary: None")
        lines.append("")

        lines.append("## Performer Interaction Trace")
        if turns:
            for i, turn in enumerate(turns, start=1):
                input_text = str(turn.get("input_text", "") or "").strip() or "(no input)"
                input_source = str(turn.get("input_source", "") or "").strip()
                interpreted_intent = str(turn.get("interpreted_intent", "") or "").strip()
                action = str(turn.get("action", "") or "").strip() or "(no action)"
                rationale = str(turn.get("justification", "") or "").strip()
                objective = str(turn.get("objective", "") or "").strip()
                stance = str(turn.get("stance", "") or "").strip()
                state_bits = []
                if objective:
                    state_bits.append(f"objective={objective}")
                if stance:
                    state_bits.append(f"stance={stance}")
                line = f"- Turn {i} - Input: \"{input_text}\""
                if input_source:
                    line += f" | Source: {input_source}"
                if interpreted_intent:
                    line += f" | Interpreted: {interpreted_intent}"
                line += f" -> Action: `{action}`"
                if rationale:
                    line += f" | Why: {rationale}"
                if state_bits:
                    line += f" | State: {', '.join(state_bits)}"
                lines.append(line)
        else:
            lines.append("- None")
        lines.append("")

        lines.append("## Actions Summary")
        if dispatched_actions:
            lines.append("- Ordered actions:")
            for i, action in enumerate(dispatched_actions, start=1):
                lines.append(f"  - {i}. {action}")
            lines.append("- Counts:")
            for action, count in sorted(action_counts.items(), key=lambda x: (-x[1], x[0])):
                lines.append(f"  - {action} x {count}")
        else:
            lines.append("- None")
        lines.append("")

        lines.append("## Notes Taken")
        if notes:
            lines.append("- Text notes:")
            for note in notes:
                lines.append(f"  - {note}")
        else:
            lines.append("- Text notes: None")

        if audio_note_lines:
            lines.append("- Audio notes (preferred corrected transcripts):")
            for note in audio_note_lines:
                lines.append(f"  - {note}")
        else:
            lines.append("- Audio notes: None")
        lines.append("")

        lines.append("## Session Interpretation")
        lines.append(f"- Performer turns: {len(turns)}")
        lines.append(f"- External speech inputs: {external_input_count}")
        lines.append(f"- Action dispatches: {action_dispatch_count}")
        lines.append(f"- Text notes taken: {'yes' if has_text_notes else 'no'}")
        lines.append(f"- Audio notes taken: {'yes' if has_audio_notes else 'no'}")
        lines.append("")

        summary_path = os.path.join(session_path, SESSION_SUMMARY_MD)
        try:
            with open(summary_path, "w", encoding="utf-8") as f:
                f.write("\n".join(lines).strip() + "\n")
        except Exception as e:
            self._log(f"[warn] Could not write enhanced session summary: {e}")

    def _load_session_state(self, session_path: str) -> dict[str, dict[str, object]]:
        if session_path in self.processed_by_session:
            return self.processed_by_session[session_path]

        self._ensure_session_layout(session_path)

        state_file = self._state_path(session_path)
        data: dict[str, dict[str, object]] = {}
        if os.path.isfile(state_file):
            try:
                with open(state_file, "r", encoding="utf-8") as f:
                    raw = json.load(f)
                if isinstance(raw, dict):
                    data = raw
            except Exception:
                data = {}
        else:
            legacy = self._legacy_state_path(session_path)
            if os.path.isfile(legacy):
                try:
                    with open(legacy, "r", encoding="utf-8") as f:
                        raw = json.load(f)
                    if isinstance(raw, dict):
                        data = raw
                except Exception:
                    data = {}

        self.processed_by_session[session_path] = data
        return data

    def _save_session_state(self, session_path: str) -> None:
        self._ensure_session_layout(session_path)
        state = self.processed_by_session.get(session_path, {})
        state_file = self._state_path(session_path)
        with open(state_file, "w", encoding="utf-8") as f:
            json.dump(state, f, ensure_ascii=False, indent=2)

    def _append_jsonl(self, path: str, record: dict[str, object]) -> None:
        with open(path, "a", encoding="utf-8", newline="\n") as f:
            f.write(json.dumps(record, ensure_ascii=False) + "\n")

    def _append_csv(self, path: str, record: dict[str, object]) -> None:
        row = {
            "ts_utc": record.get("ts_utc", ""),
            "session_folder": record.get("session_folder", ""),
            "audio_note_id": record.get("audio_note_id", -1),
            "note_label": record.get("note_label", ""),
            "note_body": record.get("note_body", ""),
            "wav_file": record.get("wav_file", ""),
            "wav_path": record.get("wav_path", ""),
            "wav_duration_sec": record.get("wav_duration_sec", 0.0),
            "wav_sample_rate": record.get("wav_sample_rate", 0),
            "wav_channels": record.get("wav_channels", 0),
            "started_utc": record.get("started_utc", ""),
            "started_local": record.get("started_local", ""),
            "ended_utc": record.get("ended_utc", ""),
            "position_ts_utc": record.get("position_ts_utc", ""),
            "position_ts_local": record.get("position_ts_local", ""),
            "transcript_text": record.get("transcript_text", ""),
            "engine": record.get("engine", ""),
            "language_hint": record.get("language_hint", ""),
            "status": record.get("status", ""),
            "error": record.get("error", ""),
        }

        file_exists = os.path.isfile(path)
        with open(path, "a", encoding="utf-8", newline="") as f:
            writer = csv.DictWriter(f, fieldnames=list(row.keys()))
            if not file_exists or os.path.getsize(path) == 0:
                writer.writeheader()
            writer.writerow(row)

    def _write_transcript_jsonl(self, session_path: str, records: list[dict[str, object]]) -> None:
        path = os.path.join(session_path, TRANSCRIPTS_JSONL)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            for rec in records:
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")

    def _write_transcript_csv(self, session_path: str, records: list[dict[str, object]]) -> None:
        self._ensure_session_layout(session_path)
        path = session_derived_file(session_path, TRANSCRIPTS_CSV)
        with open(path, "w", encoding="utf-8", newline="") as f:
            fieldnames = [
                "ts_utc", "session_id", "run_number", "run_id",
                "audio_note_id", "audio_file_path", "transcript_file_path",
                "transcript_text", "transcript_status", "transcript_engine", "transcribed_utc", "transcript_confidence",
                "corrected_transcript_text", "correction_status", "corrected_utc",
                "note_label", "note_body", "wav_file", "wav_duration_sec", "wav_sample_rate", "wav_channels",
                "started_utc", "started_local", "ended_utc", "position_ts_utc", "position_ts_local",
                "language_hint", "error"
            ]
            writer = csv.DictWriter(f, fieldnames=fieldnames)
            writer.writeheader()
            for rec in records:
                writer.writerow({
                    "ts_utc": rec.get("ts_utc", ""),
                    "session_id": rec.get("session_id", ""),
                    "run_number": rec.get("run_number", 0),
                    "run_id": rec.get("run_id", ""),
                    "audio_note_id": rec.get("audio_note_id", -1),
                    "audio_file_path": rec.get("audio_file_path", ""),
                    "transcript_file_path": rec.get("transcript_file_path", ""),
                    "transcript_text": rec.get("transcript_text", ""),
                    "transcript_status": rec.get("transcript_status", "pending"),
                    "transcript_engine": rec.get("transcript_engine", "vosk"),
                    "transcribed_utc": rec.get("transcribed_utc", ""),
                    "transcript_confidence": rec.get("transcript_confidence", ""),
                    "corrected_transcript_text": rec.get("corrected_transcript_text", ""),
                    "correction_status": rec.get("correction_status", "none"),
                    "corrected_utc": rec.get("corrected_utc", ""),
                    "note_label": rec.get("note_label", ""),
                    "note_body": rec.get("note_body", ""),
                    "wav_file": rec.get("wav_file", ""),
                    "wav_duration_sec": rec.get("wav_duration_sec", 0.0),
                    "wav_sample_rate": rec.get("wav_sample_rate", 0),
                    "wav_channels": rec.get("wav_channels", 0),
                    "started_utc": rec.get("started_utc", ""),
                    "started_local": rec.get("started_local", ""),
                    "ended_utc": rec.get("ended_utc", ""),
                    "position_ts_utc": rec.get("position_ts_utc", ""),
                    "position_ts_local": rec.get("position_ts_local", ""),
                    "language_hint": rec.get("language_hint", ""),
                    "error": rec.get("error", ""),
                })

    def _record_identity_key(self, rec: dict[str, object]) -> str:
        audio_path = str(rec.get("audio_file_path", "") or "").strip().lower()
        if audio_path:
            return audio_path
        wav_file = str(rec.get("wav_file", "") or "").strip().lower()
        return wav_file

    def _normalize_transcript_record(self, rec: dict[str, object], context: dict[str, object] | None = None) -> dict[str, object]:
        context = context or {}
        out = dict(rec)

        run_number = int(out.get("run_number", context.get("run_number", 0)) or 0)
        run_id = str(out.get("run_id", context.get("run_id", "")) or "")
        if not run_id:
            run_id = normalize_run_id(run_number, "")

        out["session_id"] = str(out.get("session_id", context.get("session_id", "")) or "")
        out["run_number"] = run_number
        out["run_id"] = run_id
        out["audio_file_path"] = str(out.get("audio_file_path", out.get("wav_path", "")) or "")
        out["transcript_file_path"] = str(out.get("transcript_file_path", out.get("transcript_txt", "")) or "")

        status = str(out.get("transcript_status", out.get("status", "pending")) or "pending").strip().lower()
        if status not in ("pending", "ok", "failed"):
            status = "pending"
        out["transcript_status"] = status

        out["transcript_engine"] = str(out.get("transcript_engine", out.get("engine", "vosk")) or "vosk")
        out["transcribed_utc"] = str(out.get("transcribed_utc", "") or "")
        out["transcript_text"] = str(out.get("transcript_text", "") or "")

        out["transcript_confidence"] = out.get("transcript_confidence", None)
        out["corrected_transcript_text"] = out.get("corrected_transcript_text", None)
        correction_status = str(out.get("correction_status", "none") or "none").strip().lower()
        if correction_status not in ("none", "corrected"):
            correction_status = "none"
        out["correction_status"] = correction_status
        out["corrected_utc"] = out.get("corrected_utc", None)

        return out

    def _upsert_transcript_record(self, session_path: str, record: dict[str, object]) -> None:
        existing = self._safe_load_jsonl(os.path.join(session_path, TRANSCRIPTS_JSONL))
        by_wav: dict[str, dict[str, object]] = {}

        for rec in existing:
            rec_norm = self._normalize_transcript_record(rec)
            key = self._record_identity_key(rec_norm)
            if not key:
                continue
            by_wav[key] = rec_norm

        rec_norm = self._normalize_transcript_record(record)
        rec_key = self._record_identity_key(rec_norm)
        if rec_key:
            by_wav[rec_key] = rec_norm

        merged = list(by_wav.values())
        merged.sort(key=lambda r: (str(r.get("position_ts_utc", "") or r.get("ts_utc", "")), str(r.get("wav_file", ""))))

        self._write_transcript_jsonl(session_path, merged)
        self._write_transcript_csv(session_path, merged)

    def _write_sidecar_txt(self, txt_path: str, transcript_text: str) -> None:
        with open(txt_path, "w", encoding="utf-8") as f:
            f.write((transcript_text or "").strip() + "\n")

    def _read_wav_metadata(self, wav_path: str) -> dict[str, object]:
        with wave.open(wav_path, "rb") as wf:
            frames = int(wf.getnframes())
            sample_rate = int(wf.getframerate())
            channels = int(wf.getnchannels())

        duration_sec = 0.0
        if sample_rate > 0:
            duration_sec = float(frames) / float(sample_rate)

        return {
            "wav_frames": frames,
            "wav_sample_rate": sample_rate,
            "wav_channels": channels,
            "wav_duration_sec": round(duration_sec, 6),
        }

    def _safe_load_jsonl(self, path: str) -> list[dict[str, object]]:
        out: list[dict[str, object]] = []
        if not os.path.isfile(path):
            return out

        with open(path, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except Exception:
                    continue
                if isinstance(obj, dict):
                    out.append(obj)
        return out

    def _locked_fallback_path(self, path: str) -> str:
        stamp = utc_now_iso().replace(":", "-")
        base, ext = os.path.splitext(path)
        return f"{base}.fallback-{stamp}{ext}"

    def _write_jsonl_rows(self, path: str, rows: list[dict[str, object]], label: str) -> str:
        try:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                for row in rows:
                    f.write(json.dumps(row, ensure_ascii=False) + "\n")
            return path
        except PermissionError:
            alt = self._locked_fallback_path(path)
            try:
                with open(alt, "w", encoding="utf-8", newline="\n") as f:
                    for row in rows:
                        f.write(json.dumps(row, ensure_ascii=False) + "\n")
                self._log(f"[warn] {label} is locked; wrote fallback: {alt}")
                return alt
            except Exception as e:
                self._log(f"[error] Failed writing {label} fallback: {e}")
                return ""
        except Exception as e:
            self._log(f"[error] Failed writing {label}: {e}")
            return ""

    def _write_enriched_csv_rows(self, path: str, enriched: list[dict[str, object]], label: str) -> str:
        def _write_to(target_path: str) -> None:
            with open(target_path, "w", encoding="utf-8", newline="") as f:
                writer = csv.DictWriter(
                    f,
                    fieldnames=["event_id", "session_id", "ts_utc", "ts_local", "source", "event_type", "payload_json"],
                )
                writer.writeheader()
                for e in enriched:
                    payload = e.get("payload")
                    writer.writerow({
                        "event_id": e.get("event_id", 0),
                        "session_id": e.get("session_id", ""),
                        "ts_utc": e.get("ts_utc", ""),
                        "ts_local": e.get("ts_local", ""),
                        "source": e.get("source", ""),
                        "event_type": e.get("event_type", ""),
                        "payload_json": json.dumps(payload if isinstance(payload, dict) else {}, ensure_ascii=False),
                    })

        try:
            _write_to(path)
            return path
        except PermissionError:
            alt = self._locked_fallback_path(path)
            try:
                _write_to(alt)
                self._log(f"[warn] {label} is locked; wrote fallback: {alt}")
                return alt
            except Exception as e:
                self._log(f"[error] Failed writing {label} fallback: {e}")
                return ""
        except Exception as e:
            self._log(f"[error] Failed writing {label}: {e}")
            return ""

    def _build_session_context(self, session_path: str) -> dict[str, object]:
        events_path = os.path.join(session_path, BASE_EVENTS_JSONL)
        events = self._safe_load_jsonl(events_path)

        session_id = ""
        run_number = 0
        run_id = "RUN_000"
        max_event_id = 0
        audio_by_file: dict[str, dict[str, object]] = {}

        for e in events:
            if not session_id:
                session_id = str(e.get("session_id", "") or "")

            try:
                max_event_id = max(max_event_id, int(e.get("event_id", 0) or 0))
            except Exception:
                pass

            if str(e.get("event_type", "")) == "session_started":
                payload = e.get("payload")
                if isinstance(payload, dict):
                    run_number = int(payload.get("run_number", run_number) or run_number or 0)
                    run_tag = str(payload.get("run_tag", "") or "")
                    run_id = normalize_run_id(run_number, run_tag)

            if str(e.get("event_type", "")) != "audio_note_saved":
                continue

            payload = e.get("payload")
            if not isinstance(payload, dict):
                continue

            wav_file = str(payload.get("file_name", "") or "").strip()
            if not wav_file:
                continue

            audio_by_file[wav_file] = {
                "audio_note_id": payload.get("audio_note_id", -1),
                "note_label": payload.get("label", ""),
                "note_body": payload.get("body", ""),
                "audio_file_path": payload.get("file_path", ""),
                "started_utc": payload.get("started_utc", ""),
                "started_local": payload.get("started_local", ""),
                "ended_utc": payload.get("ended_utc", ""),
                "position_ts_utc": payload.get("started_utc", "") or e.get("ts_utc", ""),
                "position_ts_local": payload.get("started_local", "") or e.get("ts_local", ""),
            }

        return {
            "events": events,
            "session_id": session_id,
            "run_number": run_number,
            "run_id": run_id,
            "max_event_id": max_event_id,
            "audio_by_file": audio_by_file,
        }

    def _ensure_pending_transcript_records(self, session_path: str, context: dict[str, object]) -> None:
        session_id = str(context.get("session_id", "") or "")
        run_number = int(context.get("run_number", 0) or 0)
        run_id = str(context.get("run_id", "") or normalize_run_id(run_number, ""))

        for wav_file, meta in context.get("audio_by_file", {}).items():
            if not wav_file:
                continue

            expected_audio_path = os.path.join(session_path, "audio-notes", wav_file)
            audio_path = str(meta.get("audio_file_path", "") or "")
            if not audio_path:
                audio_path = expected_audio_path

            txt_name = canonical_transcript_txt_name(wav_file)
            transcript_path = os.path.join(session_path, "audio-notes", "transcripts", txt_name)

            pending = {
                "ts_utc": utc_now_iso(),
                "session_id": session_id,
                "run_number": run_number,
                "run_id": run_id,
                "audio_note_id": int(meta.get("audio_note_id", -1) or -1),
                "note_label": meta.get("note_label", ""),
                "note_body": meta.get("note_body", ""),
                "wav_file": wav_file,
                "audio_file_path": audio_path,
                "transcript_file_path": transcript_path,
                "started_utc": meta.get("started_utc", ""),
                "started_local": meta.get("started_local", ""),
                "ended_utc": meta.get("ended_utc", ""),
                "position_ts_utc": meta.get("position_ts_utc", ""),
                "position_ts_local": meta.get("position_ts_local", ""),
                "transcript_text": "",
                "transcript_status": "pending",
                "transcript_engine": "vosk",
                "transcribed_utc": "",
                "transcript_confidence": None,
                "corrected_transcript_text": None,
                "correction_status": "none",
                "corrected_utc": None,
                "language_hint": "en-us",
                "error": "",
            }

            self._upsert_transcript_record(session_path, pending)

    def _load_transcript_records(self, session_path: str) -> list[dict[str, object]]:
        raw = self._safe_load_jsonl(os.path.join(session_path, TRANSCRIPTS_JSONL))
        by_wav: dict[str, dict[str, object]] = {}
        for rec in raw:
            rec_norm = self._normalize_transcript_record(rec)
            key = self._record_identity_key(rec_norm)
            if not key:
                continue
            by_wav[key] = rec_norm

        out = list(by_wav.values())
        out.sort(key=lambda r: (str(r.get("position_ts_utc", "") or r.get("ts_utc", "")), str(r.get("wav_file", ""))))
        return out

    def _rebuild_enriched_datasets(self, session_path: str, context: dict[str, object]) -> None:
        base_events = list(context.get("events", []))
        session_id = str(context.get("session_id", "") or "")
        max_event_id = int(context.get("max_event_id", 0) or 0)

        transcript_records = self._load_transcript_records(session_path)
        transcript_events: list[dict[str, object]] = []

        for i, rec in enumerate(transcript_records, start=1):
            ts_utc = str(rec.get("position_ts_utc", "") or rec.get("started_utc", "") or rec.get("ts_utc", "") or "")
            ts_local = str(rec.get("position_ts_local", "") or rec.get("started_local", "") or "")
            if not ts_utc:
                ts_utc = str(rec.get("ts_utc", "") or "")

            payload = {
                "run_number": rec.get("run_number", 0),
                "run_id": rec.get("run_id", ""),
                "session_id": session_id,
                "audio_note_id": rec.get("audio_note_id", -1),
                "audio_file_path": rec.get("audio_file_path", ""),
                "transcript_file_path": rec.get("transcript_file_path", ""),
                "transcript_text": rec.get("transcript_text", ""),
                "transcript_status": rec.get("transcript_status", "pending"),
                "transcript_engine": rec.get("transcript_engine", "vosk"),
                "transcribed_utc": rec.get("transcribed_utc", ""),
                "transcript_confidence": rec.get("transcript_confidence", None),
                "corrected_transcript_text": rec.get("corrected_transcript_text", None),
                "correction_status": rec.get("correction_status", "none"),
                "corrected_utc": rec.get("corrected_utc", None),
                "label": rec.get("note_label", ""),
                "body": rec.get("note_body", ""),
                "wav_file": rec.get("wav_file", ""),
                "wav_duration_sec": rec.get("wav_duration_sec", 0.0),
                "wav_sample_rate": rec.get("wav_sample_rate", 0),
                "wav_channels": rec.get("wav_channels", 0),
                "started_utc": rec.get("started_utc", ""),
                "started_local": rec.get("started_local", ""),
                "ended_utc": rec.get("ended_utc", ""),
                "language_hint": rec.get("language_hint", "en-us"),
                "error": rec.get("error", ""),
            }

            transcript_events.append({
                "event_id": max_event_id + i,
                "session_id": session_id,
                "ts_utc": ts_utc,
                "ts_local": ts_local,
                "source": "audio_note_sidecar",
                "event_type": "audio_note.transcript_ready",
                "payload": payload,
            })

        self._ensure_session_layout(session_path)
        transcript_events_path = session_derived_file(session_path, TRANSCRIPT_EVENTS_JSONL)
        self._write_jsonl_rows(transcript_events_path, transcript_events, "transcript events JSONL")

        enriched = base_events + transcript_events
        enriched.sort(key=lambda e: (str(e.get("ts_utc", "")), int(e.get("event_id", 0) or 0)))

        enriched_jsonl_path = os.path.join(session_path, ENRICHED_EVENTS_JSONL)
        self._write_jsonl_rows(enriched_jsonl_path, enriched, "enriched JSONL")

        # Write explicit friendly file names for analysts comparing pre/post transcription logs.
        without_transcripts_path = os.path.join(session_path, LOGS_WITHOUT_TRANSCRIPTS_JSONL)
        self._write_jsonl_rows(without_transcripts_path, base_events, "logs without transcription JSONL")

        with_transcripts_path = os.path.join(session_path, LOGS_WITH_TRANSCRIPTS_JSONL)
        self._write_jsonl_rows(with_transcripts_path, enriched, "logs with transcription JSONL")

        enriched_csv_path = session_derived_file(session_path, ENRICHED_EVENTS_CSV)
        self._write_enriched_csv_rows(enriched_csv_path, enriched, "enriched CSV")

        self._log(f"[ok] Rebuilt enriched datasets for session: {os.path.basename(session_path)}")

    def _process_one_wav(self, session_path: str, wav_path: str, context: dict[str, object]) -> None:
        assert self.model is not None

        wav_file = os.path.basename(wav_path)
        transcript_dir = os.path.join(session_path, "audio-notes", "transcripts")
        make_dir(transcript_dir)
        txt_name = canonical_transcript_txt_name(wav_file)
        txt_path = os.path.join(transcript_dir, txt_name)

        note_meta = dict(context.get("audio_by_file", {}).get(wav_file, {}))
        note_id = int(note_meta.get("audio_note_id", parse_audio_note_id(wav_file)) or -1)
        wav_meta = self._read_wav_metadata(wav_path)

        st = os.stat(wav_path)
        file_mtime_utc = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(st.st_mtime))

        run_number = int(context.get("run_number", 0) or 0)
        run_id = str(context.get("run_id", "") or normalize_run_id(run_number, ""))
        session_id = str(context.get("session_id", "") or "")

        record: dict[str, object] = {
            "ts_utc": utc_now_iso(),
            "session_id": session_id,
            "run_number": run_number,
            "run_id": run_id,
            "audio_note_id": note_id,
            "note_label": note_meta.get("note_label", ""),
            "note_body": note_meta.get("note_body", ""),
            "wav_file": wav_file,
            "audio_file_path": wav_path,
            "transcript_file_path": txt_path,
            "wav_frames": wav_meta.get("wav_frames", 0),
            "wav_sample_rate": wav_meta.get("wav_sample_rate", 0),
            "wav_channels": wav_meta.get("wav_channels", 0),
            "wav_duration_sec": wav_meta.get("wav_duration_sec", 0.0),
            "started_utc": note_meta.get("started_utc", ""),
            "started_local": note_meta.get("started_local", ""),
            "ended_utc": note_meta.get("ended_utc", ""),
            "position_ts_utc": note_meta.get("position_ts_utc", "") or note_meta.get("started_utc", "") or file_mtime_utc,
            "position_ts_local": note_meta.get("position_ts_local", "") or note_meta.get("started_local", ""),
            "transcript_text": "",
            "transcript_status": "pending",
            "transcript_engine": "vosk",
            "transcribed_utc": "",
            "transcript_confidence": None,
            "corrected_transcript_text": None,
            "correction_status": "none",
            "corrected_utc": None,
            "language_hint": "en-us",
            "status": "pending",
            "error": "",
        }

        try:
            text = transcribe_wav(self.model, wav_path)
            record["transcript_text"] = text
            record["transcript_status"] = "ok"
            record["status"] = "ok"
            record["transcribed_utc"] = utc_now_iso()
            self._write_sidecar_txt(txt_path, text)
            self._log(f"[ok] {wav_file} -> {txt_name}")
        except Exception as e:
            record["transcript_status"] = "failed"
            record["status"] = "failed"
            record["transcribed_utc"] = utc_now_iso()
            record["error"] = str(e)
            self._log(f"[error] {wav_file}: {e}")

        self._upsert_transcript_record(session_path, record)

        if self.send_live_osc_var.get():
            try:
                send_osc(
                    self.osc_ip_var.get().strip() or "127.0.0.1",
                    self._safe_osc_port(),
                    self.osc_address_var.get().strip() or OSC_ADDRESS_TRANSCRIPT,
                    [json.dumps(record, ensure_ascii=False)])
                self._log(f"[ok] Live OSC transcript sent for {wav_file}")
            except Exception as e:
                self._log(f"[error] Live OSC send failed for {wav_file}: {e}")

        state = self._load_session_state(session_path)
        state[wav_file] = {
            "mtime": st.st_mtime,
            "size": st.st_size,
            "processed_utc": utc_now_iso(),
            "status": record.get("status", "ok"),
        }
        self._save_session_state(session_path)

    def _scan_session(self, session_path: str) -> tuple[int, int]:
        self._ensure_session_layout(session_path)
        self._write_outputs_guide(session_path)
        audio_notes_dir = os.path.join(session_path, "audio-notes")
        
        any_processed = False
        discovered = 0
        processed = 0
        
        # Process audio notes if directory exists
        if os.path.isdir(audio_notes_dir):
            state = self._load_session_state(session_path)
            context = self._build_session_context(session_path)
            self._ensure_pending_transcript_records(session_path, context)

            for name in sorted(os.listdir(audio_notes_dir)):
                if not name.lower().endswith(".wav"):
                    continue
                discovered += 1

                wav_path = os.path.join(audio_notes_dir, name)
                if not os.path.isfile(wav_path):
                    continue

                st = os.stat(wav_path)
                prev = state.get(name)
                already = (
                    isinstance(prev, dict)
                    and prev.get("mtime") == st.st_mtime
                    and prev.get("size") == st.st_size
                )
                if already:
                    continue

                self._process_one_wav(session_path, wav_path, context)
                any_processed = True
                processed += 1
        else:
            context = self._build_session_context(session_path)

        # Rebuild enriched datasets after each scan to ensure transcript events are
        # aligned into session-level datasets even for previously processed WAV files.
        # Also rebuild if there are base events to process, even if no transcripts exist.
        transcripts_exist = os.path.isfile(os.path.join(session_path, TRANSCRIPTS_JSONL))
        base_events_exist = os.path.isfile(os.path.join(session_path, BASE_EVENTS_JSONL))
        if any_processed or transcripts_exist or base_events_exist:
            self._rebuild_enriched_datasets(session_path, context)

        self._write_enhanced_session_summary(session_path, self._load_events_for_summary(session_path))

        # Keep output guide fresh after any rebuild/scan run.
        self._write_outputs_guide(session_path)

        return (discovered, processed)

    def _scan_all_sessions(self) -> None:
        self._log("[scan] Resolving logs root...")
        raw_root = self.root_path_var.get().strip()

        # Prefer already-valid path immediately to avoid unnecessary root scanning.
        if self._is_session_folder(raw_root) or self._looks_like_launch_logs_root(raw_root):
            root_path = raw_root
            self._resolved_root_path = raw_root
        else:
            root_path = self._resolve_and_cache_logs_root(raw_root)

        if not root_path:
            self._log("[error] Could not resolve logs root. Pick AaltoLaunchLogs or a session_* folder.")
            return

        self.root_path_var.set(root_path)
        sessions = self._list_session_folders(root_path)
        if not sessions:
            self._log(f"[info] No session folders found under: {root_path}")
            return

        total_discovered = 0
        total_processed = 0
        for session in sessions:
            try:
                discovered, processed = self._scan_session(session)
            except Exception as e:
                self._log(f"[error] Failed scanning session {os.path.basename(session)}: {e}")
                continue
            total_discovered += discovered
            total_processed += processed
            self._log(f"[scan] {os.path.basename(session)} | wav found={discovered} | newly processed={processed}")

        self._log(f"[scan] complete | root={root_path} | sessions={len(sessions)} | wav found={total_discovered} | newly processed={total_processed}")

    def scan_once(self) -> None:
        if self.model is None and not self._load_model():
            return
        self._scan_all_sessions()
        self._refresh_correction_records()

    def _loop(self) -> None:
        while self.running:
            try:
                self._scan_all_sessions()
            except Exception as e:
                self._log(f"[error] scan loop failed: {e}")
            time.sleep(self._poll_seconds())

    def start(self) -> None:
        if self.model is None and not self._load_model():
            return
        self._log("[run] Manual transcription requested.")
        try:
            self._scan_all_sessions()
            self._refresh_correction_records()
        except Exception as e:
            self._log(f"[error] Manual transcription failed: {e}")

    def _tick(self) -> None:
        # Kept for compatibility; manual mode does not use periodic scanning.
        return

    def stop(self) -> None:
        # Kept for compatibility; manual mode does not use periodic scanning.
        self._log("[stop] Manual mode: nothing to stop.")

    def on_close(self) -> None:
        self.stop()
        self.root.destroy()


if __name__ == "__main__":
    root = tk.Tk()
    app = AudioNoteTranscriberApp(root)
    root.mainloop()

import tkinter as tk
from tkinter import Canvas, Frame
from PIL import Image, ImageTk
import requests
from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import BlockingOSCUDPServer
import threading
import os
import json

# Philips Hue Bridge configuration
BRIDGE_IP = "192.168.1.106"
USER_API = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR"

# Memory for storing light states
memory = {i: [(0, 0, 0) for _ in range(8)] for i in range(1, 21)}  # Default to (0, 0, 0) for all lights
current_memory = 1
osc_active = True  # Tracks if OSC input is enabled
base_color_image = None
logical_to_bridge_id = {}
logical_to_uniqueid = {}
uniqueid_to_bridge_id = {}
bridge_lights_cache = {}
light_labels = {}
CONFIG_FILE = "lights_controller_config_v15.json"
persistent_memories = {str(i): {} for i in range(1, 21)}

def get_config_path():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(script_dir, CONFIG_FILE)

def fetch_lights_data():
    """Return Hue light dictionary keyed by bridge light ID."""
    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights"
    response = requests.get(url, timeout=3)
    response.raise_for_status()
    lights = response.json()
    return lights if isinstance(lights, dict) else {}

def fetch_available_light_ids():
    """Return sorted Hue bridge light IDs, prioritizing reachable lights."""
    try:
        lights = fetch_lights_data()
    except requests.RequestException as e:
        print(f"Failed to fetch lights from bridge: {e}")
        return []

    def _sort_key(light_id):
        return int(light_id) if str(light_id).isdigit() else str(light_id)

    reachable = [
        light_id for light_id, light_data in lights.items()
        if light_data.get("state", {}).get("reachable", True)
    ]

    candidates = reachable if reachable else list(lights.keys())
    return sorted(candidates, key=_sort_key)

def assign_light_mapping():
    """Map controller slots 1-8 to currently available bridge light IDs."""
    global logical_to_bridge_id, logical_to_uniqueid, uniqueid_to_bridge_id, bridge_lights_cache

    try:
        bridge_lights_cache = fetch_lights_data()
    except requests.RequestException as e:
        print(f"Failed to fetch lights from bridge: {e}")
        bridge_lights_cache = {}
        logical_to_bridge_id = {}
        logical_to_uniqueid = {}
        uniqueid_to_bridge_id = {}
        return

    def _sort_key(light_id):
        return int(light_id) if str(light_id).isdigit() else str(light_id)

    reachable = [
        light_id for light_id, light_data in bridge_lights_cache.items()
        if light_data.get("state", {}).get("reachable", True)
    ]

    available_light_ids = sorted(reachable if reachable else list(bridge_lights_cache.keys()), key=_sort_key)
    logical_to_bridge_id = {
        slot: available_light_ids[slot - 1]
        for slot in range(1, min(9, len(available_light_ids) + 1))
    }
    logical_to_uniqueid = {
        slot: bridge_lights_cache.get(bridge_id, {}).get("uniqueid")
        for slot, bridge_id in logical_to_bridge_id.items()
    }
    uniqueid_to_bridge_id = {
        light_data.get("uniqueid"): bridge_id
        for bridge_id, light_data in bridge_lights_cache.items()
        if light_data.get("uniqueid")
    }

    if not logical_to_bridge_id:
        print("No Hue lights detected. Controls will not affect any light.")
    else:
        print("Light mapping (controller slot -> bridge light ID/name):")
        for slot in range(1, 9):
            bridge_id = logical_to_bridge_id.get(slot)
            if bridge_id is None:
                print(f"  {slot} -> (unassigned)")
            else:
                light_name = bridge_lights_cache.get(bridge_id, {}).get("name", "Unknown")
                print(f"  {slot} -> {bridge_id} ({light_name})")

def update_light_labels():
    for slot, label in light_labels.items():
        bridge_id = logical_to_bridge_id.get(slot)
        if bridge_id is None:
            label.config(text=f"Light {slot} (unassigned)")
        else:
            light_name = bridge_lights_cache.get(bridge_id, {}).get("name", "Unknown")
            label.config(text=f"Light {slot} ({light_name}, ID {bridge_id})")

def _memory_list_to_uniqueid_dict(memory_list):
    slot_to_state = {}
    for slot, (bri, hue, sat) in enumerate(memory_list, start=1):
        uniqueid = logical_to_uniqueid.get(slot)
        if uniqueid:
            slot_to_state[uniqueid] = [int(bri), int(hue), int(sat)]
    return slot_to_state

def _uniqueid_dict_to_memory_list(uniqueid_state):
    memory_list = [(0, 0, 0) for _ in range(8)]
    for slot in range(1, 9):
        uniqueid = logical_to_uniqueid.get(slot)
        if not uniqueid:
            continue
        saved_state = uniqueid_state.get(uniqueid)
        if isinstance(saved_state, list) and len(saved_state) == 3:
            memory_list[slot - 1] = (int(saved_state[0]), int(saved_state[1]), int(saved_state[2]))
    return memory_list

def save_configuration():
    try:
        payload = {
            "version": 1,
            "current_memory": int(current_memory),
            "persistent_memories": persistent_memories,
        }
        with open(get_config_path(), "w", encoding="utf-8") as config_file:
            json.dump(payload, config_file, indent=2)
        print(f"Configuration saved to {get_config_path()}")
    except OSError as e:
        print(f"Failed to save configuration: {e}")

def load_configuration():
    global current_memory, persistent_memories
    config_path = get_config_path()
    if not os.path.exists(config_path):
        print("No saved configuration found. Using defaults.")
        return

    try:
        with open(config_path, "r", encoding="utf-8") as config_file:
            loaded = json.load(config_file)
    except (OSError, json.JSONDecodeError) as e:
        print(f"Failed to load configuration: {e}")
        return

    loaded_memories = loaded.get("persistent_memories", {})
    new_persistent_memories = {str(i): {} for i in range(1, 21)}
    for memory_key, state_map in loaded_memories.items():
        if memory_key in new_persistent_memories and isinstance(state_map, dict):
            new_persistent_memories[memory_key] = state_map

    persistent_memories = new_persistent_memories

    loaded_current_memory = loaded.get("current_memory", 1)
    if isinstance(loaded_current_memory, int) and 1 <= loaded_current_memory <= 20:
        current_memory = loaded_current_memory

    for memory_id in range(1, 21):
        memory[memory_id] = _uniqueid_dict_to_memory_list(persistent_memories[str(memory_id)])

    print(f"Configuration loaded from {config_path}")

def _sync_current_memory_to_persistent():
    persistent_memories[str(current_memory)] = _memory_list_to_uniqueid_dict(memory[current_memory])

def save_current_memory_to_disk():
    _sync_current_memory_to_persistent()
    save_configuration()

def load_configuration_and_apply():
    load_configuration()
    update_light_labels()
    select_memory(current_memory)

def refresh_light_mapping():
    assign_light_mapping()
    update_light_labels()
    for memory_id in range(1, 21):
        memory[memory_id] = _uniqueid_dict_to_memory_list(persistent_memories[str(memory_id)])
    select_memory(current_memory)

def get_base_color_image():
    global base_color_image
    if base_color_image is None:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        img_path = os.path.join(script_dir, "app.png")
        base_color_image = Image.open(img_path).convert("RGB")
    return base_color_image

def set_light_state(light_slot, brightness, hue, saturation):
    bridge_light_id = logical_to_bridge_id.get(light_slot)
    if bridge_light_id is None:
        print(f"Skipping Light {light_slot}: no mapped Hue bridge light ID.")
        return

    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{bridge_light_id}/state"
    payload = {"on": True, "bri": brightness, "hue": hue, "sat": saturation} if brightness > 0 else {"on": False}
    response = requests.put(url, json=payload)
    if response.status_code == 200:
        print(f"Light {light_slot} (ID {bridge_light_id}) set to brightness {brightness}, hue {hue}, saturation {saturation}")
    else:
        print(f"Failed to set Light {light_slot} (ID {bridge_light_id})")

def create_color_field_and_brightness_slider(parent, light_id, row, column):
    brightness = tk.IntVar(value=254)  # Default brightness
    color_image = get_base_color_image()

    def update_light(event=None):
        bri = brightness.get()
        hue = hue_vars[light_id]
        sat = sat_vars[light_id]
        set_light_state(light_id, bri, hue, sat)

    def redraw_color_canvas(event=None):
        width = max(2, canvas.winfo_width())
        height = max(2, canvas.winfo_height())
        resized = color_image.resize((width, height), Image.Resampling.LANCZOS)
        img_tk = ImageTk.PhotoImage(resized)
        canvas.delete("all")
        canvas.create_image(0, 0, anchor=tk.NW, image=img_tk)
        canvas.image = img_tk

    def on_canvas_click(event):
        canvas_width = max(1, canvas.winfo_width())
        canvas_height = max(1, canvas.winfo_height())
        x = min(max(event.x, 0), canvas_width - 1)
        y = min(max(event.y, 0), canvas_height - 1)

        img_x = int(x * (color_image.width - 1) / max(1, canvas_width - 1))
        img_y = int(y * (color_image.height - 1) / max(1, canvas_height - 1))

        rgb = color_image.getpixel((img_x, img_y))
        r, g, b = rgb
        max_val = max(r, g, b)
        min_val = min(r, g, b)
        delta = max_val - min_val
        hue = 0
        if delta != 0:
            if max_val == r:
                hue = (60 * ((g - b) / delta) + 360) % 360
            elif max_val == g:
                hue = (60 * ((b - r) / delta) + 120) % 360
            elif max_val == b:
                hue = (60 * ((r - g) / delta) + 240) % 360
        saturation = 0 if max_val == 0 else delta / max_val

        hue_vars[light_id] = int(hue / 360 * 65535)
        sat_vars[light_id] = int(saturation * 254)
        update_light()

    label = tk.Label(parent, text=f"Light {light_id}")
    label.grid(row=row, column=column, padx=10, pady=5, sticky="ew")
    light_labels[light_id] = label

    brightness_slider = tk.Scale(parent, from_=0, to=254, orient=tk.HORIZONTAL, variable=brightness, command=update_light)
    brightness_slider.grid(row=row + 1, column=column, padx=10, sticky="ew")

    canvas = Canvas(parent, width=256, height=256)
    canvas.grid(row=row + 2, column=column, padx=10, sticky="nsew")
    parent.columnconfigure(column, weight=1)
    parent.rowconfigure(row + 2, weight=1)

    redraw_color_canvas()
    canvas.bind("<Button-1>", on_canvas_click)
    canvas.bind("<Configure>", redraw_color_canvas)

    return brightness

def record_memory():
    global current_memory
    memory[current_memory] = [(brightness_vars[light_id].get(), hue_vars[light_id], sat_vars[light_id]) for light_id in range(1, 9)]
    _sync_current_memory_to_persistent()
    print(f"Memory {current_memory} recorded: {memory[current_memory]}")

def select_memory(memory_id):
    global current_memory
    current_memory = memory_id
    persistent_state = persistent_memories.get(str(memory_id), {})
    if isinstance(persistent_state, dict) and persistent_state:
        memory[memory_id] = _uniqueid_dict_to_memory_list(persistent_state)
    active_memory_label.config(text=f"Active Memory: Memory {current_memory}")
    for light_id, (bri, hue, sat) in enumerate(memory[memory_id], start=1):
        brightness_vars[light_id].set(bri)
        hue_vars[light_id] = hue
        sat_vars[light_id] = sat
        set_light_state(light_id, bri, hue, sat)

def osc_memory_handler(address, *args):
    print(f"Received OSC message: Address={address}, Args={args}")  # Display the received message in the terminal
    
    # Handle specific OSC messages based on the message content
    if len(args) > 0:
        try:
            memory_name = str(args[0])  # Convert the argument to a string
            if memory_name.startswith("memory"):
                memory_id = int(memory_name.split(" ")[1])  # Extract the number after 'memory'
                if 1 <= memory_id <= 20:
                    print(f"Triggering Memory {memory_id} (as if the button was pressed)")
                    select_memory(memory_id)  # Trigger the corresponding memory
                else:
                    print(f"Invalid memory ID: {memory_id}. Must be between 1 and 20.")
            else:
                print(f"Unhandled argument format: {memory_name}")
        except (IndexError, ValueError) as e:
            print(f"Error parsing memory argument: {args[0]} - {e}")
    else:
        print(f"No arguments provided in OSC message.")

def toggle_osc():
    global osc_active
    if osc_active:
        osc_active = False
        osc_button.config(text="OSC Input: OFF")
        port_label.config(text="OSC Port: Not Running")
        print("OSC server stopped.")
    else:
        osc_active = True
        osc_button.config(text="OSC Input: ON")
        threading.Thread(target=start_osc_server, daemon=True).start()

def start_osc_server():
    try:
        dispatcher = Dispatcher()
        dispatcher.map("*", osc_memory_handler)  # Catch all incoming OSC messages
        server = BlockingOSCUDPServer(("0.0.0.0", 4444), dispatcher)
        port_label.config(text="OSC Port: 4444")
        print("OSC server running on port 4444...")
        server.serve_forever()
    except OSError as e:
        port_label.config(text="OSC Port: Error")
        print(f"OSC server error: {e}")

def on_app_close():
    root.destroy()

# Initialize Tkinter
root = tk.Tk()
root.title("Philips Hue Light Control")
root.minsize(900, 600)

# Make the root window resize-aware.
root.rowconfigure(0, weight=1)
root.columnconfigure(0, weight=1)

# Scrollable container so controls remain accessible when the window is small.
main_container = Frame(root)
main_container.grid(row=0, column=0, sticky="nsew")
main_container.rowconfigure(0, weight=1)
main_container.columnconfigure(0, weight=1)

ui_canvas = Canvas(main_container, highlightthickness=0)
ui_canvas.grid(row=0, column=0, sticky="nsew")

v_scroll = tk.Scrollbar(main_container, orient=tk.VERTICAL, command=ui_canvas.yview)
v_scroll.grid(row=0, column=1, sticky="ns")

h_scroll = tk.Scrollbar(main_container, orient=tk.HORIZONTAL, command=ui_canvas.xview)
h_scroll.grid(row=1, column=0, sticky="ew")

ui_canvas.configure(yscrollcommand=v_scroll.set, xscrollcommand=h_scroll.set)

content_frame = Frame(ui_canvas)
canvas_window = ui_canvas.create_window((0, 0), window=content_frame, anchor="nw")

def _update_scroll_region(event=None):
    ui_canvas.configure(scrollregion=ui_canvas.bbox("all"))

def _fit_content_to_canvas(event):
    # Keep content width at least as wide as the viewport, but allow horizontal scrolling when needed.
    content_width = max(event.width, content_frame.winfo_reqwidth())
    ui_canvas.itemconfigure(canvas_window, width=content_width)

content_frame.bind("<Configure>", _update_scroll_region)
ui_canvas.bind("<Configure>", _fit_content_to_canvas)

content_frame.columnconfigure(0, weight=1)
content_frame.columnconfigure(1, weight=0)
content_frame.rowconfigure(0, weight=1)

lights_frame = Frame(content_frame)
lights_frame.grid(row=0, column=0, padx=10, pady=10, sticky="nsew")

memories_frame = Frame(content_frame)
memories_frame.grid(row=0, column=1, padx=10, pady=10, sticky="n")

active_memory_label = tk.Label(memories_frame, text="Active Memory: Memory 1", font=("Arial", 12))
active_memory_label.grid(row=0, column=0, columnspan=5, padx=10, pady=10)

port_label = tk.Label(memories_frame, text="OSC Port: Starting...", font=("Arial", 10))
port_label.grid(row=7, column=0, columnspan=5, padx=10, pady=10)

brightness_vars = {i: None for i in range(1, 9)}
hue_vars = {i: 0 for i in range(1, 9)}
sat_vars = {i: 0 for i in range(1, 9)}

assign_light_mapping()
load_configuration()

for light_id in range(1, 5):
    brightness_vars[light_id] = create_color_field_and_brightness_slider(lights_frame, light_id, 0, light_id - 1)

for light_id in range(5, 9):
    brightness_vars[light_id] = create_color_field_and_brightness_slider(lights_frame, light_id, 5, light_id - 5)

update_light_labels()

for i in range(1, 21):
    btn_memory = tk.Button(memories_frame, text=f"Memory {i}", command=lambda i=i: select_memory(i))
    btn_memory.grid(row=(i - 1) // 5 + 1, column=(i - 1) % 5, padx=5, pady=5)

btn_record = tk.Button(memories_frame, text="Record", command=record_memory)
btn_record.grid(row=5, column=0, columnspan=5, padx=10, pady=10)

osc_button = tk.Button(memories_frame, text="OSC Input: ON", command=toggle_osc)
osc_button.grid(row=6, column=0, columnspan=5, padx=10, pady=10)

if osc_active:
    threading.Thread(target=start_osc_server, daemon=True).start()

btn_refresh_ids = tk.Button(memories_frame, text="Refresh Light IDs", command=refresh_light_mapping)
btn_refresh_ids.grid(row=8, column=0, columnspan=5, padx=10, pady=10)

btn_save_config = tk.Button(memories_frame, text="Save Configuration", command=save_current_memory_to_disk)
btn_save_config.grid(row=9, column=0, columnspan=5, padx=10, pady=10)

btn_load_config = tk.Button(memories_frame, text="Load Configuration", command=load_configuration_and_apply)
btn_load_config.grid(row=10, column=0, columnspan=5, padx=10, pady=10)

select_memory(current_memory)

root.protocol("WM_DELETE_WINDOW", on_app_close)

root.mainloop()
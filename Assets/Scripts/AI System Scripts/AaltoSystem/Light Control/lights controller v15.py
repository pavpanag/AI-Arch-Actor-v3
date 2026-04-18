import tkinter as tk
from tkinter import Canvas, Frame
from PIL import Image, ImageTk
import requests
from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import BlockingOSCUDPServer
import threading
import os

# Philips Hue Bridge configuration
BRIDGE_IP = "192.168.1.106"
USER_API = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR"

# Memory for storing light states
memory = {i: [(0, 0, 0) for _ in range(8)] for i in range(1, 21)}  # Default to (0, 0, 0) for all lights
current_memory = 1
osc_active = False  # Tracks if OSC input is enabled
base_color_image = None

def get_base_color_image():
    global base_color_image
    if base_color_image is None:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        img_path = os.path.join(script_dir, "app.png")
        base_color_image = Image.open(img_path).convert("RGB")
    return base_color_image

def set_light_state(light_id, brightness, hue, saturation):
    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{light_id}/state"
    payload = {"on": True, "bri": brightness, "hue": hue, "sat": saturation} if brightness > 0 else {"on": False}
    response = requests.put(url, json=payload)
    if response.status_code == 200:
        print(f"Light {light_id} set to brightness {brightness}, hue {hue}, saturation {saturation}")
    else:
        print(f"Failed to set light {light_id}")

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
    print(f"Memory {current_memory} recorded: {memory[current_memory]}")

def select_memory(memory_id):
    global current_memory
    current_memory = memory_id
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

port_label = tk.Label(memories_frame, text="OSC Port: Not Running", font=("Arial", 10))
port_label.grid(row=7, column=0, columnspan=5, padx=10, pady=10)

brightness_vars = {i: None for i in range(1, 9)}
hue_vars = {i: 0 for i in range(1, 9)}
sat_vars = {i: 0 for i in range(1, 9)}

for light_id in range(1, 5):
    brightness_vars[light_id] = create_color_field_and_brightness_slider(lights_frame, light_id, 0, light_id - 1)

for light_id in range(5, 9):
    brightness_vars[light_id] = create_color_field_and_brightness_slider(lights_frame, light_id, 5, light_id - 5)

for i in range(1, 21):
    btn_memory = tk.Button(memories_frame, text=f"Memory {i}", command=lambda i=i: select_memory(i))
    btn_memory.grid(row=(i - 1) // 5 + 1, column=(i - 1) % 5, padx=5, pady=5)

btn_record = tk.Button(memories_frame, text="Record", command=record_memory)
btn_record.grid(row=5, column=0, columnspan=5, padx=10, pady=10)

osc_button = tk.Button(memories_frame, text="OSC Input: OFF", command=toggle_osc)
osc_button.grid(row=6, column=0, columnspan=5, padx=10, pady=10)

root.mainloop()
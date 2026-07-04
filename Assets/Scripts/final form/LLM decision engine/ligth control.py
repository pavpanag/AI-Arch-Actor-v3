import tkinter as tk
import requests

# Philips Hue Bridge Configuration
BRIDGE_IP = "192.168.1.106"
USER_API = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR"
LIGHT_ID = 5  # The bulb we are controlling

def set_light_state():
    """Sends the current slider values to the Philips Hue Bridge."""
    hue = hue_slider.get()
    brightness = brightness_slider.get()
    saturation = saturation_slider.get()

    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{LIGHT_ID}/state"
    payload = {"on": True, "hue": hue, "bri": brightness, "sat": saturation}
    
    response = requests.put(url, json=payload)
    if response.status_code == 200:
        status_label.config(text=f"Light {LIGHT_ID} - Hue: {hue}, Brightness: {brightness}, Saturation: {saturation}")
    else:
        status_label.config(text="Failed to update light!")

# GUI Setup
root = tk.Tk()
root.title("Philips Hue Light Control")

# Create sliders
hue_slider = tk.Scale(root, from_=0, to=65535, orient="horizontal", label="Hue", command=lambda x: set_light_state())
hue_slider.pack()

brightness_slider = tk.Scale(root, from_=0, to=254, orient="horizontal", label="Brightness", command=lambda x: set_light_state())
brightness_slider.pack()

saturation_slider = tk.Scale(root, from_=0, to=254, orient="horizontal", label="Saturation", command=lambda x: set_light_state())
saturation_slider.pack()

# Status Label
status_label = tk.Label(root, text="Adjust sliders to control the light")
status_label.pack()

# Run GUI loop
root.mainloop()

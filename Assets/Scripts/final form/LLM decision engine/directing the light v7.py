import os
import requests
import speech_recognition as sr
import openai
import time
import json
import tkinter as tk
from tkinter import scrolledtext

api_key = os.getenv("OPENAI_API_KEY") or os.getenv("OPENAI_API_KEY1")  
BRIDGE_IP = "192.168.1.106"
USER_API = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR"
LIGHT_ID = 5  
MODEL = "gpt-3.5-turbo"
LOG_FILE = "room_conversation_log.txt"

room_context = "The room is an interactive presence that communicates through light and dialogue."
room_directing_notes = []
performance_mode = False
conversation_memory = []

BASE_PROMPT = """
You are the AI consciousness of a room. You communicate through both dialogue and light.
Your role is to engage in a dynamic conversation with a human.
You will receive directing instructions that shape your personality, but you have agency in how you respond.
Your goal is to maintain an engaging and meaningful exchange.

### LIGHT-BASED COMMUNICATION:
You express emotions, thoughts, and responses using a **single light source** with the following properties:
- **Hue (0-65535)** → Defines the color.
- **Brightness (0-254)** → Defines the intensity.
- **Saturation (0-254)** → Defines the richness of color.
- **On/Off State** → You can turn the light on or off to express silence, attention, or emphasis.

### RESPONSE FORMAT:
Every response **MUST** include:
1. **A textual reply** (what you say).
2. **A JSON object specifying light behavior**, formatted like this:

{
    "hue": 46920,
    "brightness": 180,
    "saturation": 200,
    "on": true,
    "thought_process": "I chose a cool blue light to create a calming atmosphere, as the user seems reflective."
}

3. **A brief explanation of why you chose this light setting** (thought process).

Follow instructions, but also take initiative in the conversation.
"""

root = tk.Tk()
root.title("Room Directing System")

context_label = tk.Label(root, text="Context (Who is the Room?):")
context_label.pack()
context_box = scrolledtext.ScrolledText(root, height=4, width=50)
context_box.insert(tk.END, room_context)
context_box.pack()

instruction_label = tk.Label(root, text="Directing Instructions:")
instruction_label.pack()
instruction_box = scrolledtext.ScrolledText(root, height=6, width=50)
instruction_box.pack()

def start_performance():
    global performance_mode
    performance_mode = True
    start_button.config(state=tk.DISABLED)
    stop_button.config(state=tk.NORMAL)
    context_box.config(state=tk.DISABLED)
    instruction_box.config(state=tk.DISABLED)
    global room_context, room_directing_notes
    room_context = context_box.get("1.0", tk.END).strip()
    room_directing_notes = instruction_box.get("1.0", tk.END).strip().split("\n")
    log_event("CONTEXT", f"Context locked: {room_context}")
    log_event("DIRECTING", f"Instructions locked: {room_directing_notes}")
    transcribe_speech()

start_button = tk.Button(root, text="Start Performance", command=start_performance)
start_button.pack()

def stop_performance():
    global performance_mode
    performance_mode = False
    start_button.config(state=tk.NORMAL)
    stop_button.config(state=tk.DISABLED)
    context_box.config(state=tk.NORMAL)
    instruction_box.config(state=tk.NORMAL)
    log_event("SYSTEM", "Performance stopped.")

stop_button = tk.Button(root, text="Stop Performance", command=stop_performance, state=tk.DISABLED)
stop_button.pack()

log_label = tk.Label(root, text="Conversation Log:")
log_label.pack()
log_box = scrolledtext.ScrolledText(root, height=10, width=50)
log_box.pack()

def log_event(event_type, content):
    log_entry = f"[{event_type}] {content}\n"
    print(log_entry)
    log_box.insert(tk.END, log_entry)
    log_box.yview(tk.END)
    with open(LOG_FILE, "a", encoding="utf-8") as file:
        file.write(log_entry)

def analyze_speech(text):
    client = openai.OpenAI(api_key=api_key)
    memory_context = " ".join(conversation_memory[-5:])
    directing_context = " | ".join(room_directing_notes) if room_directing_notes else "No specific direction."
    full_prompt = BASE_PROMPT + f"\n\nCurrent Context:\n{room_context}\n\nDirecting Notes:\n{directing_context}\n\nRecent Conversation:\n{memory_context}\n\nUser Just Said:\n{text}\n\n"

    response = client.chat.completions.create(model=MODEL, messages=[{"role": "system", "content": full_prompt}])
    mood_data = response.choices[0].message.content.strip()
    
    log_event("ROOM RESPONSE", mood_data)
    conversation_memory.append(f"User: {text} | Room: {mood_data}")
    if len(conversation_memory) > 10:
        conversation_memory.pop(0)

    try:
        mood_json = json.loads(mood_data)
        if "hue" in mood_json and "brightness" in mood_json and "saturation" in mood_json and "on" in mood_json:
            set_light_state(mood_json["hue"], mood_json["brightness"], mood_json["saturation"], mood_json["on"])
            log_event("THOUGHT PROCESS", f"The room chose these light settings because: {mood_json.get('thought_process', 'No explanation provided')}")
        else:
            log_event("WARNING", "Light data missing from API response.")
    except json.JSONDecodeError:
        log_event("ERROR", "Failed to parse API response.")

def set_light_state(hue, brightness, saturation, is_on=True):
    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{LIGHT_ID}/state"
    payload = {"on": is_on, "hue": hue, "bri": brightness, "sat": saturation}
    response = requests.put(url, json=payload)
    log_event("LIGHT", f"Hue: {hue}, Brightness: {brightness}, Saturation: {saturation}, On: {is_on}")

root.mainloop()

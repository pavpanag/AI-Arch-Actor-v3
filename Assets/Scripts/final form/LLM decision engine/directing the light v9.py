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
MODEL = "gpt-4-turbo"  # Change to test different models: "gpt-3.5-turbo", "gpt-4-turbo", "gpt-4"
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
- **Pulsing, flashing, or fading** can be used for extra expressiveness.

### RESPONSE FORMAT:
Instead of responding with direct dialogue, **explain why you made the light choice**.
Each response **must** be a JSON object with the following:
- **"light_behavior"**: A JSON object with `"hue"`, `"brightness"`, `"saturation"`, `"on"`, and `"effect"` (e.g., `"pulse"`, `"fade"`, `"steady"`).
- **"reasoning"**: An explanation of why this light behavior was chosen, including:
  - How the last 5 dialogues influenced the choice.
  - How the directing instructions affected the choice.
  - Whether this choice continues a pattern, or deliberately shifts the mood.

Example output:
{
    "light_behavior": {
        "hue": 46920,
        "brightness": 180,
        "saturation": 200,
        "on": true,
        "effect": "fade"
    },
    "reasoning": "I chose a cool blue fading light to create a calm atmosphere. The previous dialogues were reflective, so I want to reinforce that mood. The fading effect helps with this, rather than an abrupt change. The directing instruction encourages cooperation, so I'm keeping things smooth and inviting."
}

### BEHAVIOR EVOLUTION:
- **At first, keep changes subtle** (slight hue shifts, stable lights).
- **Over time, become more bold** (experiment with sudden shifts, stronger effects).
- **If the user seems engaged, take initiative in changing the light** without waiting for a direct question.
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
        set_light_state(mood_json["light_behavior"]["hue"], mood_json["light_behavior"]["brightness"], mood_json["light_behavior"]["saturation"], mood_json["light_behavior"]["on"])
        log_event("THOUGHT PROCESS", f"The room chose this light setting because: {mood_json['reasoning']}")
    except json.JSONDecodeError:
        log_event("ERROR", "Failed to parse API response.")

def set_light_state(hue, brightness, saturation, is_on=True):
    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{LIGHT_ID}/state"
    payload = {"on": is_on, "hue": hue, "bri": brightness, "sat": saturation}
    response = requests.put(url, json=payload)
    log_event("LIGHT", f"Hue: {hue}, Brightness: {brightness}, Saturation: {saturation}, On: {is_on}")

def transcribe_speech():
    if not performance_mode:
        return
    recognizer = sr.Recognizer()
    with sr.Microphone() as source:
        log_box.insert(tk.END, "\n💬 PERFORMANCE MODE ACTIVE 💬\n")
        recognizer.adjust_for_ambient_noise(source)
        audio = recognizer.listen(source)
        try:
            text = recognizer.recognize_google(audio, language="en-US")
            log_event("DIALOGUE", text)
            analyze_speech(text)
        except sr.UnknownValueError:
            pass
        except sr.RequestError:
            pass
    if performance_mode:
        root.after(100, transcribe_speech)

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

root.mainloop()
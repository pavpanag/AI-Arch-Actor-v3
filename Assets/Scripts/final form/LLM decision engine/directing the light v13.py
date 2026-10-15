import os
import requests
import speech_recognition as sr
import openai
import time
import json
import tkinter as tk
from tkinter import scrolledtext, ttk

# OpenAI & Philips Hue Configuration
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
- **Effects** → You can apply `"pulse"`, `"fade"`, `"steady"` to modify transitions.

### LISTENING MODE:
- When you are **waiting for input**, the light should be a **dim white that gently fluctuates**:
  {
      "hue": 6500,
      "brightness": 50,
      "saturation": 0,
      "on": true,
      "effect": "pulse"
  }

### RESPONSE MODE:
- When generating a response, **adjust the light dynamically** based on emotional intent.
- Once the response is **delivered**, return to the **fluctuating white listening state**.

### HANDLING UNCLEAR INPUTS:
- If the user’s input is **unclear, garbled, or fragmented**, try to **infer intent**.
- If the meaning is still **uncertain**, ask for **clarification**.
- Use **previous dialogues** to make a **logical assumption**.

### RESPONSE FORMAT:
Each response **must** be a JSON object containing:
- **"light_behavior"**: A JSON object with `"hue"`, `"brightness"`, `"saturation"`, `"on"`, `"effect"` (e.g., `"pulse"`, `"fade"`, `"steady"`).
- **"return_to_listening"**: A boolean (`true`) indicating that after responding, the system should return to the **fluctuating white listening mode**.
- **"reasoning"**: Explanation of why this light behavior was chosen, including:
  - How the last 5 dialogues influenced the choice.
  - How the directing instructions affected the choice.
  - If the choice follows a pattern or deliberately shifts the mood.

IMPORTANT: Always respond in the specified JSON format. Do not include any additional text outside the JSON object.
"""

# GUI Setup
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

# Microphone selection
mic_label = tk.Label(root, text="Select Microphone:")
mic_label.pack()
mic_list = sr.Microphone.list_microphone_names()
mic_var = tk.StringVar()
mic_dropdown = ttk.Combobox(root, textvariable=mic_var, values=mic_list, state="readonly")
mic_dropdown.pack()
mic_dropdown.current(0)  # Set default selection to the first microphone

def log_event(event_type, content):
    log_entry = f"[{event_type}] {content}\n"
    print(log_entry)
    log_box.insert(tk.END, log_entry)
    log_box.yview(tk.END)
    with open(LOG_FILE, "a", encoding="utf-8") as file:
        file.write(log_entry)

def handle_non_json_response(response_text):
    log_event("ERROR", f"Failed to parse API response: {response_text}")
    # Provide a default response
    default_response = {
        "light_behavior": {
            "hue": 6500,
            "brightness": 50,
            "saturation": 0,
            "on": True,
            "effect": "pulse"
        },
        "return_to_listening": True,
        "reasoning": "Default response due to non-JSON API response."
    }
    return default_response

def is_valid_json(response_text):
    try:
        json.loads(response_text)
        return True
    except ValueError:
        return False

def analyze_speech(text):
    client = openai.OpenAI(api_key=api_key)
    memory_context = " ".join(conversation_memory[-5:])
    directing_context = " | ".join(room_directing_notes) if room_directing_notes else "No specific direction."
    full_prompt = BASE_PROMPT + f"\n\nCurrent Context:\n{room_context}\n\nDirecting Notes:\n{directing_context}\n\nRecent Conversation:\n{memory_context}\n\nUser Just Said:\n{text}\n\n"

    try:
        response = client.chat.completions.create(model=MODEL, messages=[{"role": "system", "content": full_prompt}])
        mood_data = response.choices[0].message.content.strip()

        log_event("ROOM RESPONSE", mood_data)
        conversation_memory.append(f"User: {text} | Room: {mood_data}")
        if len(conversation_memory) > 10:
            conversation_memory.pop(0)

        if is_valid_json(mood_data):
            mood_json = json.loads(mood_data)
        else:
            mood_json = handle_non_json_response(mood_data)

        # Set the light based on the AI's response
        set_light_state(
            mood_json["light_behavior"]["hue"],
            mood_json["light_behavior"]["brightness"],
            mood_json["light_behavior"]["saturation"],
            mood_json["light_behavior"]["on"],
            mood_json["light_behavior"].get("effect", "steady")
        )

        log_event("THOUGHT PROCESS", f"The room chose this light setting because: {mood_json['reasoning']}")

        # Return to fluctuating listening state after responding
        if mood_json.get("return_to_listening", False):
            time.sleep(3)  # Let the response light stay for a moment
            set_light_state(6500, 50, 0, True, "pulse")  # Return to dim fluctuating white light

    except openai.error.OpenAIError as e:
        log_event("ERROR", f"OpenAI API error: {e}")

def set_light_state(hue, brightness, saturation, is_on=True, effect="steady"):
    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{LIGHT_ID}/state"
    payload = {"on": is_on, "hue": hue, "bri": brightness, "sat": saturation, "effect": effect}
    response = requests.put(url, json=payload)
    log_event("LIGHT", f"Hue: {hue}, Brightness: {brightness}, Saturation: {saturation}, Effect: {effect}, On: {is_on}")

def transcribe_speech():
    if not performance_mode:
        return
    recognizer = sr.Recognizer()
    mic_index = mic_list.index(mic_var.get())
    with sr.Microphone(device_index=mic_index) as source:
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

def set_instructions():
    global room_directing_notes
    room_directing_notes = instruction_box.get("1.0", tk.END).strip().split("\n")
    log_event("DIRECTING", f"Instructions updated: {room_directing_notes}")

def start_performance():
    global performance_mode
    performance_mode = True
    start_button.config(state=tk.DISABLED)
    stop_button.config(state=tk.NORMAL)
    context_box.config(state=tk.DISABLED)
    instruction_box.config(state=tk.NORMAL)
    global room_context
    room_context = context_box.get("1.0", tk.END).strip()
    log_event("CONTEXT", f"Context locked: {room_context}")
    set_instructions()
    set_light_state(6500, 50, 0, True, "pulse")  # Initial fluctuating white light
    transcribe_speech()

start_button = tk.Button(root, text="Start Performance", command=start_performance)
start_button.pack()

stop_button = tk.Button(root, text="Stop Performance", command=lambda: set_light_state(0, 0, 0, False))
stop_button.pack()

set_instructions_button = tk.Button(root, text="Set Instructions", command=set_instructions)
set_instructions_button.pack()

log_box = scrolledtext.ScrolledText(root, height=10, width=50)
log_box.pack()

root.mainloop()
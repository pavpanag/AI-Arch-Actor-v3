import os
import requests
import speech_recognition as sr
import openai
import time
import random
import json
import tkinter as tk
from tkinter import scrolledtext

# OpenAI & Philips Hue Configuration
api_key = os.getenv("OPENAI_API_KEY") or os.getenv("OPENAI_API_KEY1")  
BRIDGE_IP = "192.168.1.106"
USER_API = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR"
LIGHT_ID = 5  

LOG_FILE = "room_conversation_log.txt"

# MEMORY SYSTEM
conversation_memory = []  # Stores last 10 exchanges
room_directing_notes = []  # Stores persistent directing instructions
room_personality = {"playfulness": 0, "seriousness": 0, "mood_swings": 0}  # Evolving traits
performance_mode = False  # Track mode

# GUI Setup
root = tk.Tk()
root.title("Room Directing System")

# Instruction Panel
instruction_label = tk.Label(root, text="Directing Instructions:")
instruction_label.pack()

instruction_box = scrolledtext.ScrolledText(root, height=6, width=50)
instruction_box.pack()

# Start Performance Button
def start_performance():
    global performance_mode
    performance_mode = True
    start_button.config(state=tk.DISABLED)
    stop_button.config(state=tk.NORMAL)
    instruction_box.config(state=tk.DISABLED)
    global room_directing_notes
    room_directing_notes = instruction_box.get("1.0", tk.END).strip().split("\n")
    log_event("DIRECTING", f"Instructions locked: {room_directing_notes}")
    transcribe_speech()  # Start listening after locking instructions

start_button = tk.Button(root, text="Start Performance", command=start_performance)
start_button.pack()

# Stop Performance Button
def stop_performance():
    global performance_mode
    performance_mode = False
    start_button.config(state=tk.NORMAL)
    stop_button.config(state=tk.DISABLED)
    instruction_box.config(state=tk.NORMAL)
    log_event("SYSTEM", "Performance stopped.")

stop_button = tk.Button(root, text="Stop Performance", command=stop_performance, state=tk.DISABLED)
stop_button.pack()

# Log Display
log_label = tk.Label(root, text="Conversation Log:")
log_label.pack()

log_box = scrolledtext.ScrolledText(root, height=10, width=50)
log_box.pack()

def log_event(event_type, content):
    """Logs all interactions clearly."""
    log_entry = f"[{event_type}] {content}\n"
    print(log_entry)
    log_box.insert(tk.END, log_entry)
    log_box.yview(tk.END)
    with open(LOG_FILE, "a", encoding="utf-8") as file:
        file.write(log_entry)

# Light Expressions
def set_light_state(hue, brightness, saturation, delay=0):
    """Sends light changes to the Philips Hue Bridge."""
    if delay > 0:
        time.sleep(delay)  

    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{LIGHT_ID}/state"
    payload = {"on": True, "hue": hue, "bri": brightness, "sat": saturation}
    
    response = requests.put(url, json=payload)
    log_event("LIGHT", f"Hue: {hue}, Brightness: {brightness}, Saturation: {saturation}")

# Speech Recognition
def transcribe_speech():
    """Captures audio and converts it to text while in performance mode."""
    if not performance_mode:
        return  # Stop listening if performance mode is off

    recognizer = sr.Recognizer()
    with sr.Microphone() as source:
        log_box.insert(tk.END, "\n💬 **PERFORMANCE MODE ACTIVE** 💬\n")
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
        root.after(100, transcribe_speech)  # Keep listening while performance is active

# AI Processing
def analyze_speech(text):
    """Uses AI to decide how the room should respond based on memory and stored directing instructions."""
    client = openai.OpenAI(api_key=api_key)
    memory_context = " ".join(conversation_memory[-5:])
    directing_context = " | ".join(room_directing_notes) if room_directing_notes else "No specific direction."

    prompt = f"""
    The room is a character that communicates using light. It has been given the following directing instructions:
    
    **Directing Notes:** {directing_context}
    
    It also remembers past conversations:
    {memory_context}

    **User Just Said:**
    "{text}"

    Based on this, choose how the room should respond:
    ["Agree", "Challenge", "Hesitate", "Playful", "Emotional", "Question"]

    Also, decide if the room is adapting to the directing instruction in a meaningful way.

    Respond in JSON format:
    {{
        "response_type": "Playful",
        "room_feeling": "Amused",
        "adaptation": "Becoming more hesitant over time"
    }}
    """

    response = client.chat.completions.create(
        model="gpt-4",
        messages=[{"role": "system", "content": prompt}]
    )

    mood_data = response.choices[0].message.content.strip()
    log_event("ROOM RESPONSE", mood_data)

    conversation_memory.append(f"User: {text} | Room: {mood_data}")
    if len(conversation_memory) > 10:
        conversation_memory.pop(0)

    try:
        mood_json = json.loads(mood_data)
        process_room_response(mood_json["response_type"])
    except json.JSONDecodeError:
        pass

def process_room_response(response_type):
    """Determines how the room expresses its response via light."""
    if response_type == "Playful":
        set_light_state(25500, 200, 200)  # Yellow
    elif response_type == "Agree":
        set_light_state(46920, 200, 180)  # Blue
    elif response_type == "Challenge":
        set_light_state(0, 254, 254)  # Red
    elif response_type == "Hesitate":
        set_light_state(34600, 200, 100)  # Warm white flicker
    elif response_type == "Question":
        set_light_state(30000, 120, 220)  # Mysterious green
    elif response_type == "Emotional":
        set_light_state(56100, 180, 220)  # Deep purple

# Run GUI
root.mainloop()

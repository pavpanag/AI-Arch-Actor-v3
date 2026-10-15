import os
import requests
import speech_recognition as sr
import openai
import time
import random
import json

# OpenAI & Philips Hue Configuration
api_key = os.getenv("OPENAI_API_KEY") or os.getenv("OPENAI_API_KEY1")  
BRIDGE_IP = "192.168.1.106"
USER_API = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR"
LIGHT_ID = 5  

LOG_FILE = "room_conversation_log.txt"

# Memory System: Tracks the last 5 interactions
conversation_memory = []

# Light Animations
def blink_light(times=3, speed=0.5):
    """Blinks the light rapidly."""
    for _ in range(times):
        set_light_state(0, 254, 254)  # Bright Red
        time.sleep(speed)
        set_light_state(25500, 200, 200)  # Yellow
        time.sleep(speed)

def pulse_light():
    """Creates a slow pulsing effect."""
    for i in range(100, 255, 30):  # Brightness increase
        set_light_state(46920, i, 200)  # Blue Pulse
        time.sleep(0.2)
    for i in range(255, 100, -30):  # Brightness decrease
        set_light_state(46920, i, 200)
        time.sleep(0.2)

# Light Expressions
LIGHT_EXPRESSIONS = {
    "Agree": lambda: set_light_state(46920, 200, 180),  # Calming blue
    "Challenge": lambda: set_light_state(0, 254, 254),  # Strong red
    "Hesitate": lambda: pulse_light(),  # Pulsing effect
    "Playful": lambda: blink_light(),  # Blinking effect
    "Emotional": lambda: set_light_state(56100, 180, 220),  # Deep purple
    "Question": lambda: set_light_state(30000, 120, 220),  # Mysterious green
}

def log_event(event):
    """Logs all interactions for later analysis."""
    with open(LOG_FILE, "a", encoding="utf-8") as file:
        file.write(event + "\n")

def transcribe_speech(device_index=1):
    """Captures audio and converts it to text."""
    recognizer = sr.Recognizer()
    with sr.Microphone(device_index=device_index) as source:
        print("\nSpeak now... (Listening)")
        recognizer.adjust_for_ambient_noise(source)
        audio = recognizer.listen(source)
    
    try:
        text = recognizer.recognize_google(audio, language="en-US")
        print(f"[USER] {text}")
        log_event(f"[USER] {text}")
        return text
    except sr.UnknownValueError:
        return None
    except sr.RequestError:
        return None

def analyze_speech(text):
    """Uses AI to decide how the room should respond."""
    client = openai.OpenAI(api_key=api_key)
    memory_context = " ".join(conversation_memory[-3:])  # Last 3 exchanges
    
    prompt = f"""
    The room is a character that communicates using light. Given the following statement and past interactions, decide how it should respond.
    
    **Past Dialogue:**
    {memory_context}

    **User Just Said:**
    "{text}"

    Choose from: ["Agree", "Challenge", "Hesitate", "Playful", "Emotional", "Question"]

    Also, interpret what the room "feels" (e.g., thoughtful, amused, annoyed, curious).

    Respond in JSON format:
    {{
        "response_type": "Playful",
        "room_feeling": "Amused"
    }}
    """

    response = client.chat.completions.create(
        model="gpt-4",
        messages=[{"role": "system", "content": prompt}]
    )

    mood_data = response.choices[0].message.content.strip()
    print(mood_data)  
    log_event(f"[ROOM DECISION] {mood_data}")  

    # Store in memory
    conversation_memory.append(f"User: {text} | Room: {mood_data}")
    if len(conversation_memory) > 5:
        conversation_memory.pop(0)  # Keep memory size small

    return mood_data

def set_light_state(hue, brightness, saturation, delay=0):
    """Sends light changes to the Philips Hue Bridge."""
    if delay > 0:
        time.sleep(delay)  

    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{LIGHT_ID}/state"
    payload = {"on": True, "hue": hue, "bri": brightness, "sat": saturation}

    print(f"[DEBUG] Sending request to Hue: {url}")
    print(f"[DEBUG] Payload: {payload}")

    response = requests.put(url, json=payload)
    print(f"[DEBUG] Hue Response: {response.status_code}, {response.text}")

    log_event(f"[LIGHT RESPONSE] Hue: {hue}, Brightness: {brightness}, Saturation: {saturation}")

def process_room_response(response_type):
    """Determines how the room expresses its response via light."""
    if response_type in LIGHT_EXPRESSIONS:
        LIGHT_EXPRESSIONS[response_type]()
    else:
        set_light_state(34600, 200, 100)  # Default neutral

# Main loop: The room "talks" through light
while True:
    speech_text = transcribe_speech(device_index=1)
    
    if speech_text and speech_text.lower() == "exit":
        log_event("[SYSTEM] Exiting system.")
        break

    if speech_text:
        response_data = analyze_speech(speech_text)

        try:
            mood_data = json.loads(response_data)
            response_type = mood_data.get("response_type", "Agree")
            log_event(f"[ROOM EXPRESSION] The room responds as '{response_type}'")
        except json.JSONDecodeError:
            log_event("[ERROR] Failed to parse response.")
            response_type = "Agree"

        process_room_response(response_type)

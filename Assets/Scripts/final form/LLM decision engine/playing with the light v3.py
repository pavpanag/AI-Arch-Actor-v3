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

# MEMORY SYSTEM (Tracks past interactions)
conversation_memory = []  # Stores last 10 exchanges
room_personality = {"playfulness": 0, "seriousness": 0, "mood_swings": 0}  # Evolving traits

# Light Expressions
LIGHT_EXPRESSIONS = {
    "Agree": lambda: set_light_state(46920, 200, 180),  # Calming blue
    "Challenge": lambda: set_light_state(0, 254, 254),  # Strong red
    "Hesitate": lambda: pulse_light(),  # Pulsing effect
    "Playful": lambda: blink_light(),  # Blinking effect
    "Emotional": lambda: set_light_state(56100, 180, 220),  # Deep purple
    "Question": lambda: set_light_state(30000, 120, 220),  # Mysterious green
}

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
    for i in range(100, 255, 30):  
        set_light_state(46920, i, 200)  # Blue Pulse
        time.sleep(0.2)
    for i in range(255, 100, -30):  
        set_light_state(46920, i, 200)
        time.sleep(0.2)

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
    """Uses AI to decide how the room should respond based on memory and personality."""
    client = openai.OpenAI(api_key=api_key)
    memory_context = " ".join(conversation_memory[-5:])  # Last 5 exchanges
    
    prompt = f"""
    The room is a character that communicates using light. Given the following statement and past interactions, decide how it should respond.

    **Past Dialogue:**
    {memory_context}

    **User Just Said:**
    "{text}"

    Current Personality Traits:
    - Playfulness Level: {room_personality["playfulness"]}
    - Seriousness Level: {room_personality["seriousness"]}
    - Mood Swings: {room_personality["mood_swings"]}

    Choose from: ["Agree", "Challenge", "Hesitate", "Playful", "Emotional", "Question"]

    Also, decide if the room is developing a "habit" of responding in a certain way.

    Respond in JSON format:
    {{
        "response_type": "Playful",
        "room_feeling": "Amused",
        "habit_change": "More playful over time"
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
    if len(conversation_memory) > 10:
        conversation_memory.pop(0)  # Keep memory size manageable

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

# Main loop: The room "talks" through light and evolves over time
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
            habit_change = mood_data.get("habit_change", None)

            if habit_change:
                print(f"[ROOM PERSONALITY EVOLVING] {habit_change}")
                log_event(f"[ROOM PERSONALITY EVOLVING] {habit_change}")

            log_event(f"[ROOM EXPRESSION] The room responds as '{response_type}'")
        except json.JSONDecodeError:
            log_event("[ERROR] Failed to parse response.")
            response_type = "Agree"

        process_room_response(response_type)

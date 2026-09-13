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

# Room's Personality: How it chooses to respond
RESPONSE_TYPES = ["Agree", "Challenge", "Hesitate", "Playful", "Emotional", "Question"]

# Light Expressions: How it communicates
LIGHT_EXPRESSIONS = {
    "Agree": {"hue": 46920, "brightness": 200, "saturation": 180},  # Calming blue
    "Challenge": {"hue": 0, "brightness": 254, "saturation": 254},  # Strong red
    "Hesitate": {"hue": 34600, "brightness": 150, "saturation": 100},  # Warm white with flicker
    "Playful": {"hue": 25500, "brightness": 254, "saturation": 200},  # Bright yellow
    "Emotional": {"hue": 56100, "brightness": 180, "saturation": 220},  # Deep purple
    "Question": {"hue": 30000, "brightness": 120, "saturation": 220},  # Mysterious green
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
    prompt = f"""
    The room is a character that communicates using light. Given the following statement, decide how it should respond.
    
    Choose from the following responses: ["Agree", "Challenge", "Hesitate", "Playful", "Emotional", "Question"]
    
    Also, interpret what the room "feels" (e.g., thoughtful, amused, annoyed, curious).
    
    Respond in JSON format:
    {{
        "response_type": "Playful",
        "room_feeling": "Amused"
    }}

    Statement: "{text}"
    """

    response = client.chat.completions.create(
        model="gpt-4",
        messages=[{"role": "system", "content": prompt}]
    )

    mood_data = response.choices[0].message.content.strip()
    print(mood_data)  
    log_event(f"[ROOM DECISION] {mood_data}")  
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
    light_settings = LIGHT_EXPRESSIONS.get(response_type, LIGHT_EXPRESSIONS["Agree"])
    
    # Add a little "thinking time" for realism
    if response_type == "Hesitate":
        print("[ROOM] The room flickers slightly, unsure...")
        time.sleep(1)
        set_light_state(light_settings["hue"], light_settings["brightness"], light_settings["saturation"])
        time.sleep(1)
        set_light_state(LIGHT_EXPRESSIONS["Agree"]["hue"], LIGHT_EXPRESSIONS["Agree"]["brightness"], LIGHT_EXPRESSIONS["Agree"]["saturation"])
    else:
        set_light_state(light_settings["hue"], light_settings["brightness"], light_settings["saturation"])

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

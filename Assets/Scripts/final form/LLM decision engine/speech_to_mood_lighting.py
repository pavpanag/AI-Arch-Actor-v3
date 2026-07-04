import os
import requests
import speech_recognition as sr
import openai

# OpenAI & Philips Hue Configuration
api_key = os.getenv("OPENAI_API_KEY1")  # OpenAI API key from environment
BRIDGE_IP = "192.168.1.106"
USER_API = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR"
LIGHT_ID = 5  # Controlling Light 5

# Predefined light settings for different moods
MOOD_TO_LIGHT = {
    "Calm": {"hue": 46920, "brightness": 150, "saturation": 200},  # Blue
    "Intense": {"hue": 0, "brightness": 254, "saturation": 254},  # Red
    "Reflective": {"hue": 56100, "brightness": 180, "saturation": 220},  # Purple
    "Energetic": {"hue": 25500, "brightness": 254, "saturation": 200},  # Yellow
    "Eerie": {"hue": 10000, "brightness": 100, "saturation": 254},  # Orange
    "Tense": {"hue": 6500, "brightness": 220, "saturation": 250},  # White cold
    "Mysterious": {"hue": 30000, "brightness": 120, "saturation": 220},  # Greenish
    "Dramatic": {"hue": 0, "brightness": 180, "saturation": 254},  # Deep Red
    "Neutral": {"hue": 34600, "brightness": 200, "saturation": 100},  # Warm white
}

def transcribe_speech(device_index=1):
    """Captures audio and converts it to text."""
    recognizer = sr.Recognizer()
    with sr.Microphone(device_index=device_index) as source:
        print("\nSpeak now... (Listening)")
        recognizer.adjust_for_ambient_noise(source)
        audio = recognizer.listen(source)
    
    try:
        text = recognizer.recognize_google(audio, language="en-US")  # English language
        return text  # Return text only
    except sr.UnknownValueError:
        return None
    except sr.RequestError:
        return None

def analyze_speech(text):
    """Analyzes speech and determines sentiment, emotions, atmosphere, and context."""
    client = openai.OpenAI(api_key=api_key)
    prompt = f"""
    Analyze the following spoken statement and categorize it into:
    
    1. **Sentiment**: (Positive, Negative, Neutral)
    2. **Emotion**: (Happiness, Sadness, Anger, Surprise, Fear, Trust, Anticipation, Disgust, Neutral)
    3. **Atmosphere**: (Calm, Intense, Reflective, Energetic, Eerie, Tense, Mysterious, Dramatic, Neutral)
    4. **Other Relevant Context**: (e.g., Urgency, Conflict, Excitement, Doubt, Playfulness, None if not applicable)
    
    **Example Output (JSON format):**
    {{
        "sentiment": "Positive",
        "emotion": "Happiness",
        "atmosphere": "Calm",
        "context": "Playfulness"
    }}

    **Statement:** "{text}"
    
    Respond in JSON format only.
    """

    response = client.chat.completions.create(
        model="gpt-4",
        messages=[{"role": "system", "content": prompt}]
    )

    return response.choices[0].message.content.strip()

def set_light_state(hue, brightness, saturation):
    """Sends lighting changes to the Philips Hue Bridge."""
    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{LIGHT_ID}/state"
    payload = {"on": True, "hue": hue, "bri": brightness, "sat": saturation}
    response = requests.put(url, json=payload)

    if response.status_code == 200:
        print(f"Light set - Hue: {hue}, Brightness: {brightness}, Saturation: {saturation}")
    else:
        print("Failed to update light!")

def process_mood_and_change_lighting(atmosphere):
    """Finds the correct light setting for the detected atmosphere and updates the bulb."""
    mood_settings = MOOD_TO_LIGHT.get(atmosphere, MOOD_TO_LIGHT["Neutral"])  # Default to Neutral
    set_light_state(**mood_settings)

# Run Real-Time Speech Transcription and Mood-Based Lighting
while True:
    speech_text = transcribe_speech(device_index=1)  # Default to microphone index 1
    
    if speech_text and speech_text.lower() == "exit":
        break
    
    if speech_text:
        analysis = analyze_speech(speech_text)
        print(analysis)  # Display only the structured analysis
        
        # Parse JSON response
        try:
            import json
            mood_data = json.loads(analysis)
            detected_atmosphere = mood_data.get("atmosphere", "Neutral")  # Default to Neutral if missing
            process_mood_and_change_lighting(detected_atmosphere)
        except json.JSONDecodeError:
            print("Error parsing mood response. Using default lighting.")
            process_mood_and_change_lighting("Neutral")

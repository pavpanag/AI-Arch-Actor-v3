import os
import speech_recognition as sr
import openai

# Load API key securely
api_key = os.getenv("OPENAI_API_KEY") or os.getenv("OPENAI_API_KEY1")

def transcribe_speech(device_index=1):  # Default mic index set to 1
    """Captures audio from the microphone and converts it to text."""
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
    """Analyzes speech and determines sentiment, emotions, atmosphere, and other contextual elements."""
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

# Run Real-Time Speech Transcription and Analysis
while True:
    speech_text = transcribe_speech(device_index=1)  # Default to microphone index 1
    
    if speech_text and speech_text.lower() == "exit":
        break
    
    if speech_text:
        analysis = analyze_speech(speech_text)
        print(analysis)  # Display only the structured analysis

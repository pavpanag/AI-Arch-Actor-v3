import openai
import os
import speech_recognition as sr
from pythonosc import udp_client

# Load API key securely
api_key = os.getenv("OPENAI_API_KEY") or os.getenv("OPENAI_API_KEY1")

# OSC client setup
osc_client = udp_client.SimpleUDPClient("127.0.0.1", 8000)

def transcribe_speech():
    """Captures audio from the microphone and converts it to text."""
    recognizer = sr.Recognizer()
    with sr.Microphone() as source:
        print("\n🎙️ Speak now... (Listening)")
        recognizer.adjust_for_ambient_noise(source)
        audio = recognizer.listen(source)

    try:
        text = recognizer.recognize_google(audio, language="el-GR")  # Greek language support
        print("📝 Transcribed:", text)
        return text
    except sr.UnknownValueError:
        print("❌ Could not understand audio.")
        return None
    except sr.RequestError:
        print("❌ Speech recognition service error.")
        return None

def analyze_sentiment(text):
    """Analyzes the sentiment (positive, negative, neutral) and emotional tone of the Greek text."""
    client = openai.OpenAI(api_key=api_key)
    response = client.chat.completions.create(
        model="gpt-3.5-turbo",  # Switch to gpt-4-turbo if needed
        messages=[
            {"role": "system", "content": "Analyze the sentiment (positive, negative, neutral) and emotional tone of the following Greek sentence."},
            {"role": "user", "content": text},
        ]
    )
    return response.choices[0].message.content  # Extract sentiment analysis result

def send_osc_messages(text, sentiment):
    """Sends OSC messages with the recognized words, the whole phrase, the sentiment, and the emotion."""
    words = text.split()
    for word in words:
        osc_client.send_message("/word", word)
    osc_client.send_message("/phrase", text)
    osc_client.send_message("/sentiment", sentiment)
    # Assuming the sentiment analysis result contains both sentiment and emotion
    # You might need to parse the sentiment result to extract emotion if it's separate
    osc_client.send_message("/emotion", sentiment)  # Adjust if emotion is separate

# 🔥 Run Real-Time Sentiment Analysis
while True:
    print("\n🎙️ Say something in Greek (or type 'exit' to quit)...")
    speech_text = transcribe_speech()
    
    if speech_text and speech_text.lower() == "exit":
        print("👋 Exiting...")
        break
    
    if speech_text:
        sentiment_result = analyze_sentiment(speech_text)
        print("🧠 Sentiment Analysis:", sentiment_result)
        send_osc_messages(speech_text, sentiment_result)
import openai

import os

import speech_recognition as sr

from pythonosc import udp_client



# Load API key securely

api_key = os.getenv("OPENAI_API_KEY1")



# OSC client setup

osc_client = udp_client.SimpleUDPClient("127.0.0.1", 3333)



# Defined emotions

VALID_EMOTIONS = {"happiness", "sadness", "anger", "fear", "surprise", "trust", "anticipation", "neutral", "disgust"}



def list_microphones():

    """Lists all available microphones with their indexes."""

    print("Available microphones:")

    for index, name in enumerate(sr.Microphone.list_microphone_names()):

        print(f"{index}: {name}")



def transcribe_speech(device_index=None):

    """Captures audio from the microphone and converts it to text."""

    recognizer = sr.Recognizer()

    try:

        with sr.Microphone(device_index=device_index) as source:

            print("\n🎙️ Speak now... (Listening)")

            recognizer.adjust_for_ambient_noise(source)

            audio = recognizer.listen(source)

    except AssertionError as e:

        print(f"❌ Microphone error: {e}")

        return None

    except Exception as e:

        print(f"❌ Unexpected error: {e}")

        return None



    try:

        text = recognizer.recognize_google(audio, language="en-US")  # English language support

        print("📝 Transcribed:", text)

        return text

    except sr.UnknownValueError:

        print("❌ Could not understand audio.")

        return None

    except sr.RequestError:

        print("❌ Speech recognition service error.")

        return None



def analyze_sentiment(text):

    """Analyzes the sentiment and assigns one of the predefined emotions."""

    client = openai.OpenAI(api_key=api_key)

    try:

        response = client.chat.completions.create(

            model="gpt-3.5-turbo",  # Switch to gpt-4-turbo if needed

            messages=[

                {"role": "system", "content": "Analyze the sentiment of the following English sentence and return only one word representing an emotion from this list: happiness, sadness, anger, fear, surprise, trust, anticipation, neutral, disgust. Ensure it is lowercase and consistent."},

                {"role": "user", "content": text},

            ]

        )

        result = response.choices[0].message.content.strip().lower()

        # Ensure the emotion is one of the predefined ones

        if result not in VALID_EMOTIONS:

            result = "neutral"  # Default to neutral if unexpected output

        return result

    except Exception as e:

        print(f"❌ Error analyzing sentiment: {e}")

        return "neutral"



def send_osc_messages(text, emotion):

    """Sends OSC messages with the recognized words, phrase, and assigned emotion."""

    words = text.split()

    for word in words:

        osc_client.send_message("/word", word)

        print(f"OSC Message Sent: /word {word}")

    

    osc_client.send_message("/phrase", text)

    print(f"OSC Message Sent: /phrase {text}")

    

    osc_client.send_message("/emotion", emotion)

    print(f"OSC Message Sent: /emotion {emotion}")



# Main script execution

if __name__ == "__main__":

    # List available microphones

    list_microphones()



    # Prompt user to select a microphone

    try:

        device_index = int(input("Select the microphone index (or press Enter to use default): ") or -1)

        if device_index == -1:

            device_index = None  # Use default microphone

    except ValueError:

        print("Invalid input. Using default microphone.")

        device_index = None



    # 🔥 Run Real-Time Emotion Recognition

    while True:

        print("\n🎙️ Say something in English (or type 'exit' to quit)...")

        speech_text = transcribe_speech(device_index=device_index)

        

        if speech_text and speech_text.lower() == "exit":

            print("👋 Exiting...")

            break

        

        if speech_text:

            detected_emotion = analyze_sentiment(speech_text)

            print("🧠 Detected Emotion:", detected_emotion)

            send_osc_messages(speech_text, detected_emotion)
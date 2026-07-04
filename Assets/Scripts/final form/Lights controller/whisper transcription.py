import whisper
from docx import Document
import torch

# Path to your audio file
audio_file = r"C:\Users\pavpa\Desktop\to be transcribed\focus group Ws5.m4a"

# Define the output file name
output_file_name = "transcription focus ws 6.docx"

# Check if GPU is available
device = "cuda" if torch.cuda.is_available() else "cpu"
print(f"Using device: {device}")

# Load the Whisper model on the specified device
model = whisper.load_model("large", device=device)


# Transcribe the audio file in Greek
result = model.transcribe(audio_file, language="el")

# Print the transcription
print("Transcription:")
print(result["text"])

# Save the transcription to a Word document
doc = Document()
doc.add_heading("Transcription", level=1)
doc.add_paragraph(result["text"])
doc.save(output_file_name)

print(f"Transcription saved to '{output_file_name}'")
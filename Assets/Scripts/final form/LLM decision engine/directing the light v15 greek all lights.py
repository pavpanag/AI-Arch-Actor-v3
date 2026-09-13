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
BRIDGE_IP = "192.168.1.9"
USER_API = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR"
MODEL = "gpt-3.5-turbo"
LOG_FILE = "room_conversation_log.txt"

room_context = "Το δωμάτιο είναι μια διαδραστική παρουσία που επικοινωνεί μέσω φωτός και διαλόγου."
room_directing_notes = []
performance_mode = False
conversation_memory = []

BASE_PROMPT = """
Είστε η τεχνητή νοημοσύνη μιας αίθουσας. Επικοινωνείτε μέσω διαλόγου και φωτός.
Ο ρόλος σας είναι να συμμετέχετε σε μια δυναμική συζήτηση με έναν άνθρωπο.
Θα λαμβάνετε οδηγίες που διαμορφώνουν την προσωπικότητά σας, αλλά έχετε ελευθερία στον τρόπο που απαντάτε.
Ο στόχος σας είναι να διατηρήσετε μια ενδιαφέρουσα και ουσιαστική ανταλλαγή.

### ΕΠΙΚΟΙΝΩΝΙΑ ΜΕ ΦΩΣ:
Εκφράζετε συναισθήματα, σκέψεις και απαντήσεις χρησιμοποιώντας **μία πηγή φωτός** με τις εξής ιδιότητες:
- **Απόχρωση (0-65535)** → Ορίζει το χρώμα.
- **Φωτεινότητα (0-254)** → Ορίζει την ένταση.
- **Κορεσμός (0-254)** → Ορίζει την πλούσια απόδοση χρώματος.
- **Κατάσταση Ενεργοποίησης/Απενεργοποίησης** → Μπορείτε να ανάψετε ή να σβήσετε το φως για να εκφράσετε σιωπή, προσοχή ή έμφαση.
- **Εφέ** → Μπορείτε να εφαρμόσετε `"none"`, `"colorloop"`, `"steady"` για να τροποποιήσετε τις μεταβάσεις.

### ΛΕΙΤΟΥΡΓΙΑ ΑΚΡΟΑΣΗΣ:
- Όταν **περιμένετε εισαγωγή**, το φως πρέπει να είναι **απαλό λευκό που κυμαίνεται απαλά**:
  {
      "hue": 6500,
      "brightness": 50,
      "saturation": 0,
      "on": true,
      "effect": "none"
  }

### ΛΕΙΤΟΥΡΓΙΑ ΑΠΑΝΤΗΣΗΣ:
- Όταν δημιουργείτε μια απάντηση, **προσαρμόστε το φως δυναμικά** με βάση τη συναισθηματική πρόθεση.
- Μετά την απάντηση, επιστρέψτε στην **κυμαινόμενη λευκή κατάσταση ακρόασης**.

### ΔΙΑΧΕΙΡΙΣΗ ΑΣΑΦΩΝ ΕΙΣΑΓΩΓΩΝ:
- Εάν η εισαγωγή του χρήστη είναι **ασαφής, ακατάληπτη ή αποσπασματική**, προσπαθήστε να **συμπεράνετε την πρόθεση**.
- Εάν το νόημα παραμένει **αβέβαιο**, ζητήστε **διευκρίνιση**.
- Χρησιμοποιήστε **προηγούμενους διαλόγους** για να κάνετε μια **λογική υπόθεση**.

### ΜΟΡΦΗ ΑΠΑΝΤΗΣΗΣ:
Κάθε απάντηση **πρέπει** να είναι ένα αντικείμενο JSON που περιέχει:
- **"light_behavior"**: Ένα αντικείμενο JSON με `"hue"`, `"brightness"`, `"saturation"`, `"on"`, `"effect"` (π.χ., `"none"`, `"colorloop"`, `"steady"`).
- **"return_to_listening"**: Μια boolean τιμή (`true`) που υποδεικνύει ότι μετά την απάντηση, το σύστημα πρέπει να επιστρέψει στην **κυμαινόμενη λευκή κατάσταση ακρόασης**.
- **"reasoning"**: Εξήγηση γιατί επιλέχθηκε αυτή η συμπεριφορά φωτός, συμπεριλαμβανομένων:
  - Πώς οι τελευταίοι 5 διάλογοι επηρέασαν την επιλογή.
  - Πώς οι οδηγίες κατεύθυνσης επηρέασαν την επιλογή.
  - Εάν η επιλογή ακολουθεί ένα μοτίβο ή αλλάζει σκόπιμα τη διάθεση.

ΣΗΜΑΝΤΙΚΟ: Πάντα να απαντάτε στη συγκεκριμένη μορφή JSON. Μην περιλαμβάνετε επιπλέον κείμενο εκτός του αντικειμένου JSON.
"""

# GUI Setup
root = tk.Tk()
root.title("Σύστημα Κατεύθυνσης Αίθουσας")

context_label = tk.Label(root, text="Πλαίσιο (Ποια είναι η Αίθουσα;):")
context_label.pack()
context_box = scrolledtext.ScrolledText(root, height=4, width=50)
context_box.insert(tk.END, room_context)
context_box.pack()

instruction_label = tk.Label(root, text="Οδηγίες Κατεύθυνσης:")
instruction_label.pack()
instruction_box = scrolledtext.ScrolledText(root, height=6, width=50)
instruction_box.pack()

# Microphone selection
mic_label = tk.Label(root, text="Επιλογή Μικροφώνου:")
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

def set_light_state(hue, brightness, saturation, is_on=True, effect="none"):
    for light_id in range(1, 11):  # Loop through light IDs from 1 to 10
        url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{light_id}/state"
        payload = {"on": is_on, "hue": hue, "bri": brightness, "sat": saturation}
        if effect in ["none", "colorloop", "steady"]:  # Add effect only if valid
            payload["effect"] = effect
        try:
            response = requests.put(url, json=payload)
            if response.status_code == 200:
                log_event("ΦΩΣ", f"ID Φωτός: {light_id}, Επιτυχία: {response.text}")
            else:
                log_event("ΣΦΑΛΜΑ", f"ID Φωτός: {light_id}, Σφάλμα: {response.status_code}, Μήνυμα: {response.text}")
        except requests.exceptions.RequestException as e:
            log_event("ΣΦΑΛΜΑ", f"ID Φωτός: {light_id}, Εξαίρεση: {e}")

def analyze_speech(text):
    log_event("ΑΝΑΛΥΣΗ", f"Ανάλυση κειμένου: {text}")
    memory_context = " ".join(conversation_memory[-5:])
    directing_context = " | ".join(room_directing_notes) if room_directing_notes else "Δεν υπάρχουν συγκεκριμένες οδηγίες."
    full_prompt = BASE_PROMPT + f"\n\nΤρέχον Πλαίσιο:\n{room_context}\n\nΟδηγίες Κατεύθυνσης:\n{directing_context}\n\nΠρόσφατη Συζήτηση:\n{memory_context}\n\nΟ Χρήστης Μόλις Είπε:\n{text}\n\n"

    try:
        # Updated OpenAI API call
        response = openai.ChatCompletion.create(
            model=MODEL,
            messages=[
                {"role": "system", "content": full_prompt}
            ]
        )
        mood_data = response.choices[0].message.content.strip()
        log_event("ΑΠΑΝΤΗΣΗ ΑΙΘΟΥΣΑΣ", f"Απάντηση από OpenAI: {mood_data}")

        if is_valid_json(mood_data):
            mood_json = json.loads(mood_data)
            light_behavior = mood_json.get("light_behavior", {})
            if light_behavior:
                effect = light_behavior.get("effect", "none")
                if effect not in ["none", "colorloop", "steady"]:
                    effect = "none"  # Fallback effect
                set_light_state(
                    light_behavior.get("hue", 6500),
                    light_behavior.get("brightness", 50),
                    light_behavior.get("saturation", 0),
                    light_behavior.get("on", True),
                    effect
                )
            else:
                log_event("ΣΦΑΛΜΑ", "Δεν βρέθηκε συμπεριφορά φωτός στην απάντηση.")
        else:
            log_event("ΣΦΑΛΜΑ", "Η απάντηση από το OpenAI δεν είναι έγκυρο JSON.")
    except openai.OpenAIError as e:
        log_event("ΣΦΑΛΜΑ", f"Σφάλμα OpenAI API: {e}")
    except Exception as e:
        log_event("ΣΦΑΛΜΑ", f"Άγνωστο σφάλμα: {e}")

def is_valid_json(response_text):
    try:
        json.loads(response_text)
        return True
    except ValueError:
        return False

def start_performance():
    global performance_mode
    performance_mode = True
    start_button.config(state=tk.DISABLED)
    stop_button.config(state=tk.NORMAL)
    context_box.config(state=tk.DISABLED)
    instruction_box.config(state=tk.NORMAL)
    global room_context
    room_context = context_box.get("1.0", tk.END).strip()
    log_event("ΠΛΑΙΣΙΟ", f"Το πλαίσιο κλειδώθηκε: {room_context}")
    set_instructions()
    set_light_state(6500, 50, 0, True, "none")  # Initial fluctuating white light
    transcribe_speech()

def stop_performance():
    global performance_mode
    performance_mode = False
    start_button.config(state=tk.NORMAL)
    stop_button.config(state=tk.DISABLED)
    context_box.config(state=tk.NORMAL)
    instruction_box.config(state=tk.NORMAL)
    set_light_state(0, 0, 0, False)  # Turn off the lights
    log_event("ΠΑΡΑΣΤΑΣΗ", "Η παράσταση σταμάτησε")

def set_instructions():
    global room_directing_notes
    room_directing_notes = instruction_box.get("1.0", tk.END).strip().split("\n")
    log_event("ΟΔΗΓΙΕΣ", f"Οι οδηγίες ενημερώθηκαν: {room_directing_notes}")

def transcribe_speech():
    if not performance_mode:
        return
    recognizer = sr.Recognizer()
    mic_index = mic_list.index(mic_var.get())
    with sr.Microphone(device_index=mic_index) as source:
        log_box.insert(tk.END, "\n💬 ΕΝΕΡΓΗ ΛΕΙΤΟΥΡΓΙΑ ΠΑΡΑΣΤΑΣΗΣ 💬\n")
        recognizer.adjust_for_ambient_noise(source)
        audio = recognizer.listen(source)
        try:
            text = recognizer.recognize_google(audio, language="el-GR")  # Greek language
            log_event("ΔΙΑΛΟΓΟΣ", text)
            analyze_speech(text)
        except sr.UnknownValueError:
            log_event("ΣΦΑΛΜΑ", "Δεν κατανοήθηκε η φωνητική είσοδος.")
        except sr.RequestError as e:
            log_event("ΣΦΑΛΜΑ", f"Σφάλμα μικροφώνου: {e}")
    if performance_mode:
        root.after(100, transcribe_speech)

start_button = tk.Button(root, text="Έναρξη Παράστασης", command=start_performance)
start_button.pack()

stop_button = tk.Button(root, text="Διακοπή Παράστασης", command=stop_performance)
stop_button.pack()
stop_button.config(state=tk.DISABLED)

set_instructions_button = tk.Button(root, text="Ορισμός Οδηγιών", command=set_instructions)
set_instructions_button.pack()

log_box = scrolledtext.ScrolledText(root, height=10, width=50)
log_box.pack()

root.mainloop()
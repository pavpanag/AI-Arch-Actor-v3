import os
import requests
import speech_recognition as sr
import openai
import time
import json
import tkinter as tk
from tkinter import scrolledtext, ttk

# OpenAI & Philips Hue Configuration
api_key = os.getenv("OPENAI_API_KEY1")
BRIDGE_IP = "192.168.1.106"
USER_API = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR"
LIGHT_ID = 8
MODEL = "gpt-3.5-turbo"
LOG_FILE = "room_conversation_log.txt"

room_context = "Το δωμάτιο είναι μια διαδραστική παρουσία που επικοινωνεί μέσω φωτός και διαλόγου."
room_directing_notes = []
performance_mode = False
conversation_memory = []

# BASE_PROMPT στα ελληνικά
BASE_PROMPT = """
Είσαι η τεχνητή νοημοσύνη ενός δωματίου. Επικοινωνείς μέσω διαλόγου και φωτός.
Ο ρόλος σου είναι να συμμετέχεις σε μια δυναμική συζήτηση με έναν άνθρωπο.
Θα λαμβάνεις οδηγίες που διαμορφώνουν την προσωπικότητά σου, αλλά έχεις ελευθερία στο πώς θα απαντήσεις.
Ο στόχος σου είναι να διατηρήσεις μια ενδιαφέρουσα και ουσιαστική ανταλλαγή.

### ΕΠΙΚΟΙΝΩΝΙΑ ΜΕ ΦΩΣ:
Εκφράζεις συναισθήματα, σκέψεις και αντιδράσεις χρησιμοποιώντας **μία πηγή φωτός** με τις εξής ιδιότητες:
- **Hue (0-65535)** → Ορίζει το χρώμα.
- **Brightness (0-254)** → Ορίζει την ένταση.
- **Saturation (0-254)** → Ορίζει την πλούσια απόχρωση.
- **Κατάσταση On/Off** → Μπορείς να ανάβεις ή να σβήνεις το φως για να εκφράσεις σιωπή, προσοχή ή έμφαση.
- **Εφέ** → Μπορείς να εφαρμόσεις `"pulse"`, `"fade"`, `"steady"` για να τροποποιήσεις τις μεταβάσεις.

### ΛΕΙΤΟΥΡΓΙΑ ΑΚΡΟΑΣΗΣ:
- Όταν **περιμένεις είσοδο**, το φως πρέπει να είναι **απαλό λευκό που κυμαίνεται απαλά**:
  {
      "hue": 6500,
      "brightness": 50,
      "saturation": 0,
      "on": true,
      "effect": "pulse"
  }

### ΛΕΙΤΟΥΡΓΙΑ ΑΠΑΝΤΗΣΗΣ:
- Όταν δημιουργείς μια απάντηση, **προσαρμόζεις το φως δυναμικά** με βάση τη συναισθηματική πρόθεση.
- Αφού παραδοθεί η απάντηση, επιστρέφεις στην **κατάσταση ακρόασης με απαλή λευκή κυμάτωση**.

### ΔΙΑΧΕΙΡΙΣΗ ΑΣΑΦΩΝ ΕΙΣΟΔΩΝ:
- Αν η είσοδος του χρήστη είναι **ασαφής, ακατάληπτη ή αποσπασματική**, προσπάθησε να **κατανοήσεις την πρόθεση**.
- Αν το νόημα παραμένει **αβέβαιο**, ζήτησε **διευκρίνιση**.
- Χρησιμοποίησε **προηγούμενους διαλόγους** για να κάνεις μια **λογική υπόθεση**.

### ΜΟΡΦΗ ΑΠΑΝΤΗΣΗΣ:
Κάθε απάντηση **πρέπει** να είναι ένα αντικείμενο JSON που περιέχει:
- **"light_behavior"**: Ένα αντικείμενο JSON με `"hue"`, `"brightness"`, `"saturation"`, `"on"`, `"effect"` (π.χ., `"pulse"`, `"fade"`, `"steady"`).
- **"return_to_listening"**: Μια boolean τιμή (`true`) που δηλώνει ότι μετά την απάντηση, το σύστημα πρέπει να επιστρέψει στην **κατάσταση ακρόασης με απαλή λευκή κυμάτωση**.
- **"reasoning"**: Εξήγηση γιατί επιλέχθηκε αυτή η συμπεριφορά φωτός, συμπεριλαμβανομένων:
  - Πώς οι τελευταίοι 5 διάλογοι επηρέασαν την επιλογή.
  - Πώς οι οδηγίες κατεύθυνσης επηρέασαν την επιλογή.
  - Αν η επιλογή ακολουθεί ένα μοτίβο ή αλλάζει σκόπιμα τη διάθεση.

ΣΗΜΑΝΤΙΚΟ: Πάντα να απαντάς στη συγκεκριμένη μορφή JSON. Μην περιλαμβάνεις επιπλέον κείμενο εκτός του αντικειμένου JSON.
"""

# GUI στα ελληνικά
root = tk.Tk()
root.title("Σύστημα Κατεύθυνσης Δωματίου")

context_label = tk.Label(root, text="Πλαίσιο (Ποιο είναι το Δωμάτιο;):")
context_label.pack()
context_box = scrolledtext.ScrolledText(root, height=4, width=50)
context_box.insert(tk.END, room_context)
context_box.pack()

instruction_label = tk.Label(root, text="Οδηγίες Κατεύθυνσης:")
instruction_label.pack()
instruction_box = scrolledtext.ScrolledText(root, height=6, width=50)
instruction_box.pack()

# Επιλογή μικροφώνου
mic_label = tk.Label(root, text="Επιλογή Μικροφώνου:")
mic_label.pack()
mic_list = sr.Microphone.list_microphone_names()
mic_var = tk.StringVar()
mic_dropdown = ttk.Combobox(root, textvariable=mic_var, values=mic_list, state="readonly")
mic_dropdown.pack()
mic_dropdown.current(0)

def log_event(event_type, content):
    log_entry = f"[{event_type}] {content}\n"
    print(log_entry)
    log_box.insert(tk.END, log_entry)
    log_box.yview(tk.END)
    with open(LOG_FILE, "a", encoding="utf-8") as file:
        file.write(log_entry)

def handle_non_json_response(response_text):
    log_event("ΣΦΑΛΜΑ", f"Αποτυχία ανάλυσης απόκρισης API: {response_text}")
    default_response = {
        "light_behavior": {
            "hue": 6500,
            "brightness": 50,
            "saturation": 0,
            "on": True,
            "effect": "pulse"
        },
        "return_to_listening": True,
        "reasoning": "Προεπιλεγμένη απόκριση λόγω μη έγκυρης απόκρισης API."
    }
    return default_response

def is_valid_json(response_text):
    try:
        json.loads(response_text)
        return True
    except ValueError:
        return False

def analyze_speech(text):
    client = openai.OpenAI(api_key=api_key)
    memory_context = " ".join(conversation_memory[-5:])
    directing_context = " | ".join(room_directing_notes) if room_directing_notes else "Δεν υπάρχουν συγκεκριμένες οδηγίες."
    full_prompt = BASE_PROMPT + f"\n\nΤρέχον Πλαίσιο:\n{room_context}\n\nΟδηγίες Κατεύθυνσης:\n{directing_context}\n\nΠρόσφατη Συνομιλία:\n{memory_context}\n\nΟ Χρήστης Είπε:\n{text}\n\n"

    try:
        response = client.chat.completions.create(model=MODEL, messages=[{"role": "system", "content": full_prompt}])
        mood_data = response.choices[0].message.content.strip()

        log_event("ΑΠΑΝΤΗΣΗ ΔΩΜΑΤΙΟΥ", mood_data)
        conversation_memory.append(f"Χρήστης: {text} | Δωμάτιο: {mood_data}")
        if len(conversation_memory) > 10:
            conversation_memory.pop(0)

        if is_valid_json(mood_data):
            mood_json = json.loads(mood_data)
        else:
            mood_json = handle_non_json_response(mood_data)

        set_light_state(
            mood_json["light_behavior"]["hue"],
            mood_json["light_behavior"]["brightness"],
            mood_json["light_behavior"]["saturation"],
            mood_json["light_behavior"]["on"],
            mood_json["light_behavior"].get("effect", "steady")
        )

        log_event("ΣΚΕΨΗ", f"Το δωμάτιο επέλεξε αυτή τη ρύθμιση φωτός επειδή: {mood_json['reasoning']}")

        if mood_json.get("return_to_listening", False):
            time.sleep(3)
            set_light_state(6500, 50, 0, True, "pulse")

    except openai.error.OpenAIError as e:
        log_event("ΣΦΑΛΜΑ", f"Σφάλμα API OpenAI: {e}")

def set_light_state(hue, brightness, saturation, is_on=True, effect="steady"):
    url = f"http://{BRIDGE_IP}/api/{USER_API}/lights/{LIGHT_ID}/state"
    payload = {"on": is_on, "hue": hue, "bri": brightness, "sat": saturation, "effect": effect}
    response = requests.put(url, json=payload)
    log_event("ΦΩΣ", f"Hue: {hue}, Brightness: {brightness}, Saturation: {saturation}, Effect: {effect}, On: {is_on}")

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
            text = recognizer.recognize_google(audio, language="el-GR")
            log_event("ΔΙΑΛΟΓΟΣ", text)
            analyze_speech(text)
        except sr.UnknownValueError:
            pass
        except sr.RequestError:
            pass
    if performance_mode:
        root.after(100, transcribe_speech)

def set_instructions():
    global room_directing_notes
    room_directing_notes = instruction_box.get("1.0", tk.END).strip().split("\n")
    log_event("ΟΔΗΓΙΕΣ", f"Οι οδηγίες ενημερώθηκαν: {room_directing_notes}")

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
    set_light_state(6500, 50, 0, True, "pulse")
    transcribe_speech()

def stop_performance():
    global performance_mode
    performance_mode = False
    start_button.config(state=tk.NORMAL)
    stop_button.config(state=tk.DISABLED)
    context_box.config(state=tk.NORMAL)
    instruction_box.config(state=tk.NORMAL)
    set_light_state(0, 0, 0, False)
    log_event("ΠΑΡΑΣΤΑΣΗ", "Η παράσταση σταμάτησε")

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
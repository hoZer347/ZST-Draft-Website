import pandas as pd
import json

# Google Sheet ID (from the URL)
SHEET_ID = "148NrapQQCjn7gGvRBhU90gIKAky1h9WRoGuCvANqMdE"
SHEET_NAME = "Pokedex"  # Change this if the sheet name is different

# Construct the CSV export URL
CSV_URL = f"https://docs.google.com/spreadsheets/d/{SHEET_ID}/gviz/tq?tqx=out:csv&sheet={SHEET_NAME}"

# Read the CSV into a Pandas DataFrame
df = pd.read_csv(CSV_URL)

# Convert to a list of dictionaries (JSON format)
data = df.to_dict(orient="records")

# Save as a JSON file
with open("Pokedex.json", "w", encoding="utf-8") as f:
    json.dump(data, f, indent=4, ensure_ascii=False)

print("Pokedex successfully saved to Pokedex.json!")

import pandas as pd

SHEET_ID = "148NrapQQCjn7gGvRBhU90gIKAky1h9WRoGuCvANqMdE"
SHEET_NAME = "Pokedex"
CSV_URL = f"https://docs.google.com/spreadsheets/d/{SHEET_ID}/gviz/tq?tqx=out:csv&sheet={SHEET_NAME}"

df = pd.read_csv(CSV_URL)

# Convert NaN to empty string using `.map()` (Future-proof)
df = df.map(lambda x: "" if pd.isna(x) else x)

# Save as CSV
df.to_csv("Data/Pokedex.csv", index=False, encoding="utf-8")

print("Pokedex successfully saved to Pokedex.csv!")

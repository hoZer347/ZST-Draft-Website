import pandas as pd

SHEET_ID = "148NrapQQCjn7gGvRBhU90gIKAky1h9WRoGuCvANqMdE"

def load_table(table_name):
	csv_url = f"https://docs.google.com/spreadsheets/d/{ SHEET_ID }/gviz/tq?tqx=out:csv&sheet={ table_name }"

	df = pd.read_csv(csv_url)
	df = df.map(lambda x: "" if pd.isna(x) else x)
	df.to_csv(f"Data/{ table_name }.csv", index=False, encoding="utf-8")

	print(f"{ table_name } successfully saved to { table_name }.csv!")

load_table("Pokedex")
load_table("Draft")

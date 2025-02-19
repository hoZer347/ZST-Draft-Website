@echo off
SET VENV_DIR=.venv

:: Check if the virtual environment exists
IF NOT EXIST "%VENV_DIR%\Scripts\activate" (
    echo [INFO] Virtual environment not found. Creating one...
    python -m venv %VENV_DIR%
    if %errorlevel% neq 0 (
        echo [ERROR] Failed to create virtual environment.
        exit /b %errorlevel%
    )
)

:: Activate the virtual environment
call "%VENV_DIR%\Scripts\activate"

:: Upgrade pip
python -m pip install --upgrade pip

:: Install dependencies from Requirements.txt
if exist "Requirements.txt" (
    echo [INFO] Installing dependencies...
    python -m pip install -r "Requirements.txt"
) else (
    echo [WARNING] Requirements.txt not found! Skipping package installation.
)

:: Run the Python script
if exist "Scripts\import_data.py" (
    echo [INFO] Running import_pokedex.py...
    python "Scripts\import_data.py"
) else (
    echo [ERROR] import_data.py not found! Exiting.
)

echo [INFO] Setup completed.

@echo off
setlocal

:: Set build directory
set BUILD_DIR="..\Bindings"
if not exist "%BUILD_DIR%" mkdir "%BUILD_DIR%"

:: Temporary file to store function names
set TEMP_FILE=exported_functions.txt
del %TEMP_FILE% 2>nul

:: Find extern "C" functions using findstr and write them to a file
for %%f in (*.cpp) do (
    findstr /R /C:"extern \"C\" {.*" /C:"^[a-zA-Z_][a-zA-Z0-9_]* *(" "%%f" >> %TEMP_FILE%
)

:: Read function names and format them for EXPORTED_FUNCTIONS
set EXPORTED_FUNCTIONS=
for /f "tokens=1 delims=(" %%i in (%TEMP_FILE%) do (
    if not "%%i"=="extern" (
        if not defined EXPORTED_FUNCTIONS (
            set EXPORTED_FUNCTIONS="_%%i"
        ) else (
            set EXPORTED_FUNCTIONS=%EXPORTED_FUNCTIONS%,"_%%i"
        )
    )
)

:: Cleanup temporary file
del %TEMP_FILE%

:: Ensure correct format for EXPORTED_FUNCTIONS
if defined EXPORTED_FUNCTIONS (
    set EXPORTED_FUNCTIONS=-s EXPORTED_FUNCTIONS="[%EXPORTED_FUNCTIONS%]"
) else (
    set EXPORTED_FUNCTIONS=
)

:: Emscripten Compiler Settings
set EMCC_FLAGS=-O3 -s WASM=1 -s MODULARIZE=1 -s EXPORT_ES6=1 -s EXPORTED_RUNTIME_METHODS="['cwrap']"

:: Compile each .cpp file to .js and .wasm
for %%f in (*.cpp) do (
    echo Compiling %%f...
    emcc "%%f" -o "%BUILD_DIR%\%%~nf.js" %EMCC_FLAGS% %EXPORTED_FUNCTIONS%
)

echo Compilation complete. Check the "Build" directory for output.
pause

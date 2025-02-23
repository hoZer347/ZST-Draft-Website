@echo off
setlocal

:: Set build directory
set BUILD_DIR="..\Bindings"
if not exist %BUILD_DIR% mkdir %BUILD_DIR%

:: Emscripten Compiler Settings
set EMCC_FLAGS=-O3 -s WASM=1 -s MODULARIZE=1 -s EXPORT_ES6=1 -s EXPORTED_RUNTIME_METHODS="['cwrap', 'ccall']" -lembind

:: Compile each .cpp file to .js and .wasm
for %%f in (*.cpp) do (
    echo Compiling %%f...
    emcc "%%f" -o "%BUILD_DIR%\%%~nf.js" %EMCC_FLAGS%
)

echo Compilation complete. Check the "Bindings" directory for output.
pause

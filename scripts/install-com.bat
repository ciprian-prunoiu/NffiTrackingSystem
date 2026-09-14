@echo off
setlocal EnableDelayedExpansion

REM ============================================================
REM install-com.bat
REM Inregistreaza COM server-ul NFFI (typelib + comhost)
REM Se auto-eleva la admin.
REM ============================================================

REM ---- Verifica daca ruleaza ca admin ----
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Se ridica la admin...
    powershell -NoProfile -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

REM ---- Setari ----
set "SCRIPT_DIR=%~dp0"
set "SOLUTION_ROOT=%SCRIPT_DIR%.."
set "BIN_DIR=%SOLUTION_ROOT%\NffiTrackingSystem.ComServer\bin\Debug\net8.0-windows"
set "ASSEMBLY_DLL=%BIN_DIR%\NffiTrackingSystem.ComServer.dll"
set "TLB_FILE=%BIN_DIR%\NffiTrackingSystem.ComServer.tlb"
set "COMHOST_DLL=%BIN_DIR%\NffiTrackingSystem.ComServer.comhost.dll"
set "REGSVR32=%SystemRoot%\System32\regsvr32.exe"

echo.
echo =====================================================
echo  NFFI COM - Install
echo =====================================================
echo  Bin: %BIN_DIR%
echo.

REM ---- Verifica fisiere ----
if not exist "%ASSEMBLY_DLL%" (
    echo [X] Nu gasesc: %ASSEMBLY_DLL%
    echo     Ruleaza Build in Visual Studio mai intai.
    pause
    exit /b 1
)
if not exist "%COMHOST_DLL%" (
    echo [X] Nu gasesc: %COMHOST_DLL%
    echo     Ruleaza Build in Visual Studio mai intai.
    pause
    exit /b 1
)

REM ---- Gaseste dscom ----
set "DSCOM="
where dscom >nul 2>&1
if %errorlevel% equ 0 (
    for /f "delims=" %%i in ('where dscom') do set "DSCOM=%%i"
) else (
    if exist "%USERPROFILE%\.dotnet\tools\dscom.exe" (
        set "DSCOM=%USERPROFILE%\.dotnet\tools\dscom.exe"
    )
)

if "%DSCOM%"=="" (
    echo [!] dscom nu e gasit. Il instalez global...
    call dotnet tool install --global dscom
    if %errorlevel% neq 0 (
        echo [X] Instalarea dscom a esuat.
        pause
        exit /b 1
    )
    set "DSCOM=%USERPROFILE%\.dotnet\tools\dscom.exe"
)

echo [OK] dscom: %DSCOM%
echo.

REM ---- 1. Unregister vechi (daca exista) ----
echo [1/4] Dezinstalare veche...
if exist "%TLB_FILE%" (
    "%DSCOM%" tlbunregister "%TLB_FILE%"
)
if exist "%COMHOST_DLL%" (
    "%REGSVR32%" /s /u "%COMHOST_DLL%"
)
echo      Gata.
echo.

REM ---- 2. tlbexport ----
echo [2/4] Generez typelib...
"%DSCOM%" tlbexport "%ASSEMBLY_DLL%" --out "%TLB_FILE%"
if %errorlevel% neq 0 (
    echo [X] tlbexport a esuat.
    pause
    exit /b 1
)
if not exist "%TLB_FILE%" (
    echo [X] Typelib nu a fost generat.
    pause
    exit /b 1
)
echo      OK: %TLB_FILE%
echo.

REM ---- 3. tlbregister ----
echo [3/4] Inregistrez typelib...
"%DSCOM%" tlbregister "%TLB_FILE%"
if %errorlevel% neq 0 (
    echo [X] tlbregister a esuat.
    pause
    exit /b 1
)
echo      OK.
echo.

REM ---- 4. regsvr32 ----
echo [4/4] Inregistrez comhost...
"%REGSVR32%" /s "%COMHOST_DLL%"
if %errorlevel% neq 0 (
    echo [X] regsvr32 a esuat.
    pause
    exit /b 1
)
echo      OK.
echo.

echo =====================================================
echo  COM inregistrat cu succes.
echo  Testeaza cu: powershell -File scripts\run-demo.ps1
echo =====================================================
echo.
pause
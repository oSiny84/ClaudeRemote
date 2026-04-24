@echo off
REM ============================================================
REM ClaudeRemote Android - APK build script
REM
REM Usage:
REM   build-apk.bat            -> builds debug APK (installable, unsigned release is omitted)
REM   build-apk.bat release    -> builds release APK (requires signing config for install)
REM
REM Output is copied to the script directory as ClaudeRemote-<variant>.apk.
REM ============================================================

setlocal ENABLEDELAYEDEXPANSION

cd /d "%~dp0"

REM --- Auto-detect JAVA_HOME (Android Studio ships a JBR/JRE we can reuse) ---
if defined JAVA_HOME goto :java_done
call :try_java "%ProgramFiles%\Android\Android Studio\jbr"
if defined JAVA_HOME goto :java_done
call :try_java "%ProgramW6432%\Android\Android Studio\jbr"
if defined JAVA_HOME goto :java_done
call :try_java "%ProgramFiles%\Android\Android Studio\jre"
if defined JAVA_HOME goto :java_done
call :try_java "%LOCALAPPDATA%\Programs\Android Studio\jbr"
if defined JAVA_HOME goto :java_done
call :try_java "%LOCALAPPDATA%\Programs\Android Studio\jre"
if defined JAVA_HOME goto :java_done
call :try_java "%ProgramFiles%\Eclipse Adoptium\jdk-17"
if defined JAVA_HOME goto :java_done
call :try_java "%ProgramFiles%\Java\jdk-17"
if defined JAVA_HOME goto :java_done

echo [ERROR] JAVA_HOME is not set and no JDK was found in standard locations.
echo Install Android Studio, or set JAVA_HOME to a JDK 17 install manually.
exit /b 1

:java_done
echo [INFO] Using JAVA_HOME=%JAVA_HOME%
set "PATH=%JAVA_HOME%\bin;%PATH%"
goto :after_java

:try_java
if exist "%~1\bin\java.exe" set "JAVA_HOME=%~1"
exit /b 0

:after_java

set VARIANT=%~1
if "%VARIANT%"=="" set VARIANT=debug

if /I "%VARIANT%"=="debug" (
    set GRADLE_TASK=assembleDebug
    set APK_PATH=app\build\outputs\apk\debug\app-debug.apk
    set OUT_NAME=ClaudeRemote-debug.apk
) else if /I "%VARIANT%"=="release" (
    set GRADLE_TASK=assembleRelease
    set APK_PATH=app\build\outputs\apk\release\app-release.apk
    set OUT_NAME=ClaudeRemote-release.apk
) else (
    echo [ERROR] Unknown variant: %VARIANT%
    echo Usage: build-apk.bat [debug^|release]
    exit /b 1
)

echo.
echo === Building %VARIANT% APK ===
echo.

call "%~dp0gradlew.bat" %GRADLE_TASK%
if errorlevel 1 (
    echo.
    echo [ERROR] Gradle build failed.
    exit /b 1
)

if not exist "%APK_PATH%" (
    echo.
    echo [ERROR] APK not found at: %APK_PATH%
    exit /b 1
)

copy /Y "%APK_PATH%" "%OUT_NAME%" >nul
if errorlevel 1 (
    echo [ERROR] Failed to copy APK.
    exit /b 1
)

echo.
echo === Build complete ===
echo Output: %~dp0%OUT_NAME%
echo.

endlocal
exit /b 0

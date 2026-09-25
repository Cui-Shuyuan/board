@echo off
setlocal EnableExtensions

rem Build the native UaaL POC in the required order:
rem   1. Unity export to clients\unity\Builds\AndroidLibrary
rem   2. verify unityLibrary exists
rem   3. Gradle assembleDebug
rem   4. print APK path

set "SCRIPT_DIR=%~dp0"
set "ROOT=%SCRIPT_DIR%..\.."
set "UNITY_PROJECT=%ROOT%\clients\unity"
set "LIBRARY_DIR=%UNITY_PROJECT%\Builds\AndroidLibrary"
set "APK=%SCRIPT_DIR%app\build\outputs\apk\debug\app-debug.apk"

if not defined UNITY_EXE set "UNITY_EXE=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Unity.exe"
if not exist "%UNITY_EXE%" (
  echo ERROR: Unity editor not found: "%UNITY_EXE%"
  echo Set UNITY_EXE to the full path of Unity.exe.
  exit /b 1
)

if not defined UNITY_GRADLE_HOME set "UNITY_GRADLE_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\Tools\gradle"
if not defined JAVA_HOME set "JAVA_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK"

rem Keep Gradle's user home ASCII; this avoids Windows code-page issues when the
rem Windows account name contains non-ASCII characters.
if not defined GRADLE_USER_HOME set "GRADLE_USER_HOME=%~d0\gradle-home-boardai"

if not exist "%SCRIPT_DIR%local.properties" (
  echo sdk.dir=D:/Unity/Hub/Editor/6000.5.8f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK> "%SCRIPT_DIR%local.properties"
)

echo [1/4] Exporting Unity Android Library...
if not exist "%LIBRARY_DIR%" mkdir "%LIBRARY_DIR%"
"%UNITY_EXE%" -batchmode -quit -projectPath "%UNITY_PROJECT%" -executeMethod ExportAndroidLibrary.Export -logFile "%LIBRARY_DIR%\export.log"
if errorlevel 1 (
  echo ERROR: Unity export failed. See "%LIBRARY_DIR%\export.log".
  exit /b 1
)

echo [2/4] Checking unityLibrary...
if not exist "%LIBRARY_DIR%\unityLibrary\build.gradle" (
  echo ERROR: missing "%LIBRARY_DIR%\unityLibrary\build.gradle".
  exit /b 1
)

echo [3/4] Building APK...
call "%SCRIPT_DIR%gradlew.bat" assembleDebug
if errorlevel 1 (
  echo ERROR: Gradle assembleDebug failed.
  exit /b 1
)

echo [4/4] APK:
echo %APK%
exit /b 0

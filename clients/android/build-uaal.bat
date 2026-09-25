@echo off
setlocal EnableExtensions

rem ===========================================================================
rem Single machine-configuration block for the UaaL build.
rem
rem Override any of these before running the script, for example:
rem   set UNITY_EXE=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Unity.exe
rem   set ANDROID_HOME=D:\Android\Sdk
rem   set JAVA_HOME=C:\Java\jdk-17
rem   set UNITY_GRADLE_HOME=D:\Unity\...\Tools\gradle
rem   set GRADLE_USER_HOME=D:\gradle-home-boardai
rem ===========================================================================

set "SCRIPT_DIR=%~dp0"
set "ROOT=%SCRIPT_DIR%..\.."
set "UNITY_PROJECT=%ROOT%\clients\unity"
set "LIBRARY_DIR=%UNITY_PROJECT%\Builds\AndroidLibrary"
set "APK=%SCRIPT_DIR%app\build\outputs\apk\debug\app-debug.apk"

if not defined UNITY_ROOT set "UNITY_ROOT=D:\Unity\Hub\Editor\6000.5.8f1"

if not defined UNITY_EXE set "UNITY_EXE=%UNITY_ROOT%\Editor\Unity.exe"
if not defined JAVA_HOME set "JAVA_HOME=%UNITY_ROOT%\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK"
if not defined ANDROID_HOME set "ANDROID_HOME=%UNITY_ROOT%\Editor\Data\PlaybackEngines\AndroidPlayer\SDK"
if not defined UNITY_GRADLE_HOME set "UNITY_GRADLE_HOME=%UNITY_ROOT%\Editor\Data\PlaybackEngines\AndroidPlayer\Tools\gradle"

rem Keep Gradle's user home ASCII and short. This avoids Windows code-page
rem issues when the Windows account name contains non-ASCII characters.
if not defined GRADLE_USER_HOME set "GRADLE_USER_HOME=%~d0\gradle-home-boardai"

set "ANDROID_SDK_ROOT=%ANDROID_HOME%"

if not exist "%UNITY_EXE%" (
  echo ERROR: Unity editor not found: "%UNITY_EXE%"
  echo Set UNITY_EXE or UNITY_ROOT to the correct Unity installation.
  exit /b 1
)

if not exist "%JAVA_HOME%\bin\java.exe" (
  echo ERROR: JDK not found: "%JAVA_HOME%"
  echo Set JAVA_HOME to a JDK 17+ installation.
  exit /b 1
)

if not exist "%ANDROID_HOME%\platform-tools\adb.exe" (
  echo WARNING: Android SDK not found at "%ANDROID_HOME%".
  echo Continue only if local.properties already points at a valid SDK.
)

if not exist "%SCRIPT_DIR%local.properties" (
  set "SDK_FOR_PROPERTIES=%ANDROID_HOME:\=/%"
  > "%SCRIPT_DIR%local.properties" echo sdk.dir=%SDK_FOR_PROPERTIES%
)

rem Build order:
rem   1. Unity export to clients\unity\Builds\AndroidLibrary
rem   2. verify unityLibrary exists
rem   3. Gradle assembleDebug
rem   4. print APK path

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

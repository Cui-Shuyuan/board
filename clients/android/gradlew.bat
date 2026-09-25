@echo off
setlocal EnableExtensions

rem ---------------------------------------------------------------------------
rem BoardAI UaaL Gradle launcher
rem
rem Prefer the Gradle distribution embedded in the Unity Android player. This
rem keeps the POC reproducible without downloading a second Gradle distribution.
rem If no Unity Gradle installation is found, fall back to the standard wrapper.
rem ---------------------------------------------------------------------------

if not defined GRADLE_USER_HOME set "GRADLE_USER_HOME=%~d0\gradle-home-boardai"
set "APP_HOME=%~dp0"
set "WRAPPER_JAR=%APP_HOME%gradle\wrapper\gradle-wrapper.jar"

if not defined UNITY_GRADLE_HOME set "UNITY_GRADLE_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\Tools\gradle"
if exist "%UNITY_GRADLE_HOME%\lib\gradle-launcher-9.1.0.jar" goto embedded

goto wrapper

:embedded
if not defined JAVA_HOME set "JAVA_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK"
if exist "%JAVA_HOME%\bin\java.exe" (set "JAVA_EXE=%JAVA_HOME%\bin\java.exe") else (set "JAVA_EXE=java.exe")
"%JAVA_EXE%" -classpath "%UNITY_GRADLE_HOME%\lib\*;%UNITY_GRADLE_HOME%\lib\plugins\*" org.gradle.launcher.GradleMain %*
exit /b %ERRORLEVEL%

:wrapper
if not defined JAVA_HOME set "JAVA_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK"
if exist "%JAVA_HOME%\bin\java.exe" (set "JAVA_EXE=%JAVA_HOME%\bin\java.exe") else (set "JAVA_EXE=java.exe")
"%JAVA_EXE%" -classpath "%WRAPPER_JAR%" org.gradle.wrapper.GradleWrapperMain %*
exit /b %ERRORLEVEL%

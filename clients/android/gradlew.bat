@echo off
setlocal EnableExtensions

rem ---------------------------------------------------------------------------
rem BoardAI UaaL Gradle launcher
rem
rem Machine-specific paths are intentionally not hard-coded here. build-uaal.bat
rem sets them from its single configuration block (or they can be supplied by
rem the caller). If UNITY_GRADLE_HOME is not set, fall back to the standard
rem Gradle wrapper, which downloads the distribution declared in
rem gradle/wrapper/gradle-wrapper.properties.
rem ---------------------------------------------------------------------------

set "APP_HOME=%~dp0"
set "WRAPPER_JAR=%APP_HOME%gradle\wrapper\gradle-wrapper.jar"

if defined UNITY_GRADLE_HOME (
    if exist "%UNITY_GRADLE_HOME%\lib\gradle-launcher-9.1.0.jar" goto embedded
    dir /b "%UNITY_GRADLE_HOME%\lib\gradle-launcher-*.jar" >nul 2>nul && goto embedded
)

call :resolve_java
"%JAVA_EXE%" -classpath "%WRAPPER_JAR%" org.gradle.wrapper.GradleWrapperMain %*
exit /b %ERRORLEVEL%

:embedded
call :resolve_java
"%JAVA_EXE%" -classpath "%UNITY_GRADLE_HOME%\lib\*;%UNITY_GRADLE_HOME%\lib\plugins\*" org.gradle.launcher.GradleMain %*
exit /b %ERRORLEVEL%

:resolve_java
if defined JAVA_HOME (
    if exist "%JAVA_HOME%\bin\java.exe" (
        set "JAVA_EXE=%JAVA_HOME%\bin\java.exe"
        exit /b 0
    )
    if exist "%JAVA_HOME%\bin\java" (
        set "JAVA_EXE=%JAVA_HOME%\bin\java"
        exit /b 0
    )
)
set "JAVA_EXE=java.exe"
exit /b 0

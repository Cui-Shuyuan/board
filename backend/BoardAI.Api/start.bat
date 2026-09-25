@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..\..") do set "BOARD_BASE_PATH=%%~fI"

if not defined BOARD_API_URLS set "BOARD_API_URLS=http://0.0.0.0:5000"
if not defined DOTNET_EXE set "DOTNET_EXE=dotnet"
if not defined QDRANT_EXE set "QDRANT_EXE=qdrant.exe"
if not defined QDRANT_DATA_DIR set "QDRANT_DATA_DIR=%BOARD_BASE_PATH%\.qdrant-data"

echo ============================================
echo   BoardAI 启动
echo ============================================

rem 读取根目录 .env（已被 gitignore，不会提交）
if exist "%BOARD_BASE_PATH%\.env" for /F "usebackq tokens=1,* delims==" %%A in ("%BOARD_BASE_PATH%\.env") do set "%%A=%%B"

echo [1/2] 启动 Qdrant 向量数据库...
if not defined QDRANT__STORAGE__STORAGE_PATH set "QDRANT__STORAGE__STORAGE_PATH=%QDRANT_DATA_DIR%"
start "Qdrant" "%QDRANT_EXE%"
timeout /t 3 /nobreak >nul

echo [2/2] 启动 BoardAI.Api...
cd /d "%SCRIPT_DIR%"
rem Development 环境读取 appsettings.Development.json
set ASPNETCORE_ENVIRONMENT=Development
"%DOTNET_EXE%" run --urls "%BOARD_API_URLS%"

echo.
echo API 已退出: %BOARD_API_URLS%
echo.
echo 向量索引重建命令（改了规则文件后执行）:
echo   全部重建: curl -X POST %BOARD_API_URLS%/api/rules/admin/rebuild-all
echo   单个游戏: curl -X POST %BOARD_API_URLS%/api/rules/admin/rebuild-index/civolution

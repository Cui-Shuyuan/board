@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..\..") do set "BOARD_BASE_PATH=%%~fI"

rem Runtime-owned data (models, build output, Qdrant, cache, logs, manifests,
rem releases, secrets) lives outside the source checkout.
if not defined BOARD_RUNTIME_ROOT set "BOARD_RUNTIME_ROOT=D:\board"
if not defined BOARD_API_URLS set "BOARD_API_URLS=http://0.0.0.0:5000"
if not defined DOTNET_EXE set "DOTNET_EXE=dotnet"
if not defined QDRANT_EXE set "QDRANT_EXE=qdrant.exe"
if not defined QDRANT_DATA_DIR set "QDRANT_DATA_DIR=%BOARD_RUNTIME_ROOT%\qdrant\data"

rem Runtime paths are explicit so a direct `dotnet run` without environment
rem variables cannot accidentally write generated content back into Git.
set "Embedding__ModelDir=%BOARD_RUNTIME_ROOT%\models\bge-base-zh-v1.5-fp32"
set "Rules__DiskCache__Directory=%BOARD_RUNTIME_ROOT%\cache\rules-derived-cache"
set "Content__ManifestDirectory=%BOARD_RUNTIME_ROOT%\content\manifests"
set "Content__ReleaseDirectory=%BOARD_RUNTIME_ROOT%\content\releases"

echo ============================================
echo   BoardAI 启动
echo ============================================
echo Runtime root: %BOARD_RUNTIME_ROOT%
mkdir "%BOARD_RUNTIME_ROOT%\config" 2>nul
mkdir "%BOARD_RUNTIME_ROOT%\logs" 2>nul

rem 读取运行环境文件；优先 runtime/config/.env，兼容旧仓库根 .env。
if exist "%BOARD_RUNTIME_ROOT%\config\.env" (
  for /F "usebackq tokens=1,* delims==" %%A in ("%BOARD_RUNTIME_ROOT%\config\.env") do set "%%A=%%B"
) else if exist "%BOARD_BASE_PATH%\.env" (
  for /F "usebackq tokens=1,* delims==" %%A in ("%BOARD_BASE_PATH%\.env") do set "%%A=%%B"
)

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
API 已退出: %BOARD_API_URLS%
echo.
echo 向量索引重建命令（改了规则文件后执行）:
echo   全部重建: curl -X POST %BOARD_API_URLS%/api/rules/admin/rebuild-all
echo   单个游戏: curl -X POST %BOARD_API_URLS%/api/rules/admin/rebuild-index/civolution

@echo off
echo ============================================
echo   BoardAI 启动
echo ============================================

echo [1/2] 启动 Qdrant 向量数据库...
set QDRANT__STORAGE__STORAGE_PATH=D:\qdrant\data
start "Qdrant" D:\qdrant\qdrant.exe
timeout /t 3 /nobreak >nul

echo [2/2] 启动 BoardAI.Api...
cd /d D:\workspace\board\backend\BoardAI.Api
D:\dotnet\dotnet.exe run --urls "http://localhost:5000"
echo.
echo API 已就绪: http://localhost:5000
echo.
echo 向量索引重建命令（改了规则文件后执行）:
echo   全部重建: curl -X POST http://localhost:5000/api/rules/admin/rebuild-all
echo   单个游戏: curl -X POST http://localhost:5000/api/rules/admin/rebuild-index/civolution

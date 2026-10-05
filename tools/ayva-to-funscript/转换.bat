@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem 优先用 DSH 自带的 Node，找不到就用 PATH 里的 node
set NODE=%USERPROFILE%\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe
if not exist "%NODE%" set NODE=node

"%NODE%" convert.js %*
pause

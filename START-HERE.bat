@echo off
REM Panda Pocket: double-click this to start everything.
REM
REM You do not need Visual Studio, .NET, PostgreSQL or MongoDB installed.
REM Docker Desktop contains all of it. This script starts Docker if it is not
REM running, brings the system up, waits until it answers, and opens it.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0infra\start.ps1"

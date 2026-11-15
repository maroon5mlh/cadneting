@echo off
REM Build the AutoCAD plugin into build\<timestamp> and sync it into CadNesting.bundle
REM Usage: build.cmd ["D:\CAD2017\AutoCAD 2027"]
REM Without an argument, build.ps1 auto-detects the AutoCAD install path.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*

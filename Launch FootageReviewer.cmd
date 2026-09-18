@echo off
rem Launches the latest Release-published FootageReviewer.exe.
rem Built by `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`.
start "" "%~dp0dist\FootageReviewer.exe"

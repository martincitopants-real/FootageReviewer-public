@echo off
rem Re-publishes a fresh Release single-file exe to .\dist\FootageReviewer.exe.
rem Run this after changing code so the launcher picks up the change.
pushd "%~dp0src\FootageReviewer.App"
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0dist"
if exist "%~dp0dist\FootageReviewer.App.exe" (
    if exist "%~dp0dist\FootageReviewer.exe" del "%~dp0dist\FootageReviewer.exe"
    move /Y "%~dp0dist\FootageReviewer.App.exe" "%~dp0dist\FootageReviewer.exe" >nul
)
popd
echo.
echo Done. Launch with: "Launch FootageReviewer.cmd" in the project folder.
pause

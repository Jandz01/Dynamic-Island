@echo off
echo ===================================================
echo   Building Dynamic Island Standalone for Windows x64
echo ===================================================

echo [1/3] Cleaning previous publish folder...
if exist publish rmdir /s /q publish
if exist DynamicIsland-win-x64.zip del /f /q DynamicIsland-win-x64.zip

echo [2/3] Publishing self-contained executable...
dotnet publish DynamicIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Publish failed!
    pause
    exit /b %ERRORLEVEL%
)

echo [3/3] Compressing into DynamicIsland-win-x64.zip...
powershell -Command "Compress-Archive -Path publish\* -DestinationPath DynamicIsland-win-x64.zip -Force"

echo ===================================================
echo [SUCCESS] Done! File created: DynamicIsland-win-x64.zip
echo You can send this file to your friend or upload it to GitHub Releases.
echo ===================================================
pause

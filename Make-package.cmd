@echo off
dotnet build -c Release NTwain/NTwain.csproj
if errorlevel 1 exit /b %errorlevel%
dotnet pack -c Release --no-build /p:ContinuousIntegrationBuild=true -o ./build NTwain/NTwain.csproj

@echo off
rem Runs the C# compiler through dotnet.exe instead of the CET-marked csc.exe apphost,
rem which crashes on Windows builds without full CET support.
dotnet exec "C:\Program Files\dotnet\sdk\10.0.401\Roslyn\bincore\csc.dll" %*

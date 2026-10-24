@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cd /d "%~dp0.."
if not exist build mkdir build
cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /MD /I native native\tests\processor_test.cpp native\processor.cpp native\rnnoise_backend.cpp /Fo:build\ /Fe:build\processor_test.exe /link /INCREMENTAL:NO
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /MD %NR_DIAGNOSTIC_FLAGS% /LD /I native native\apo.cpp native\control.cpp native\processor.cpp native\rnnoise_backend.cpp /Fo:build\ /Fe:build\NoiseReduction.Apo.dll /link /DEF:native\apo.def /MANIFEST:NO /INCREMENTAL:NO ole32.lib uuid.lib shell32.lib advapi32.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /MD /I native native\tests\apo_test.cpp /Fo:build\ /Fe:build\apo_test.exe /link /INCREMENTAL:NO audiomediatypecrt.lib ole32.lib uuid.lib propsys.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /MD native\device_probe.cpp /Fo:build\ /Fe:build\device_probe.exe /link /INCREMENTAL:NO ole32.lib uuid.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /MD /I native native\tests\control_test.cpp native\control.cpp /Fo:build\ /Fe:build\control_test.exe /link /INCREMENTAL:NO ole32.lib uuid.lib shell32.lib advapi32.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /MD native\capture_probe.cpp /Fo:build\ /Fe:build\capture_probe.exe /link /INCREMENTAL:NO ole32.lib uuid.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /MD /I native native\tests\windows_control_test.cpp /Fo:build\ /Fe:build\windows_control_test.exe /link /INCREMENTAL:NO ole32.lib uuid.lib shell32.lib propsys.lib audiomediatypecrt.lib
exit /b %errorlevel%

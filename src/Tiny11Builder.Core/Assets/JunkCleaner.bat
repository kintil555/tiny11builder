@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Junk Files Cleaner

:: ---------- Cek Administrator ----------
set "ISADMIN=0"
fltmc >nul 2>&1 && set "ISADMIN=1"

:: ---------- Pengaturan ----------
set "AUTOCLOSE=1"
set "EXCL=%~dp0exclusions.txt"
set "CLEAN_X=%EXCL%"
set "HAS_EXCL=0"

:: ---------- Daftar item (adm = butuh Administrator) ----------
set COUNT=17
set "name1=Temp User (%%TEMP%%)"
set "name2=Temp Windows (C:\Windows\Temp) [Admin]"
set "name3=Prefetch Windows [Admin]"
set "name4=Cache Windows Update (SoftwareDistribution) [Admin]"
set "name5=NVIDIA ComputeCache (AppData\Roaming\NVIDIA\ComputeCache)"
set "name6=NVIDIA Local (AppData\Local\NVIDIA + NV_Cache)"
set "name7=DirectX Shader Cache (D3DSCache)"
set "name8=Thumbnail dan Icon Cache Explorer"
set "name9=Cache Google Chrome"
set "name10=Cache Microsoft Edge"
set "name11=Crash Dumps dan Windows Error Reporting"
set "name12=Recycle Bin"
set "name13=Cache Brave Browser"
set "name14=Cache Discord (AppData\Roaming\discord, login/settings aman)"
set "name15=Cache Legcord (AppData\Roaming\legcord, settings/tema/plugin aman)"
set "name16=Disk Cleanup Windows - cleanmgr /VERYLOWDISK, latar belakang [Admin]"
set "name17=Component Cleanup - DISM /ResetBase ala WinUtil, bisa lama [Admin]"

for /l %%i in (1,1,%COUNT%) do (
    set "sel%%i=0"
    set "adm%%i=0"
)
set "adm2=1"
set "adm3=1"
set "adm4=1"
set "adm16=1"
set "adm17=1"

:: Proses yang ditutup otomatis sebelum item dibersihkan
set "proc9=chrome.exe"
set "proc10=msedge.exe"
set "proc13=brave.exe"
set "proc14=Discord.exe"
set "proc15=Legcord.exe"

:menu
call :checkexcl
cls
echo ============================================================
echo                    JUNK FILES CLEANER
echo ============================================================
if "%ISADMIN%"=="1" (echo  Mode: Administrator) else (echo  Mode: User biasa - item [Admin] dilewati. Tekan E untuk elevasi.)
echo.
echo  Ketik nomor untuk centang / hapus centang:
echo.
for /l %%i in (1,1,%COUNT%) do (
    if "!sel%%i!"=="1" (set "mark=X") else (set "mark= ")
    set "num= %%i"
    echo    [!mark!] !num:~-2!. !name%%i!
)
echo.
echo    A = Pilih semua      N = Batalkan semua
echo    S = Mulai bersihkan  E = Jalankan sebagai Administrator
echo    K = Auto-tutup aplikasi ON/OFF   X = Edit exclusions
echo    Q = Keluar
echo.
if "%AUTOCLOSE%"=="1" (set "AC_TXT=ON ") else (set "AC_TXT=OFF")
if "%HAS_EXCL%"=="1" (set "EX_TXT=AKTIF") else (set "EX_TXT=kosong")
echo  Auto-tutup aplikasi: %AC_TXT%    Exclusion: %EX_TXT%
echo.
set "choice="
set /p "choice=Pilihan: "
if not defined choice goto menu
if /i "!choice!"=="Q" exit /b 0
if /i "!choice!"=="A" goto selall
if /i "!choice!"=="N" goto selnone
if /i "!choice!"=="S" goto start
if /i "!choice!"=="E" goto elevate
if /i "!choice!"=="K" goto toggleac
if /i "!choice!"=="X" goto editexcl

echo !choice!| findstr /r "^[0-9][0-9]*$" >nul || goto menu
set /a n=!choice! 2>nul
if %n% LSS 1 goto menu
if %n% GTR %COUNT% goto menu
if "!sel%n%!"=="1" (set "sel%n%=0") else (set "sel%n%=1")
goto menu

:selall
for /l %%i in (1,1,%COUNT%) do set "sel%%i=1"
goto menu

:selnone
for /l %%i in (1,1,%COUNT%) do set "sel%%i=0"
goto menu

:elevate
if "%ISADMIN%"=="1" goto menu
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b 0

:toggleac
if "%AUTOCLOSE%"=="1" (set "AUTOCLOSE=0") else (set "AUTOCLOSE=1")
goto menu

:editexcl
if not exist "%EXCL%" (
    (
        echo # DAFTAR PENGECUALIAN - satu path per baris ^(file atau folder^).
        echo # Item yang cocok tidak akan dihapus. Folder yang dikecualikan aman beserta isinya.
        echo # Baris diawali # atau ; diabaikan. Boleh pakai variabel seperti %%LOCALAPPDATA%%.
        echo # Contoh:
        echo # C:\Users\ASUS\AppData\Local\Temp\project-penting
        echo # %%LOCALAPPDATA%%\NVIDIA\DXCache\file-penting.bin
    ) > "%EXCL%"
)
start "" /wait notepad "%EXCL%"
goto menu

:start
set "ANY=0"
for /l %%i in (1,1,%COUNT%) do if "!sel%%i!"=="1" set "ANY=1"
if "%ANY%"=="0" (
    echo.
    echo  Belum ada item yang dipilih.
    pause
    goto menu
)
echo.
if "%HAS_EXCL%"=="1" (
    echo  Exclusion aktif ^(dilewati^):
    for /f "usebackq delims=" %%L in (`findstr /r /v /c:"^[ ]*$" /c:"^[ ]*[#;]" "%EXCL%"`) do echo      %%L
)
if "%AUTOCLOSE%"=="1" echo  Aplikasi terkait ^(Chrome/Edge/Brave/Discord/Legcord^) akan DITUTUP otomatis. Simpan dulu pekerjaanmu.
set "yn="
set /p "yn=Hapus file dari item terpilih? (Y/N): "
if /i not "!yn!"=="Y" goto menu

echo.
for /f %%A in ('powershell -NoProfile -Command "(Get-PSDrive -Name $env:SystemDrive.Substring(0,1)).Free"') do set "FREE_BEFORE=%%A"

for /l %%i in (1,1,%COUNT%) do if "!sel%%i!"=="1" call :runtask %%i

set "FREE_AFTER="
for /f %%A in ('powershell -NoProfile -Command "(Get-PSDrive -Name $env:SystemDrive.Substring(0,1)).Free"') do set "FREE_AFTER=%%A"
set "FREED=?"
if defined FREE_BEFORE if defined FREE_AFTER (
    for /f %%A in ('powershell -NoProfile -Command "[math]::Round((%FREE_AFTER%-%FREE_BEFORE%)/1GB,2)"') do set "FREED=%%A"
)
echo.
echo ============================================================
echo  Selesai. Ruang kosong bertambah sekitar: %FREED% GB
tasklist /fi "imagename eq cleanmgr.exe" 2>nul | find /i "cleanmgr.exe" >nul && echo  Catatan: Disk Cleanup masih bekerja di latar belakang, hasilnya belum terhitung.
echo ============================================================
pause
goto menu

:: ---------- Runner ----------
:runtask
set "idx=%~1"
if "!adm%idx%!"=="1" if "%ISADMIN%"=="0" (
    echo  [SKIP] !name%idx%! ^(butuh Administrator^)
    exit /b 0
)
echo  [....] !name%idx%!
if "%AUTOCLOSE%"=="1" if defined proc%idx% call :closeapp "!proc%idx%!"
call :task%idx%
echo  [ OK ] !name%idx%!
exit /b 0

:: ---------- Helper: kosongkan isi folder, folder induk tetap ada ----------
:cleandir
set "T=%~1"
if not exist "%T%\" exit /b 0
if "%HAS_EXCL%"=="1" goto cleandir_ps
del /f /s /q "%T%\*" >nul 2>&1
for /d %%D in ("%T%\*") do rd /s /q "%%D" >nul 2>&1
exit /b 0

:: Versi dengan exclusion (PowerShell): lewati path di exclusions.txt, folder induknya tetap dimasuki
:cleandir_ps
set "CLEAN_T=%T%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t=[IO.Path]::GetFullPath($env:CLEAN_T).TrimEnd('\'); $x=@(Get-Content -LiteralPath $env:CLEAN_X -ErrorAction SilentlyContinue | ForEach-Object { $_.Trim().Trim([char]34) } | Where-Object { $_ -and $_ -notmatch '^[#;]' } | ForEach-Object { [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($_)).TrimEnd('\') }); if ($x | Where-Object { $t -eq $_ -or $t.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) }) { Write-Host ('         [lewati seluruh folder] ' + $t); exit }; function Wipe($d) { foreach ($i in Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue) { $p = $i.FullName; if ($x -contains $p) { Write-Host ('         [lewati] ' + $p); continue }; if ($i.PSIsContainer -and ($x | Where-Object { $_.StartsWith($p + '\', [StringComparison]::OrdinalIgnoreCase) })) { Wipe $p } else { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue } } }; Wipe $t" 2>nul
exit /b 0

:checkexcl
set "HAS_EXCL=0"
if exist "%EXCL%" findstr /r /v /c:"^[ ]*$" /c:"^[ ]*[#;]" "%EXCL%" >nul 2>&1 && set "HAS_EXCL=1"
exit /b 0

:closeapp
set "P=%~1"
tasklist /fi "imagename eq %P%" 2>nul | find /i "%P%" >nul || exit /b 0
echo         menutup %P% ...
taskkill /im "%P%" /t >nul 2>&1
timeout /t 2 /nobreak >nul
taskkill /f /im "%P%" /t >nul 2>&1
exit /b 0

:: ---------- Task ----------
:task1
call :cleandir "%TEMP%"
exit /b 0

:task2
call :cleandir "%SystemRoot%\Temp"
exit /b 0

:task3
call :cleandir "%SystemRoot%\Prefetch"
exit /b 0

:task4
net stop wuauserv >nul 2>&1
net stop bits >nul 2>&1
call :cleandir "%SystemRoot%\SoftwareDistribution\Download"
net start bits >nul 2>&1
net start wuauserv >nul 2>&1
exit /b 0

:task5
call :cleandir "%APPDATA%\NVIDIA\ComputeCache"
exit /b 0

:task6
call :cleandir "%LOCALAPPDATA%\NVIDIA"
call :cleandir "%ProgramData%\NVIDIA Corporation\NV_Cache"
exit /b 0

:task7
call :cleandir "%LOCALAPPDATA%\D3DSCache"
exit /b 0

:task8
del /f /q "%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db" >nul 2>&1
del /f /q "%LOCALAPPDATA%\Microsoft\Windows\Explorer\iconcache_*.db" >nul 2>&1
exit /b 0

:task9
call :cleandir "%LOCALAPPDATA%\Google\Chrome\User Data\Default\Cache"
call :cleandir "%LOCALAPPDATA%\Google\Chrome\User Data\Default\Code Cache"
call :cleandir "%LOCALAPPDATA%\Google\Chrome\User Data\Default\GPUCache"
exit /b 0

:task10
call :cleandir "%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Cache"
call :cleandir "%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Code Cache"
call :cleandir "%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\GPUCache"
exit /b 0

:task11
call :cleandir "%LOCALAPPDATA%\CrashDumps"
call :cleandir "%LOCALAPPDATA%\Microsoft\Windows\WER"
call :cleandir "%ProgramData%\Microsoft\Windows\WER\ReportQueue"
call :cleandir "%ProgramData%\Microsoft\Windows\WER\ReportArchive"
exit /b 0

:task12
powershell -NoProfile -Command "Clear-RecycleBin -Force -ErrorAction SilentlyContinue" >nul 2>&1
exit /b 0

:task13
call :cleandir "%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\Default\Cache"
call :cleandir "%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\Default\Code Cache"
call :cleandir "%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\Default\GPUCache"
exit /b 0

:: Discord: hanya folder cache. Local Storage/Session Storage (token login) TIDAK disentuh.
:task14
call :cleandir "%APPDATA%\discord\Cache"
call :cleandir "%APPDATA%\discord\Code Cache"
call :cleandir "%APPDATA%\discord\GPUCache"
call :cleandir "%APPDATA%\discord\DawnCache"
call :cleandir "%APPDATA%\discord\Service Worker\CacheStorage"
exit /b 0

:: Legcord (Electron): hanya folder cache. settings.json, themes, plugins, Local Storage TIDAK disentuh.
:task15
call :cleandir "%APPDATA%\legcord\Cache"
call :cleandir "%APPDATA%\legcord\Code Cache"
call :cleandir "%APPDATA%\legcord\GPUCache"
call :cleandir "%APPDATA%\legcord\DawnCache"
call :cleandir "%APPDATA%\legcord\Service Worker\CacheStorage"
exit /b 0

:: Meniru WinUtil (config/tweaks.json, WPFTweaksDiskCleanup): cleanmgr.exe /d C: /VERYLOWDISK
:: cleanmgr adalah aplikasi GUI, di WinUtil tidak ditunggu: langsung lanjut ke DISM, cleanmgr bekerja di latar belakang
:task16
echo         cleanmgr dijalankan di latar belakang seperti WinUtil ^(jendelanya boleh hilang sendiri^).
start "" cleanmgr.exe /d %SystemDrive% /VERYLOWDISK
exit /b 0

:: DISM ala Chris Titus WinUtil: Dism.exe /online /Cleanup-Image /StartComponentCleanup /ResetBase
:task17
echo         DISM sering terlihat diam di 20%% atau 62%%, itu normal. Jangan ditutup.
Dism.exe /online /Cleanup-Image /StartComponentCleanup /ResetBase
exit /b 0

using System.Text;
using System.Text.Json;
using SharpCompress.Archives;

namespace Tiny11Builder.Core;

/// <summary>A driver source: a folder, a single .inf, a .7z/.zip archive, or a vendor installer .exe that carries a 7z payload (ASUS style).</summary>
public static class DriverSource
{
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    public static readonly string[] Extensions = [".exe", ".inf", ".7z", ".zip"];

    public static void Validate(string path)
    {
        var full = PathSafety.Full(path);
        if (!Directory.Exists(full) && !(File.Exists(full) && Extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Driver source not found or unsupported (use a folder, .inf, .exe, .7z or .zip): {path}");
    }

    /// <summary>Returns folders containing at least one .inf. Archives are extracted below <paramref name="scratch"/>.</summary>
    public static IReadOnlyList<string> Prepare(IEnumerable<string> sources, string scratch, Action<string> log, CancellationToken token)
    {
        var result = new List<string>();
        var n = 0;
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            Validate(source);
            var full = PathSafety.Full(source);
            string folder;
            if (Directory.Exists(full)) folder = full;
            else if (Path.GetExtension(full).Equals(".inf", StringComparison.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(full)!;
            else
            {
                folder = Path.Combine(scratch, $"driver-{++n}");
                Directory.CreateDirectory(folder);
                Extract(full, folder, token);
            }
            var infs = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Count();
            if (infs == 0) throw new InvalidDataException($"No .inf driver files found in {source}.");
            log($"Driver source {Path.GetFileName(full)}: {infs} .inf file(s).");
            if (!result.Contains(folder, StringComparer.OrdinalIgnoreCase)) result.Add(folder);
        }
        return result;
    }

    private static void Extract(string file, string destination, CancellationToken token)
    {
        var archivePath = file;
        string? carved = null;
        try
        {
            // Vendor .exe installers append a plain 7z archive to the stub: copy only that tail to a temp file.
            if (Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var offset = FindSignature(file) ?? throw new InvalidDataException(
                    $"{Path.GetFileName(file)} has no embedded 7z payload. Extract it first and add the folder instead.");
                carved = destination + ".payload.7z";
                using (var source = File.OpenRead(file))
                using (var target = File.Create(carved))
                {
                    source.Position = offset;
                    source.CopyTo(target);
                }
                archivePath = carved;
            }
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key is not null))
            {
                token.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(destination, entry.Key!.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive entry escapes the extraction folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.OpenEntryStream();
                using var output = File.Create(target);
                input.CopyTo(output);
            }
        }
        finally
        {
            if (carved is not null) File.Delete(carved);
        }
    }

    /// <summary>Finds the offset of a real 7z archive: signature plus a valid start-header CRC. SFX stubs can contain the bare signature bytes.</summary>
    internal static long? FindSignature(string file)
    {
        using var s = File.OpenRead(file);
        var buffer = new byte[1 << 20];
        long basePos = 0;
        var carry = 0;
        int read;
        while ((read = s.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var span = buffer.AsSpan(0, carry + read);
            var from = 0;
            while (from < span.Length)
            {
                var i = span[from..].IndexOf(SevenZipSignature);
                if (i < 0) break;
                var at = basePos + from + i;
                if (IsValidHeader(file, at)) return at;
                from += i + 1;
            }
            carry = Math.Min(SevenZipSignature.Length - 1, span.Length);
            span[^carry..].CopyTo(buffer);
            basePos += span.Length - carry;
        }
        return null;
    }

    private static bool IsValidHeader(string file, long offset)
    {
        using var s = File.OpenRead(file);
        if (offset + 32 > s.Length) return false;
        s.Position = offset;
        var h = new byte[32];
        s.ReadExactly(h);
        var stored = BitConverter.ToUInt32(h, 8);
        if (Crc32(h.AsSpan(12, 20)) != stored) return false;
        var nextOffset = BitConverter.ToInt64(h, 12);
        var nextSize = BitConverter.ToInt64(h, 20);
        return nextOffset >= 0 && nextSize >= 0 && offset + 32 + nextOffset + nextSize <= s.Length;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}

public static class PostInstall
{
    public const string BraveWingetId = "Brave.Brave";
    public const string BraveInstallerName = "BraveSetup.exe";
    public const string JunkCleanerFolder = "JunkCleaner";

    public sealed record AppEntry(string Name, string Winget, string? Installer = null, string? Args = null);

    public static bool Enabled(BuildRecipe r) => r.InstallBrave || r.WinUtilShortcut || r.IncludeJunkCleaner || r.PostInstallApps.Length > 0;

    public static void Validate(BuildRecipe r)
    {
        foreach (var id in r.PostInstallApps)
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9.\-_+]{1,100}$"))
                throw new InvalidDataException($"Invalid winget package id: {id}");
        if (r.PostInstallApps.Length != r.PostInstallApps.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidDataException("Duplicate winget package ids.");
        if (!string.IsNullOrWhiteSpace(r.BraveInstallerPath) && (!r.InstallBrave || !File.Exists(PathSafety.Full(r.BraveInstallerPath))))
            throw new InvalidDataException("The Brave installer path requires Brave to be enabled and the file to exist.");
    }

    public static string ReadJunkCleaner()
    {
        using var stream = typeof(PostInstall).Assembly.GetManifestResourceStream("JunkCleaner.bat")
            ?? throw new InvalidDataException("The embedded JunkCleaner.bat is missing.");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    /// <summary>Writes Windows\Setup\Scripts\SetupComplete.cmd, FirstBoot.ps1 and postinstall.json (and JunkCleaner) into the mounted image.</summary>
    public static void Apply(string mount, BuildRecipe recipe, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Image customization requires Windows.");
        ImageOperations.RequireOwnedMount(mount);
        var dir = Path.Combine(mount, @"Windows\Setup\Scripts");
        PathSafety.RejectReparsePoints(Path.Combine(mount, "Windows"));
        Directory.CreateDirectory(dir);
        var apps = new List<AppEntry>();
        if (recipe.InstallBrave)
        {
            string? bundled = null;
            if (!string.IsNullOrWhiteSpace(recipe.BraveInstallerPath))
            {
                File.Copy(PathSafety.Full(recipe.BraveInstallerPath), Path.Combine(dir, BraveInstallerName), overwrite: true);
                bundled = BraveInstallerName;
            }
            apps.Add(new("Brave", BraveWingetId, bundled, "/silent /install"));
        }
        apps.AddRange(recipe.PostInstallApps.Select(id => new AppEntry(id, id)));
        if (recipe.IncludeJunkCleaner)
        {
            var programFiles = Path.Combine(mount, "Program Files");
            PathSafety.RejectReparsePoints(programFiles);
            var cleaner = Path.Combine(programFiles, JunkCleanerFolder);
            Directory.CreateDirectory(cleaner);
            File.WriteAllText(Path.Combine(cleaner, "JunkCleaner.bat"), ReadJunkCleaner().ReplaceLineEndings("\r\n"), Encoding.ASCII);
        }
        var config = new { apps, winutilShortcut = recipe.WinUtilShortcut, junkCleaner = recipe.IncludeJunkCleaner };
        File.WriteAllText(Path.Combine(dir, "postinstall.json"), JsonSerializer.Serialize(config, JsonDefaults.Options), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "FirstBoot.ps1"), FirstBootScript.ReplaceLineEndings("\r\n"), new UTF8Encoding(true));
        // SetupComplete.cmd runs once as SYSTEM after setup; keep any existing one by chaining.
        var cmd = Path.Combine(dir, "SetupComplete.cmd");
        var existing = File.Exists(cmd) ? File.ReadAllText(cmd) : "";
        var line = "start \"\" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%~dp0FirstBoot.ps1\"\r\n";
        if (!existing.Contains("FirstBoot.ps1"))
            File.WriteAllText(cmd, (existing.Length > 0 && !existing.EndsWith('\n') ? existing + "\r\n" : existing) + line, Encoding.ASCII);
        log($"Post-install engine written: {apps.Count} app(s){(recipe.WinUtilShortcut ? ", WinUtil shortcut" : "")}{(recipe.IncludeJunkCleaner ? ", Junk Cleaner" : "")}.");
    }

    // Runs detached from SetupComplete so Windows setup is never blocked. It registers a SYSTEM scheduled task
    // (startup + logon) that retries until every app is installed, so Wi-Fi that is only connected later still works.
    internal const string FirstBootScript = """
        # Tiny11 first-boot engine (winutil-style catalog in postinstall.json)
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}
        $dir = $PSScriptRoot
        $log = Join-Path $dir 'firstboot.log'
        $statePath = Join-Path $dir 'firstboot.state.json'
        $taskName = 'Tiny11FirstBoot'
        function Log($m) { "$([DateTime]::Now.ToString('s')) $m" | Out-File $log -Append -Encoding utf8 }

        $mutex = New-Object System.Threading.Mutex($false, 'Global\Tiny11FirstBoot')
        if (-not $mutex.WaitOne(0)) { exit }
        try {
            $cfg = Get-Content (Join-Path $dir 'postinstall.json') -Raw | ConvertFrom-Json
            $state = [pscustomobject]@{ attempts = 0; done = @() }
            if (Test-Path $statePath) { try { $state = Get-Content $statePath -Raw | ConvertFrom-Json } catch {} }
            $state.attempts = [int]$state.attempts + 1
            $done = @($state.done)
            function Save-State { $state.done = @($done); $state | ConvertTo-Json | Out-File $statePath -Encoding utf8 }
            Save-State
            Log "First boot pass $($state.attempts)"

            # Retry on every startup/logon until finished (or 15 passes).
            if (-not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
                try {
                    $a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`""
                    $t = @((New-ScheduledTaskTrigger -AtStartup), (New-ScheduledTaskTrigger -AtLogOn))
                    $p = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                    $s = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
                    Register-ScheduledTask -TaskName $taskName -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null
                    Log 'Retry task registered'
                } catch { Log "Retry task failed: $_" }
            }

            function New-Shortcut($path, $target, $arguments, $desc) {
                if (Test-Path $path) { return }
                $s = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
                $s.TargetPath = $target
                if ($arguments) { $s.Arguments = $arguments }
                $s.Description = $desc
                $s.Save()
                # Run as administrator flag
                $b = [IO.File]::ReadAllBytes($path); $b[0x15] = $b[0x15] -bor 0x20; [IO.File]::WriteAllBytes($path, $b)
            }
            $desktop = Join-Path $env:PUBLIC 'Desktop'
            if ($cfg.junkCleaner -and $done -notcontains 'junkcleaner') {
                $bat = Join-Path $env:ProgramFiles 'JunkCleaner\JunkCleaner.bat'
                if (Test-Path $bat) {
                    New-Shortcut (Join-Path $desktop 'Junk Cleaner.lnk') $bat '' 'Junk Files Cleaner'
                    Log 'Junk Cleaner shortcut created'
                } else { Log 'JunkCleaner.bat missing' }
                $done += 'junkcleaner'; Save-State
            }
            if ($cfg.winutilShortcut -and $done -notcontains 'winutil') {
                New-Shortcut (Join-Path $desktop 'WinUtil.lnk') 'powershell.exe' '-NoProfile -ExecutionPolicy Bypass -Command "irm https://christitus.com/win | iex"' 'Chris Titus WinUtil (downloads and runs the latest version)'
                Log 'WinUtil shortcut created'
                $done += 'winutil'; Save-State
            }

            function Wait-Net([int]$sec) {
                $end = (Get-Date).AddSeconds($sec)
                while ((Get-Date) -lt $end) {
                    try { if ((Invoke-WebRequest 'https://www.msftconnecttest.com/connecttest.txt' -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { return $true } } catch {}
                    Start-Sleep 5
                }
                return $false
            }
            function Winget-Path {
                $c = Get-Command winget.exe -ErrorAction SilentlyContinue
                if ($c) { return $c.Source }
                $p = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe\winget.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
                if ($p) { return $p.FullName }
            }
            function Brave-Installed {
                (Test-Path "$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe") -or (Test-Path "${env:ProgramFiles(x86)}\BraveSoftware\Brave-Browser\Application\brave.exe")
            }
            function Install-BraveDirect {
                $arm = $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64'
                $asset = if ($arm) { 'BraveBrowserStandaloneSilentSetupArm64.exe' } else { 'BraveBrowserStandaloneSilentSetup.exe' }
                $urls = @("https://github.com/brave/brave-browser/releases/latest/download/$asset",
                          $(if ($arm) { 'https://laptop-updates.brave.com/latest/winarm64' } else { 'https://laptop-updates.brave.com/latest/winx64' }))
                foreach ($u in $urls) {
                    $f = Join-Path $env:TEMP 'BraveDirectSetup.exe'
                    try {
                        Remove-Item $f -Force -ErrorAction SilentlyContinue
                        Invoke-WebRequest $u -OutFile $f -UseBasicParsing -TimeoutSec 300
                        $sig = Get-AuthenticodeSignature $f
                        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Brave Software') { Log "Brave download rejected ($u): signature $($sig.Status)"; continue }
                        $pr = Start-Process $f -ArgumentList '/silent /install' -Wait -PassThru
                        Log "Brave direct installer exit $($pr.ExitCode)"
                        Remove-Item $f -Force -ErrorAction SilentlyContinue
                        if (Brave-Installed) { return $true }
                    } catch { Log "Brave download failed ($u): $_" }
                }
                return $false
            }
            function Install-Winget($id) {
                $w = Winget-Path
                if (-not $w) { Log "winget unavailable for $id"; return $false }
                foreach ($scope in @(@('--scope', 'machine'), @())) {
                    & $w install --id $id -e --silent --accept-package-agreements --accept-source-agreements @scope 2>&1 | Out-File $log -Append -Encoding utf8
                    Log "winget $id exit $LASTEXITCODE"
                    # 0 = installed, 0x8A150011 = already installed / no newer version
                    if ($LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq -1978335215 -or $LASTEXITCODE -eq -1978335189) { return $true }
                }
                return $false
            }

            $netChecked = $false; $netOk = $false
            foreach ($app in @($cfg.apps)) {
                $key = "app:$($app.winget)"
                if ($done -contains $key) { continue }
                $ok = $false
                $isBrave = $app.winget -eq 'Brave.Brave'
                if ($isBrave -and (Brave-Installed)) { $ok = $true }
                $local = if ($app.installer) { Join-Path $dir $app.installer } else { $null }
                if (-not $ok -and $local -and (Test-Path $local)) {
                    Log "Installing $($app.name) from bundled installer"
                    $pr = Start-Process $local -ArgumentList $app.args -Wait -PassThru
                    Log "$($app.name) bundled installer exit $($pr.ExitCode)"
                    if ($isBrave) { $ok = Brave-Installed } else { $ok = ($pr.ExitCode -eq 0) }
                }
                if (-not $ok) {
                    if (-not $netChecked) { $netOk = Wait-Net 180; $netChecked = $true; if (-not $netOk) { Log 'No network yet; will retry on next startup/logon.' } }
                    if ($netOk) {
                        if ($isBrave) { $ok = (Install-BraveDirect) -or ((Install-Winget 'Brave.Brave') -and (Brave-Installed)) }
                        else { $ok = Install-Winget $app.winget }
                    }
                }
                if ($ok) { $done += $key; Save-State; Log "$($app.name) done" }
            }

            $pending = @($cfg.apps | Where-Object { $done -notcontains "app:$($_.winget)" })
            if ($pending.Count -eq 0 -or $state.attempts -ge 15) {
                if ($pending.Count -gt 0) { Log "Giving up on: $(($pending | ForEach-Object { $_.name }) -join ', ')" }
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
                Remove-Item (Join-Path $dir 'BraveSetup.exe') -Force -ErrorAction SilentlyContinue
                Log 'First boot done'
            }
        } finally { $mutex.ReleaseMutex() }
        """;
}

public static class DriverInjector
{
    /// <summary>
    /// Adds every driver folder with DISM. A vendor pack can hold INFs DISM rejects (other OS/arch variants); one bad INF must not
    /// fail the build, so a failed bulk add is retried INF by INF. Fails only when nothing at all was accepted.
    /// </summary>
    public static async Task<int> AddAsync(DismClient dism, string mount, IReadOnlyList<string> folders, CancellationToken token, Action<string> log)
    {
        var added = 0;
        var skipped = new List<string>();
        foreach (var folder in folders)
        {
            token.ThrowIfCancellationRequested();
            log($"Adding drivers from {folder}");
            try
            {
                await dism.RunAsync(token, $"/Image:{mount}", "/Add-Driver", $"/Driver:{folder}", "/Recurse").ConfigureAwait(false);
                added += Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Count();
            }
            catch (DismCommandException ex)
            {
                log($"Bulk add failed (exit {ex.ExitCode}); retrying each .inf separately.");
                foreach (var inf in Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await dism.RunAsync(token, $"/Image:{mount}", "/Add-Driver", $"/Driver:{inf}").ConfigureAwait(false);
                        added++;
                    }
                    catch (DismCommandException) { skipped.Add(inf); log($"Skipped incompatible driver: {Path.GetFileName(inf)}"); }
                }
            }
        }
        if (added == 0) throw new InvalidDataException("DISM accepted none of the supplied drivers (architecture or OS mismatch?).");
        log($"Drivers added: {added}; skipped: {skipped.Count}.");
        return added;
    }
}

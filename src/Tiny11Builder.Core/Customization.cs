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
            var i = span.IndexOf(SevenZipSignature);
            if (i >= 0) return basePos + i;
            carry = Math.Min(SevenZipSignature.Length - 1, span.Length);
            span[^carry..].CopyTo(buffer);
            basePos += span.Length - carry;
        }
        return null;
    }
}

public static class PostInstall
{
    public const string BraveWingetId = "Brave.Brave";
    public const string BraveInstallerName = "BraveSetup.exe";

    public sealed record AppEntry(string Name, string Winget, string? Installer = null, string? Args = null);

    public static bool Enabled(BuildRecipe r) => r.InstallBrave || r.WinUtilShortcut || r.PostInstallApps.Length > 0;

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

    /// <summary>Writes Windows\Setup\Scripts\SetupComplete.cmd, FirstBoot.ps1 and postinstall.json into the mounted image.</summary>
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
        var config = new { apps, winutilShortcut = recipe.WinUtilShortcut };
        File.WriteAllText(Path.Combine(dir, "postinstall.json"), JsonSerializer.Serialize(config, JsonDefaults.Options), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "FirstBoot.ps1"), FirstBootScript, new UTF8Encoding(true));
        // SetupComplete.cmd runs once as SYSTEM after setup; keep any existing one by chaining.
        var cmd = Path.Combine(dir, "SetupComplete.cmd");
        var existing = File.Exists(cmd) ? File.ReadAllText(cmd) : "";
        var line = "start \"\" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%~dp0FirstBoot.ps1\"\r\n";
        if (!existing.Contains("FirstBoot.ps1")) File.WriteAllText(cmd, existing + line, Encoding.ASCII);
        log($"Post-install engine written: {apps.Count} app(s){(recipe.WinUtilShortcut ? ", WinUtil shortcut" : "")}.");
    }

    // Runs detached so Windows setup is never blocked; retries until the network (LAN/Wi-Fi driver) is up.
    internal const string FirstBootScript = """
        # Tiny11 first-boot engine (winutil-style catalog in postinstall.json)
        $ErrorActionPreference = 'Continue'
        $dir = $PSScriptRoot
        $log = Join-Path $env:SystemRoot 'Setup\Scripts\firstboot.log'
        function Log($m) { "$([DateTime]::Now.ToString('s')) $m" | Out-File $log -Append -Encoding utf8 }
        $cfg = Get-Content (Join-Path $dir 'postinstall.json') -Raw | ConvertFrom-Json

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
            $p = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe" -ErrorAction SilentlyContinue | Select-Object -Last 1
            if ($p) { return $p.FullName }
        }

        $pending = @()
        foreach ($app in $cfg.apps) {
            $local = if ($app.installer) { Join-Path $dir $app.installer } else { $null }
            if ($local -and (Test-Path $local)) {
                Log "Installing $($app.name) from bundled installer"
                $p = Start-Process $local -ArgumentList $app.args -Wait -PassThru
                Log "$($app.name) exit code $($p.ExitCode)"
                if ($p.ExitCode -eq 0) { Remove-Item $local -Force -ErrorAction SilentlyContinue; continue }
            }
            $pending += $app
        }

        if ($pending.Count -gt 0) {
            if (-not (Wait-Net 600)) { Log 'No network after 10 minutes; online installs skipped.' }
            else {
                $w = Winget-Path
                foreach ($app in $pending) {
                    if ($w) {
                        Log "winget install $($app.winget)"
                        & $w install --id $app.winget -e --silent --accept-package-agreements --accept-source-agreements --scope machine 2>&1 | Out-File $log -Append -Encoding utf8
                        Log "$($app.name) winget exit $LASTEXITCODE"
                    }
                    elseif ($app.winget -eq 'Brave.Brave') {
                        $f = Join-Path $env:TEMP 'BraveSetup.exe'
                        try {
                            Invoke-WebRequest 'https://github.com/brave/brave-browser/releases/latest/download/BraveBrowserStandaloneSilentSetup.exe' -OutFile $f -UseBasicParsing
                            $sig = Get-AuthenticodeSignature $f
                            if ($sig.Status -eq 'Valid' -and $sig.SignerCertificate.Subject -match 'Brave Software') { Start-Process $f -ArgumentList '/silent /install' -Wait; Log 'Brave installed (direct download)' }
                            else { Log "Brave download rejected: signature $($sig.Status)" }
                        } catch { Log "Brave download failed: $_" }
                    }
                    else { Log "winget unavailable; skipped $($app.name)" }
                }
            }
        }

        if ($cfg.winutilShortcut) {
            $lnk = Join-Path $env:PUBLIC 'Desktop\WinUtil.lnk'
            $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
            $s.TargetPath = 'powershell.exe'
            $s.Arguments = '-NoProfile -ExecutionPolicy Bypass -Command "irm https://christitus.com/win | iex"'
            $s.Description = 'Chris Titus WinUtil (downloads and runs the latest version)'
            $s.Save()
            Log 'WinUtil shortcut created'
        }
        Log 'First boot done'
        """;
}

public static class DriverInjector
{
    public static async Task AddAsync(DismClient dism, string mount, IReadOnlyList<string> folders, CancellationToken token, Action<string> log)
    {
        foreach (var folder in folders)
        {
            token.ThrowIfCancellationRequested();
            log($"Adding drivers from {folder}");
            await dism.RunAsync(token, $"/Image:{mount}", "/Add-Driver", $"/Driver:{folder}", "/Recurse").ConfigureAwait(false);
        }
    }
}

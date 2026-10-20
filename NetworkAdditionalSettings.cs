using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed record NetworkAdditionalSettings
{
    public string WifiProfile { get; init; } = "";
    public string Printer { get; init; } = "";
    public string Workgroup { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public string DriveLetter { get; init; } = "Z:";
    public string DrivePath { get; init; } = "";
    public string WinsPreferred { get; init; } = "";
    public string WinsAlternate { get; init; } = "";
    public string RoutePrefix { get; init; } = "";
    public string RouteGateway { get; init; } = "";
    public string HostsEntries { get; init; } = "";
    public string ProgramPath { get; init; } = "";
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 587;
    public string AutoGateway { get; init; } = "";
}

internal static partial class NetworkAdditionalPolicy
{
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ComputerPattern();
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
    internal static void Validate(string action, NetworkAdditionalSettings s)
    {
        if (action is "adapter-enable" or "adapter-disable" or "printers" or "wifi-list") return;
        switch (action)
        {
            case "wifi":
                if (string.IsNullOrWhiteSpace(s.WifiProfile) || s.WifiProfile.Length > 256 || s.WifiProfile.Any(char.IsControl)) throw new ArgumentException("Enter an existing Windows Wi-Fi profile name."); break;
            case "printer":
                if (string.IsNullOrWhiteSpace(s.Printer) || s.Printer.Length > 256 || s.Printer.Any(char.IsControl)) throw new ArgumentException("Select an installed printer name."); break;
            case "workgroup": ValidateComputer(s.Workgroup); break;
            case "computer": ValidateComputer(s.ComputerName); break;
            case "drive":
                if (s.DriveLetter.Length != 2 || s.DriveLetter[0] < 'D' || s.DriveLetter[0] > 'Z' || s.DriveLetter[1] != ':') throw new ArgumentException("Use a drive letter D: through Z:.");
                if (!s.DrivePath.StartsWith(@"\\", StringComparison.Ordinal) || s.DrivePath.StartsWith(@"\\?\", StringComparison.Ordinal) || s.DrivePath.StartsWith(@"\\.\", StringComparison.Ordinal) || s.DrivePath.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2 || s.DrivePath.Any(char.IsControl) || s.DrivePath.Contains('"')) throw new ArgumentException("Enter a UNC share such as \\\\server\\share; no credentials are saved."); break;
            case "wins": NetworkSettingsValidation.Address(s.WinsPreferred, false, "Preferred WINS"); NetworkSettingsValidation.Address(s.WinsAlternate, false, "Alternate WINS", true); break;
            case "route":
                var parts = s.RoutePrefix.Split('/');
                bool v6 = parts[0].Contains(':');
                if (parts.Length != 2 || !NetworkSettingsValidation.StrictAddress(parts[0], v6, out _) || !int.TryParse(parts[1], out int prefix) || prefix < 1 || prefix > (v6 ? 128 : 32)) throw new ArgumentException("Enter a non-default destination prefix, e.g. 10.20.0.0/16. Manage default gateways in the IP editor.");
                NetworkSettingsValidation.Address(s.RouteGateway, v6, "Route next hop"); break;
            case "hosts": ValidateHosts(s.HostsEntries); break;
            case "program":
                if (!Path.IsPathFullyQualified(s.ProgramPath) || s.ProgramPath.StartsWith(@"\\", StringComparison.Ordinal) || !new[] { ".exe", ".ps1" }.Contains(Path.GetExtension(s.ProgramPath).ToLowerInvariant()) || !File.Exists(s.ProgramPath)) throw new ArgumentException("Select an existing local .exe or .ps1 file. UNC programs and shell command strings are not accepted."); break;
            case "smtp":
                if (!HostPattern().IsMatch(s.SmtpHost) || s.SmtpPort < 1 || s.SmtpPort > 65535) throw new ArgumentException("Enter an SMTP hostname and port 1–65535."); break;
            default: throw new ArgumentException("Unknown additional network action.");
        }
    }
    private static void ValidateComputer(string value)
    {
        if (!ComputerPattern().IsMatch(value) || value.All(char.IsAsciiDigit)) throw new ArgumentException("Use a 1–15 character computer/workgroup name with letters, digits and internal hyphens; not all digits.");
    }
    internal static void ValidateHosts(string entries)
    {
        if (entries.Length > 16_000) throw new ArgumentException("Hosts entries exceed 16 KB.");
        foreach (string line in entries.Replace("\r", "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words.Length > 10 || !IPAddress.TryParse(words[0], out _) || words.Skip(1).Any(h => !HostPattern().IsMatch(h))) throw new ArgumentException("Each hosts line must contain an IP followed by hostnames; comments and wildcard names are not accepted.");
        }
    }
    internal const string BeginHosts = "# BEGIN NAUFAL WINDOWS UTILITY NETWORK HOSTS";
    internal const string EndHosts = "# END NAUFAL WINDOWS UTILITY NETWORK HOSTS";
    internal static string MergeHosts(string original, string entries)
    {
        ValidateHosts(entries);
        int begin = original.IndexOf(BeginHosts, StringComparison.Ordinal), end = original.IndexOf(EndHosts, StringComparison.Ordinal);
        if ((begin < 0) != (end < 0) || (begin >= 0 && (end < begin || original.IndexOf(BeginHosts, begin + 1, StringComparison.Ordinal) >= 0 || original.IndexOf(EndHosts, end + 1, StringComparison.Ordinal) >= 0))) throw new IOException("The owned hosts section is ambiguous. Review the hosts file manually.");
        string block = entries.Trim().Length == 0 ? "" : BeginHosts + "\r\n" + entries.Trim().Replace("\r", "").Replace("\n", "\r\n") + "\r\n" + EndHosts;
        return begin < 0 ? original + (block.Length == 0 ? "" : "\r\n" + block + "\r\n") : original[..begin] + block + original[(end + EndHosts.Length)..];
    }
}

internal sealed class NetworkAdditionalService
{
    private readonly NativeCommandRunner _runner = new();
    internal static string ProgramHash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    internal async Task<string> RunAsync(string action, string adapterId, NetworkAdditionalSettings settings, string? expectedHash = null)
    {
        NetworkAdditionalPolicy.Validate(action, settings);
        if (action == "smtp")
        {
            using TcpClient client = new(); using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, timeout.Token);
            return "TCP connection accepted. SMTP authentication, STARTTLS/TLS and mail delivery were not tested. No email or credentials were sent.";
        }
        if (action == "program")
        {
            if (expectedHash is null || ProgramHash(settings.ProgramPath) != expectedHash) throw new IOException("Program changed after confirmation; review again.");
            string exe = settings.ProgramPath;
            string[] args = [];
            if (Path.GetExtension(exe).Equals(".ps1", StringComparison.OrdinalIgnoreCase)) { args = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", exe]; exe = PowerShell; }
            var result = await _runner.RunAsync(exe, args, TimeSpan.FromMinutes(2));
            return RequireSuccess(result) + "\nProgram exited. Its side effects are outside the network snapshot and are not automatically rolled back.";
        }
        if (!WindowsPrivilegeService.IsAdministrator() && action is not ("printers" or "wifi-list")) throw new InvalidOperationException("Run as Administrator for this action.");
        if (action == "hosts") return UpdateHosts(settings.HostsEntries);
        if (!Guid.TryParse(adapterId, out _)) throw new ArgumentException("Select a physical adapter first.");
        string data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, NetworkSettingsJson.Default.NetworkAdditionalSettings)));
        string script = NetworkSettingsScript.Common + "\n$s = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n" +
            "$action = '" + action + "'\n$id = [guid]'" + Guid.Parse(adapterId).ToString() + "'\n" + Script;
        var command = await _runner.RunAsync(PowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(60), outputEncoding: Encoding.UTF8);
        return RequireSuccess(command);
    }
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string RequireSuccess(NativeCommandResult result)
    {
        if (result.ExitCode != 0 || result.TimedOut) throw new IOException(result.CombinedOutput);
        return result.CombinedOutput;
    }
    private static string UpdateHosts(string entries)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        byte[] original = File.ReadAllBytes(path);
        if (original.Length > 2_000_000 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Hosts file is too large or redirected. Review manually.");
        UTF8Encoding encoding = new(false, true);
        string merged = NetworkAdditionalPolicy.MergeHosts(encoding.GetString(original), entries);
        string backup = path + ".nwu-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
        string temp = path + ".nwu-" + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, merged, encoding);
        if (!File.ReadAllBytes(path).SequenceEqual(original)) { File.Delete(temp); throw new IOException("Hosts file changed during preparation. Reload before retrying."); }
        File.Replace(temp, path, backup);
        if (File.ReadAllText(path, encoding) != merged.TrimStart('\uFEFF')) throw new IOException("Hosts update read-back differs; recovery copy: " + backup);
        return "Owned hosts block updated. Other entries retained. Full original backup: " + backup + "\nRestore the backup manually if needed; DNS cache was not flushed.";
    }
    internal const string Script = """
        if ($action -eq 'printers') { Get-CimInstance Win32_Printer | Select-Object Name,Default | Format-Table -AutoSize | Out-String; return }
        if ($action -eq 'wifi-list') { & "$env:SystemRoot\System32\netsh.exe" wlan show profiles; if ($LASTEXITCODE) { throw 'Wi-Fi profile inventory failed.' }; return }
        Check-LocalControl
        $a = @(Get-NetAdapter -Physical | Where-Object { [guid]$_.InterfaceGuid -eq $id -and $_.InterfaceType -in @(6,71) })
        if ($a.Count -ne 1) { throw 'Physical adapter no longer present.' }
        $a = $a[0]
        if ($action -in @('wins','route') -and $a.InterfaceType -ne 6) { throw 'WINS and persistent routes are adapter-wide and Ethernet-only. No Wi-Fi adapter properties were changed.' }
        switch ($action) {
            'adapter-enable' { $a | Enable-NetAdapter -Confirm:$false; 'Enable requested. Refresh to inspect connection status.' }
            'adapter-disable' { $a | Disable-NetAdapter -Confirm:$false; 'Disable requested. Refresh to inspect status; use Enable to reconnect.' }
            'wifi' {
                if ($a.InterfaceType -ne 71) { throw 'Select a Wi-Fi adapter.' }
                & "$env:SystemRoot\System32\netsh.exe" wlan connect ('name=' + $s.WifiProfile) ('interface=' + $a.Name)
                if ($LASTEXITCODE) { throw 'Windows did not accept the Wi-Fi connection request.' }
                'Request accepted; association is asynchronous. Refresh adapter information to inspect the result.'
            }
            'printer' {
                $printer = @(Get-CimInstance Win32_Printer | Where-Object { $_.Name -ceq $s.Printer })
                if ($printer.Count -ne 1) { throw 'Printer not installed or ambiguous.' }
                $result = Invoke-CimMethod -InputObject $printer[0] -MethodName SetDefaultPrinter
                if ($result.ReturnValue -ne 0) { throw ('Printer operation failed: ' + $result.ReturnValue) }
                $check = @(Get-CimInstance Win32_Printer | Where-Object { $_.Name -ceq $s.Printer -and $_.Default })
                if ($check.Count -ne 1) { throw 'Default printer read-back failed.' }
                'Default printer verified for this execution account. Windows automatic default-printer management may override it later.'
            }
            'workgroup' { Add-Computer -WorkGroupName $s.Workgroup -Force; 'Workgroup change accepted. Restart Windows manually to complete and verify. No reboot requested.' }
            'computer' { Rename-Computer -NewName $s.ComputerName -Force; 'Computer rename accepted. Restart Windows manually to complete and verify. No reboot requested.' }
            'drive' {
                if (Get-PSDrive -Name $s.DriveLetter.Substring(0,1) -ErrorAction SilentlyContinue) { throw 'Drive letter already in use; no mapping replaced.' }
                & "$env:SystemRoot\System32\net.exe" use $s.DriveLetter $s.DrivePath /persistent:yes
                if ($LASTEXITCODE) { throw 'Mapping failed. Configure credentials using Windows Credential Manager.' }
                $mapping = @(Get-SmbMapping | Where-Object { $_.LocalPath -eq $s.DriveLetter -and $_.RemotePath -eq $s.DrivePath })
                if ($mapping.Count -ne 1) { throw 'Drive mapping read-back failed.' }
                'Mapping verified in this logon context. Elevated mappings may not appear in non-elevated Explorer. Use Windows Explorer for a mapping in its own context.'
            }
            'wins' {
                $nic = @(Get-CimInstance Win32_NetworkAdapterConfiguration | Where-Object { $_.SettingID -and [guid]$_.SettingID -eq $id })
                if ($nic.Count -ne 1) { throw 'WINS configuration target not found.' }
                $r = Invoke-CimMethod -InputObject $nic[0] -MethodName SetWINSServer -Arguments @{ WINSPrimaryServer = [string]$s.WinsPreferred; WINSSecondaryServer = [string]$s.WinsAlternate }
                if ($r.ReturnValue -notin @(0,1)) { throw ('WINS operation failed: ' + $r.ReturnValue) }
                'WINS configuration accepted. Refresh Windows TCP/IP properties to verify. Return code: ' + $r.ReturnValue
            }
            'route' {
                if (@(Get-NetRoute -InterfaceIndex $a.ifIndex | Where-Object { $_.DestinationPrefix -eq $s.RoutePrefix }).Count) { throw 'A route for this destination already exists. No route replaced.' }
                New-NetRoute -InterfaceIndex $a.ifIndex -DestinationPrefix $s.RoutePrefix -NextHop $s.RouteGateway -PolicyStore PersistentStore | Out-Null
                if (-not @(Get-NetRoute -InterfaceIndex $a.ifIndex -PolicyStore PersistentStore | Where-Object { $_.DestinationPrefix -eq $s.RoutePrefix -and $_.NextHop -eq $s.RouteGateway }).Count) { throw 'Persistent route verification failed.' }
                'Persistent route stored and read back. Reachability is not tested. Use Windows advanced routing tools to remove it.'
            }
            default { throw 'Unknown network action.' }
        }
        """;
}

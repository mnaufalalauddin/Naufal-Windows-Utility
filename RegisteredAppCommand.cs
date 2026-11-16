using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed record RegisteredAppCommand(string Executable, string[] Arguments, string? NvidiaModule = null)
{
    internal ProcessStartInfo StartInfo()
    {
        var start = new ProcessStartInfo(Executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Executable)! };
        foreach (string argument in Arguments) start.ArgumentList.Add(argument);
        return start;
    }

    // Resolve the executable explicitly; never pass an unquoted path to Windows'
    // executable search, which could select C:\Program.exe instead of the vendor.
    internal static RegisteredAppCommand Parse(string command, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        command = command.Trim();
        if (command.Length == 0) throw new InvalidOperationException("No uninstaller is registered. Use Windows Installed apps.");
        string executable, arguments;
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            if (end < 0 || end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1]))
                throw new InvalidOperationException("Malformed quoted executable path. Use Windows Installed apps.");
            executable = command[1..end]; arguments = command[(end + 1)..].TrimStart();
        }
        else
        {
            var candidates = new List<(string Path, int End)>();
            foreach (Match match in Regex.Matches(command, @"\.exe(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                int end = match.Index + match.Length;
                string path = Environment.ExpandEnvironmentVariables(command[..end]);
                if (IsLocalPath(path) && exists(path)) candidates.Add((path, end));
            }
            if (candidates.Count > 1) throw new InvalidOperationException("Ambiguous executable path. Use Windows Installed apps.");
            if (candidates.Count == 1)
            { executable = candidates[0].Path; arguments = command[candidates[0].End..].TrimStart(); }
            else
            {
                int end = command.IndexOfAny([' ', '\t']);
                executable = end < 0 ? command : command[..end];
                arguments = end < 0 ? "" : command[end..].TrimStart();
            }
        }
        executable = Environment.ExpandEnvironmentVariables(executable);
        if (new[] { "msiexec", "msiexec.exe", "rundll32", "rundll32.exe" }.Contains(executable, StringComparer.OrdinalIgnoreCase))
            executable = Path.Combine(Environment.SystemDirectory, Path.GetFileNameWithoutExtension(executable) + ".exe");
        if (!IsLocalPath(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !exists(executable))
            throw new InvalidOperationException("Registered executable is missing or is not a local absolute EXE path. Use Windows Installed apps.");
        return new(Path.GetFullPath(executable), SplitArguments(arguments));
    }

    internal static RegisteredAppCommand Uninstall(DesktopAppEntry app, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (app.SystemComponent) throw new InvalidOperationException("System component: use Windows or the vendor's servicing tools.");
        var plan = Parse(app.UninstallCommand, exists);
        string host = Path.GetFileNameWithoutExtension(plan.Executable);
        if (host.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            // Narrow support for NVIDIA's registered InstallerCore route. Not a
            // general permission to run arbitrary DLL exports or script hosts.
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            bool systemHost = new[] { Path.Combine(windows, "System32", "rundll32.exe"), Path.Combine(windows, "SysWOW64", "rundll32.exe") }
                .Contains(plan.Executable, StringComparer.OrdinalIgnoreCase);
            string suffix = ",UninstallPackage";
            if (!systemHost || !app.Identity.StartsWith("LocalMachine|", StringComparison.Ordinal) ||
                plan.Arguments.Length != 2 || !plan.Arguments[0].EndsWith(suffix, StringComparison.Ordinal) ||
                !Regex.IsMatch(plan.Arguments[1], @"^[A-Za-z0-9.]+$") ||
                !app.Identity.EndsWith("_" + plan.Arguments[1], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsupported DLL uninstaller. Use Windows Installed apps.");
            string dll = plan.Arguments[0][..^suffix.Length];
            if (!IsLocalPath(dll)) throw new InvalidOperationException("Uninstaller DLL must be local.");
            dll = Path.GetFullPath(dll);
            string[] allowed = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                .Where(p => p.Length > 0).Select(p => Path.Combine(p, "NVIDIA Corporation", "Installer2", "InstallerCore", "NVI2.DLL")).ToArray();
            if (!allowed.Contains(dll, StringComparer.OrdinalIgnoreCase) || !exists(dll))
                throw new InvalidOperationException("NVIDIA InstallerCore is missing or has an unexpected path. Use the official NVIDIA installer or Windows Installed apps.");
            return plan with { NvidiaModule = dll };
        }
        if (new[] { "cmd", "powershell", "pwsh", "wscript", "cscript", "regsvr32", "mshta" }.Contains(host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Script-host uninstallers are not launched here. Use Windows Installed apps.");
        return plan;
    }

    private static bool IsLocalPath(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
        path[2] == '\\' && path.IndexOf(':', 2) < 0 && !path.Contains('"') && Path.IsPathFullyQualified(path);
    private static string[] SplitArguments(string arguments)
    {
        nint argv = CommandLineToArgvW("placeholder.exe " + arguments, out int count);
        if (argv == 0) throw new InvalidOperationException("Unable to parse registered arguments.");
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(argv); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint LocalFree(nint memory);
}

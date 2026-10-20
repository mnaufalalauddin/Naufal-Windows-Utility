using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

internal sealed partial class NetworkAdditionalView : StackPanel
{
    internal Func<string, NetworkAdditionalSettings, Task>? Requested;
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly List<Control> _controls = [];
    private readonly List<Control> _ethernetControls = [];
    private bool _ethernet, _busy;
    internal NetworkAdditionalView()
    {
        Spacing = 12;
        Children.Add(NetworkSettingsView.Text("Additional actions run individually after confirmation, separately from Save to Windows. Values are not saved as local profiles. These actions are outside IP/DNS snapshot rollback; their result messages state the available verification."));
        AddSection("Adapter & Wi-Fi", [("WifiProfile", "Existing Windows Wi-Fi profile")], [("wifi-list", "List saved Wi-Fi profiles"), ("wifi", "Connect saved Wi-Fi profile…"), ("adapter-enable", "Enable adapter…"), ("adapter-disable", "Disable adapter…")]);
        AddSection("Printer", [("Printer", "Installed printer name")], [("printers", "List installed printers"), ("printer", "Set default printer…")]);
        AddSection("Computer & workgroup", [("ComputerName", "Computer name"), ("Workgroup", "Workgroup")], [("computer", "Rename computer…"), ("workgroup", "Join workgroup…")]);
        AddSection("Network drive", [("DriveLetter", "Unused drive letter (D:–Z:)"), ("DrivePath", "UNC path (\\\\server\\share)")], [("drive", "Map network drive…")]);
        AddSection("Advanced TCP/IP", [("WinsPreferred", "Preferred WINS (IPv4)"), ("WinsAlternate", "Alternate WINS (optional)"), ("RoutePrefix", "Static route destination (e.g. 10.20.0.0/16)"), ("RouteGateway", "Route next hop")], [("wins", "Set WINS servers…"), ("route", "Add persistent route…")]);
        AddSection("Hosts", [("HostsEntries", "Owned hosts entries (IP hostname per line; blank removes only the owned section)")], [("hosts", "Update owned hosts block…")]);
        _fields["HostsEntries"].AcceptsReturn = true; _fields["HostsEntries"].TextWrapping = TextWrapping.Wrap; _fields["HostsEntries"].MaxLength = 16000; _fields["HostsEntries"].Height = 150;
        ScrollViewer.SetVerticalScrollBarVisibility(_fields["HostsEntries"], ScrollBarVisibility.Auto);
        AddSection("Script / program", [("ProgramPath", "Local .exe or .ps1 path (no arguments)")], [("program", "Review hash & run…")]);
        AddSection("SMTP connection check", [("SmtpHost", "SMTP server hostname"), ("SmtpPort", "SMTP TCP port")], [("smtp", "Test TCP connection…")]);
        Load(new());
    }
    private void AddSection(string title, (string Key, string Label)[] fields, (string Key, string Label)[] actions)
    {
        StackPanel panel = new() { Spacing = 10 };
        foreach (var field in fields) AddField(panel, field.Key, field.Label);
        foreach (var action in actions)
        {
            var button = MakeButton(action.Label, async () => { if (Requested is not null) await Requested(action.Key, Value()); });
            panel.Children.Add(button);
            if (action.Key is "wins" or "route") _ethernetControls.Add(button);
        }
        Children.Add(new Expander { Header = title, Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
    }
    private void AddField(StackPanel panel, string key, string label) { TextBox box = NetworkSettingsView.Field(label); _fields[key] = box; _controls.Add(box); panel.Children.Add(box); }
    private Button MakeButton(string label, Func<Task> action)
    {
        Button button = new() { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch };
        _controls.Add(button);
        button.Click += async (_, _) => { if (Requested is null) return; try { await action(); } catch (Exception ex) { await Requested("error", new() { HostsEntries = ex.Message }); } };
        return button;
    }
    internal void Busy(bool busy) { _busy = busy; foreach (Control c in _controls) c.IsEnabled = !busy; AdapterScope(_ethernet); }
    internal void AdapterScope(bool ethernet) { _ethernet = ethernet; foreach (Control c in _ethernetControls) c.IsEnabled = ethernet && !_busy; }
    internal NetworkAdditionalSettings Value() => new()
    {
        WifiProfile = Get("WifiProfile"), Printer = Get("Printer"), ComputerName = Get("ComputerName"), Workgroup = Get("Workgroup"), DriveLetter = Get("DriveLetter"), DrivePath = Get("DrivePath"),
        WinsPreferred = Get("WinsPreferred"), WinsAlternate = Get("WinsAlternate"), RoutePrefix = Get("RoutePrefix"), RouteGateway = Get("RouteGateway"), HostsEntries = Get("HostsEntries"), ProgramPath = Get("ProgramPath"),
        SmtpHost = Get("SmtpHost"), SmtpPort = int.TryParse(Get("SmtpPort"), out int port) ? port : 0
    };
    private string Get(string key) => _fields[key].Text.Trim();
    internal void Load(NetworkAdditionalSettings s)
    {
        _fields["WifiProfile"].Text = s.WifiProfile; _fields["Printer"].Text = s.Printer; _fields["Workgroup"].Text = s.Workgroup; _fields["ComputerName"].Text = s.ComputerName;
        _fields["DriveLetter"].Text = s.DriveLetter; _fields["DrivePath"].Text = s.DrivePath; _fields["WinsPreferred"].Text = s.WinsPreferred; _fields["WinsAlternate"].Text = s.WinsAlternate;
        _fields["RoutePrefix"].Text = s.RoutePrefix; _fields["RouteGateway"].Text = s.RouteGateway; _fields["HostsEntries"].Text = s.HostsEntries; _fields["ProgramPath"].Text = s.ProgramPath;
        _fields["SmtpHost"].Text = s.SmtpHost; _fields["SmtpPort"].Text = s.SmtpPort.ToString();
    }
}

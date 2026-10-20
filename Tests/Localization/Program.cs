using Naufal_Windows_Tech_s_Powertoys;
using System.Xml.Linq;

// Historical test entry point now enforces the English-only contract.
string root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "MainWindow.xaml")))
    root = Directory.GetParent(root)?.FullName ?? throw new Exception("Project root not found.");
int checks = 0;
void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
string Read(string name) => File.ReadAllText(Path.Combine(root, name));
string settings = Read("UiDisplaySettings.cs");
Assert(!settings.Contains("SetLanguage") && !settings.Contains("LanguageCode") && !settings.Contains("ui-language.txt"), "Language preference/switching remains");
Assert(settings.Contains("root.Language = \"en-US\"") && settings.Contains("FlowDirection.LeftToRight"), "English presentation not fixed");
Assert(Read("App.xaml.cs").Contains("Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language"), "Use unpackaged-compatible resource API");
Assert(!Read("App.xaml.cs").Contains("language => Windows.Globalization."), "UWP-only override must not block unpackaged startup");
Assert(Read("ApplicationLanguagePolicy.cs").Contains("CultureInfo.GetCultureInfo(\"en-US\")"), "English UI culture not fixed");
foreach (string file in Directory.GetFiles(root, "*.cs"))
{
    string name = Path.GetFileName(file);
    Assert(!name.StartsWith("NativeUiCatalog") && !name.StartsWith("UiTranslation") && name != "SupplementalUiCatalog.cs", "Owned translation payload remains: " + name);
    Assert(!File.ReadAllText(file).Contains("UiTranslation."), "Runtime translation dependency remains: " + name);
}
var xaml = XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
Assert(!Read("MainWindow.xaml").Contains("LanguageComboBox"), "Language selector remains");
var nav = xaml.Descendants().Where(e => e.Name.LocalName == "NavigationViewItem").ToArray();
Assert(nav.Select(e => (string?)e.Attribute("Content")).SequenceEqual(new[] { "Home", "System Repair", "System Info", "Windows Security", "Advanced Windows Tweaks", "Network Settings Manager" }), "Sidebar contract drift");
Assert(!Read("Installer/NaufalWindowsPowertoys.iss").Contains("ProgramInfoLanguage"), "Installer language selector remains");
Assert(Read("Installer/Generate-ProgramInformation.ps1").Contains("EnglishUiText.cs"), "Installer does not share English copy");
Assert(!string.IsNullOrWhiteSpace(EnglishUiText.AboutDescription) && !string.IsNullOrWhiteSpace(EnglishUiText.AboutPurpose), "About copy missing");
Assert(CatalogDisplayNames.Simplify("Widgets - Remove") == "Taskbar Widgets", "English catalog names changed");
Console.WriteLine($"PASS: {checks} English-only UI/resource/navigation assertions. No Windows settings changed.");

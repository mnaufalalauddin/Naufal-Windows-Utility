using Naufal_Windows_Tech_s_Powertoys;
using System.Text.Json;

if (args.Length > 0 && args[0] is "--ssid-dns-v6-roundtrip" or "--ssid-dns-v6-apply-retain")
{
    try { await WifiSsidDnsV6Audit.Run(args); }
    catch (Exception ex) { Console.WriteLine("AUDIT FAILED: " + ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length > 0 && args[0] is "--ssid-manual-v6-boot-resume" or "--ssid-manual-v6-boot-restore")
{
    try { await WifiSsidV6BootAudit.Run(args); }
    catch (Exception ex) { Console.WriteLine("AUDIT FAILED: " + ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length > 0 && args[0] is "--ssid-manual-v6-roundtrip" or "--ssid-manual-v6-boot-prepare")
{
    try { await WifiSsidManualV6Audit.Run(args); }
    catch (Exception ex) { Console.WriteLine("AUDIT FAILED: " + ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length > 0 && args[0] == "--ssid-manual-v4-roundtrip")
{
    try { await WifiSsidManualV4Audit.Run(args); }
    catch (Exception ex) { Console.WriteLine("AUDIT FAILED: " + ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length > 0 && args[0] is "--ssid-boot-prepare" or "--ssid-static-boot-prepare" or "--ssid-boot-resume")
{
    try { await WifiSsidBootAudit.Run(args); }
    catch (Exception ex) { Console.WriteLine("AUDIT FAILED: " + ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length > 0 && args[0] == "--ssid-reconnect-roundtrip")
{
    try { await WifiSsidReconnectAudit.Run(args); }
    catch (Exception ex) { Console.WriteLine("AUDIT FAILED: " + ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length > 0 && args[0] == "--ssid-live-roundtrip")
{
    try { await WifiSsidLiveAudit.Run(args); }
    catch (Exception ex) { Console.WriteLine("AUDIT FAILED: " + ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--ssid-read-only-probe")
{
    var probeState = new WifiSsidService().Read(args[1]);
    Console.WriteLine($"Connected SSID: {probeState.Target.DisplaySsid}; policy-managed={probeState.Target.PolicyManaged}");
    foreach (bool ipv6 in new[] { false, true })
    {
        var raw = ipv6 ? probeState.IPv6 : probeState.IPv4;
        var decoded = WifiSsidCodec.Decode(raw, ipv6);
        Console.WriteLine($"IPv{(ipv6 ? 6 : 4)} metadata: {(raw is null ? "absent" : raw.Length + " bytes")}; IP={decoded.AddressMode}; DNS={decoded.DnsMode}");
    }
    Console.WriteLine("Read-only provider probe complete. No settings changed; addresses omitted.");
    return;
}

if (args.Contains("--ssid-native-buffer-check")) { await WifiSsidTests.Run(nativeBuffersOnly: true); return; }

if (args.Contains("--read-only-probe"))
{
    var adapters = await new NetworkSettingsService().ReadAsync();
    Console.WriteLine($"Read-only probe: {adapters.Length} physical Ethernet/Wi-Fi adapters, {adapters.Count(a => a.Status == "Up")} connected. No settings changed; addresses omitted.");
    foreach (var a in adapters.Where(a => a.Status == "Up"))
        Console.WriteLine($"Connected adapter type={a.InterfaceType}: IPv4 read-error={a.IPv4.ReadError}; consistent={a.IPv4.PersistenceConsistent}; DNS count={a.IPv4.Dns.Length}. IPv6 read-error={a.IPv6.ReadError}; consistent={a.IPv6.PersistenceConsistent}.");
    return;
}
if (args.Contains("--print-script")) { Console.Write(NetworkSettingsScript.Common + "\n" + NetworkSettingsScript.Apply + "\n" + NetworkAdditionalService.Script); return; }
int checks = 0;
await WifiSsidTests.Run();
await EthernetPolicyTests.Run();
void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
void Reject(Action action, string message) { try { action(); } catch (ArgumentException) { checks++; return; } catch (InvalidOperationException) { checks++; return; } throw new Exception(message); }
string projectionBefore = "[{\"Id\":\"wifi\",\"protocol\":\"Tcpip\",\"name\":\"EnableDHCP\",\"Kind\":\"DWord\",\"Value\":1}]";
string projectionActive = projectionBefore.Replace("\"Value\":1", "\"Value\":0");
Assert(WifiSsidManualV4Audit.ManualProjectionMatches(projectionBefore, projectionActive, "wifi"), "Only observed Wi-Fi runtime DHCP projection accepted");
Assert(!WifiSsidManualV4Audit.ManualProjectionMatches(projectionBefore, projectionActive, "ethernet"), "Other adapter cannot receive projection exception");
Assert(!WifiSsidManualV4Audit.ManualProjectionMatches(projectionBefore, projectionActive.Replace("DWord", "String"), "wifi"), "Registry kind drift still fails");
Assert(!WifiSsidManualV4Audit.ManualProjectionMatches(projectionBefore, projectionActive.Replace("Tcpip", "Tcpip6"), "wifi"), "IPv6 drift still fails");
var staticRequest = new NetworkFamilySettings { AddressMode = "Manual", Address = "192.0.2.132", Subnet = "255.255.255.0", Gateway = "192.0.2.1" };
var staticRuntime = new NetworkFamilyState { Dhcp = "Disabled", Addresses = [new() { Address = "192.0.2.132", Prefix = 24, Origin = "Manual", State = "Preferred" }], Gateways = ["192.0.2.1"] };
Assert(WifiSsidBootAudit.ManualRuntimeMatches(staticRuntime, staticRequest), "Static boot verification accepts exact effective manual state");
Assert(!WifiSsidBootAudit.ManualRuntimeMatches(staticRuntime with { Dhcp = "Enabled" }, staticRequest), "Matching DHCP reservation alone is not manual verification");
Assert(!WifiSsidBootAudit.ManualRuntimeMatches(staticRuntime with { Gateways = ["192.0.2.2"] }, staticRequest), "Static boot verification rejects wrong gateway");
Assert(!WifiSsidBootAudit.ManualRuntimeMatches(staticRuntime with { Addresses = [staticRuntime.Addresses[0] with { State = "Duplicate" }] }, staticRequest), "Duplicate static address cannot pass");
Assert(!WifiSsidBootAudit.ManualRuntimeMatches(staticRuntime with { Addresses = [staticRuntime.Addresses[0] with { Prefix = 25 }] }, staticRequest), "Static boot verification rejects wrong prefix");
Assert(!WifiSsidBootAudit.ManualRuntimeMatches(staticRuntime with { ReadError = "unreadable" }, staticRequest), "Unreadable static runtime cannot pass");
var v6Runtime = new NetworkFamilyState { Addresses = [new() { Address = "2001:db8::132", Prefix = 64, Origin = "Manual", State = "Preferred" }] };
Assert(WifiSsidManualV6Audit.AddressMatches(v6Runtime, "2001:db8::132", 64, "Manual"), "IPv6 manual evidence matches exact preferred address");
Assert(!WifiSsidManualV6Audit.AddressMatches(v6Runtime, "2001:db8::133", 64, "Manual"), "Different IPv6 address rejected");
Assert(!WifiSsidManualV6Audit.AddressMatches(v6Runtime, "2001:db8::132", 64, "RouterAdvertisement"), "Manual and automatic IPv6 distinguished");
Assert(!WifiSsidManualV6Audit.AddressMatches(v6Runtime with { ReadError = "unreadable" }, "2001:db8::132", 64, "Manual"), "Unreadable IPv6 cannot pass");
string v6Global = projectionBefore.Replace("Tcpip", "Tcpip6");
Assert(WifiSsidManualV6Audit.GlobalMatches(v6Global, v6Global.Replace("\"Value\":1", "\"Value\":0"), "wifi"), "IPv6 projection bounded to selected Wi-Fi");
Assert(!WifiSsidManualV6Audit.GlobalMatches(projectionBefore, projectionActive, "wifi"), "IPv6 test cannot exempt IPv4 changes");
Assert(WifiSsidManualV6Audit.ModeMatches(new() { Dhcp = "Disabled", RouterDiscovery = "Enabled" }, true), "IPv6 static per-SSID runtime retains RA without global mutation");
Assert(WifiSsidManualV6Audit.ModeMatches(new() { Dhcp = "Enabled", RouterDiscovery = "Enabled" }, true), "Windows may retain DHCPv6 after switching; exact Manual address check is still required");
Assert(!WifiSsidManualV6Audit.ModeMatches(new() { Dhcp = "Unknown", RouterDiscovery = "Enabled" }, true), "Unknown IPv6 mode fails closed");
Assert(WifiSsidManualV6Audit.ModeMatches(new() { Dhcp = "Enabled", RouterDiscovery = "Enabled" }, false), "IPv6 recovery requires DHCPv6 and RA");
var automatic4 = new NetworkFamilyState { Dhcp = "Enabled", Addresses = [new() { Address = "192.0.2.2", Prefix = 24, Origin = "Dhcp", State = "Preferred" }], Gateways = ["192.0.2.1"], Dns = ["192.0.2.1"] };
var secondFixture = new NetworkAdapterState { IPv4 = automatic4, IPv6 = new() { Dhcp = "Enabled", RouterDiscovery = "Enabled" } };
Assert(WifiSsidManualV6Audit.SecondMatches(secondFixture, secondFixture, "2001:db8::132"), "Hotspot without IPv6 Internet can prove absence of leaked manual address");
Assert(!WifiSsidManualV6Audit.SecondMatches(secondFixture, secondFixture with { IPv6 = v6Runtime }, "2001:db8::132"), "Leaked manual IPv6 fails SSID isolation");
Assert(!WifiSsidManualV6Audit.SecondMatches(secondFixture, secondFixture with { IPv6 = secondFixture.IPv6 with { Dns = ["2001:db8::53"] } }, "2001:db8::132"), "Second SSID DNS change fails isolation");
Assert(!WifiSsidManualV6Audit.SecondMatches(secondFixture, secondFixture with { IPv6 = secondFixture.IPv6 with { ReadError = "unknown" } }, "2001:db8::132"), "Unknown second SSID IPv6 fails closed");
Assert(!WifiSsidManualV6Audit.SecondMatches(secondFixture, secondFixture with { IPv4 = automatic4 with { Dhcp = "Disabled" } }, "2001:db8::132"), "Unexpected IPv4 mutation fails isolation");
var v6BootOriginal = secondFixture with { IPv6 = v6Runtime with { Dhcp = "Enabled", RouterDiscovery = "Enabled", Addresses = [v6Runtime.Addresses[0] with { Origin = "RouterAdvertisement" }], Gateways = ["fe80::1"], Dns = ["fe80::1"] } };
var v6BootManual = v6BootOriginal with { IPv6 = v6BootOriginal.IPv6 with { Dhcp = "Disabled", Addresses = v6Runtime.Addresses } };
var v6Request = new NetworkFamilySettings { AddressMode = "Manual", Address = "2001:db8::132", Subnet = "64", Gateway = "fe80::1" };
Assert(WifiSsidV6BootAudit.RuntimeMatches(v6BootManual, v6BootOriginal, v6Request, true), "IPv6 boot accepts exact manual runtime with RA retained");
Assert(WifiSsidV6BootAudit.RuntimeMatches(v6BootManual with { IPv6 = v6BootManual.IPv6 with { Dhcp = "Enabled" } }, v6BootOriginal, v6Request, true), "Exact manual address can coexist with DHCPv6 after SSID switch");
Assert(!WifiSsidV6BootAudit.RuntimeMatches(v6BootOriginal, v6BootOriginal, v6Request, true), "Same RA address cannot certify manual boot");
Assert(WifiSsidV6BootAudit.RuntimeMatches(v6BootOriginal, v6BootOriginal, v6Request, false), "IPv6 recovery accepts exact automatic runtime");
Assert(!WifiSsidV6BootAudit.RuntimeMatches(v6BootManual, v6BootOriginal, v6Request, false), "Still manual cannot certify recovery");
Assert(!WifiSsidV6BootAudit.RuntimeMatches(v6BootManual with { IPv6 = v6BootManual.IPv6 with { Gateways = ["fe80::2"] } }, v6BootOriginal, v6Request, true), "Wrong IPv6 gateway fails boot verification");
Assert(!WifiSsidV6BootAudit.RuntimeMatches(v6BootManual with { IPv4 = automatic4 with { Dns = [] } }, v6BootOriginal, v6Request, true), "IPv4 drift fails IPv6 boot verification");
const string previousBoot = "2026-10-11T16:20:42.5000000Z", nextBoot = "2026-10-11T17:00:00.0000000Z";
var bootFixture = new WifiBootCheckpoint(Environment.MachineName, previousBoot, "AwaitTestBoot", "fixture.json", [], "", "");
string baselineId = Guid.NewGuid().ToString("D");
Assert(WifiSsidBootAudit.BaselineAdapterIds("[{\"Id\":\"" + baselineId + "\"},{\"Id\":\"" + baselineId + "\"}]").SequenceEqual(new[] { baselineId }), "Boot comparison preserves original IDs independently of current device enumeration");
try { WifiSsidBootAudit.BaselineAdapterIds("[]"); throw new Exception("Empty boot baseline accepted"); } catch (IOException) { checks++; }
try { WifiSsidBootAudit.BaselineAdapterIds("[{\"Id\":\"invalid/path\"}]"); throw new Exception("Invalid adapter ID accepted"); } catch (FormatException) { checks++; }
foreach (var (checkpoint, boot, shouldPass) in new[] {
    (bootFixture, previousBoot, false), (bootFixture, nextBoot, true),
    (bootFixture with { Phase = "AwaitRestoreBoot" }, nextBoot, true),
    (bootFixture with { Phase = "Complete" }, nextBoot, false),
    (bootFixture with { Phase = "Unknown" }, nextBoot, false),
    (bootFixture with { Machine = Environment.MachineName + "-other" }, nextBoot, false),
    (bootFixture, "2026-10-11T16:20:42.5006090Z", false),
    (bootFixture, "2026-10-11T23:20:42.5000000+07:00", false),
    (bootFixture, "2026-10-11T16:20:42.4990000Z", false),
    (bootFixture, "invalid", false),
    (bootFixture with { Boot = "invalid" }, nextBoot, false),
    (bootFixture, "2026-10-11T15:00:00.0000000Z", false) })
{
    bool accepted = true;
    try { WifiSsidBootAudit.RequireNextBoot(checkpoint, boot); } catch (IOException) { accepted = false; }
    Assert(accepted == shouldPass, "Boot audit rejects same-boot, foreign-machine, completed and unknown checkpoints");
}
string id = Guid.NewGuid().ToString();
var p = new NetworkSettingsProfile { AdapterId = id };
NetworkSettingsValidation.Validate(p);
Assert(!NetworkSettingsValidation.HasChanges(p), "Default is non-mutating");
var dhcpEditor = NetworkEditorPolicy.FromState(new() { Dhcp = "Enabled", DnsAutomatic = true, Addresses = [new() { Address = "192.0.2.20", Prefix = 24, Origin = "Dhcp" }], Gateways = ["192.0.2.1"], Dns = ["192.0.2.53"] }, false);
Assert(dhcpEditor.AddressMode == "Automatic" && dhcpEditor.Address == "192.0.2.20" && dhcpEditor.Subnet == "255.255.255.0" && dhcpEditor.Gateway == "192.0.2.1", "Current DHCP configuration projected into editor");
Assert(dhcpEditor.DnsMode == "Automatic" && dhcpEditor.PreferredDns == "192.0.2.53", "Current automatic DNS shown");
Assert(!NetworkSettingsValidation.HasChanges(p with { IPv4 = NetworkEditorPolicy.Changes(dhcpEditor, dhcpEditor) }), "Loading current data creates no pending changes");
Assert(NetworkEditorPolicy.Changes(dhcpEditor, dhcpEditor with { Address = "192.0.2.30" }).AddressMode == "Unchanged", "DHCP lease display is not a static edit");
var dnsOnly = NetworkEditorPolicy.Changes(dhcpEditor, dhcpEditor with { DnsMode = "Manual", PreferredDns = "192.0.2.54" });
Assert(dnsOnly.AddressMode == "Unchanged" && dnsOnly.DnsMode == "Manual", "DNS-only edit leaves address untouched");
var staticEditor = NetworkEditorPolicy.FromState(new() { Dhcp = "Disabled", Addresses = [new() { Address = "192.0.2.21", Prefix = 25, Origin = "Manual" }], Dns = ["192.0.2.53", "192.0.2.54"] }, false);
Assert(staticEditor.AddressMode == "Manual" && staticEditor.Subnet == "255.255.255.128" && staticEditor.AlternateDns == "192.0.2.54", "Static values and alternate DNS projected");
Assert(NetworkEditorPolicy.Changes(staticEditor, staticEditor with { Gateway = "192.0.2.1" }).AddressMode == "Manual", "Gateway edit is included");
Assert(NetworkEditorPolicy.Changes(staticEditor, staticEditor with { Subnet = "255.255.255.0" }).AddressMode == "Manual", "Subnet edit is included");
Assert(NetworkEditorPolicy.Changes(staticEditor, staticEditor with { AlternateDns = "" }).DnsMode == "Manual", "Clearing alternate DNS is included");
Assert(NetworkEditorPolicy.Changes(staticEditor, staticEditor with { AddressMode = "Automatic" }).AddressMode == "Automatic", "DHCP transition is included");
Assert(NetworkEditorPolicy.Changes(staticEditor, staticEditor with { AddressMode = "Unchanged" }).AddressMode == "Unchanged", "Explicit Unchanged preserves configuration");
var ipv6Editor = NetworkEditorPolicy.FromState(new() { Dhcp = "Disabled", RouterDiscovery = "Disabled", Addresses = [new() { Address = "2001:db8::20", Prefix = 64, Origin = "Manual" }] }, true);
Assert(ipv6Editor.AddressMode == "Manual" && ipv6Editor.Subnet == "64", "IPv6 prefix projected without IPv4 mask conversion");
Assert(NetworkEditorPolicy.FromState(new() { Dhcp = "Enabled", RouterDiscovery = "Enabled" }, true).AddressMode == "Automatic", "IPv6 DHCP plus RA projects Automatic");
Assert(NetworkEditorPolicy.FromState(new() { Dhcp = "Enabled", RouterDiscovery = "Disabled" }, true).AddressMode == "Unchanged", "Mixed IPv6 mode is not simplified");
Assert(NetworkEditorPolicy.FromState(new() { Dhcp = "Enabled", PersistenceConsistent = false }, false).AddressMode == "Unchanged", "Inconsistent state remains unchanged");
Assert(NetworkEditorPolicy.FromState(new() { Dhcp = "Enabled", DnsAutomatic = true, ReadError = "unreadable" }, false) is { AddressMode: "Unchanged", DnsMode: "Unchanged" }, "Read errors cannot fabricate editable baseline");
Assert(NetworkEditorPolicy.FromState(new() { Dns = ["192.0.2.1", "192.0.2.2", "192.0.2.3"] }, false).DnsMode == "Unchanged", "More than two DNS servers are not silently truncated");
Assert(NetworkEditorPolicy.FromState(new() { ReadError = null!, Dhcp = "Enabled", DnsAutomatic = true }, false) is { AddressMode: "Automatic", DnsMode: "Automatic" }, "Successful collector with absent/null error field is readable");
string WifiXml(string ssid) => "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>Office profile</name><SSIDConfig><SSID><hex>" + ssid + "</hex><name>untrusted-display-name</name></SSID></SSIDConfig><MSM><security><sharedKey><keyMaterial>DO-NOT-PERSIST</keyMaterial></sharedKey></security></MSM></WLANProfile>";
var wifiA = WifiProfileInventory.Parse(id, "Office profile", 0, WifiXml("4F6666696365"));
Assert(wifiA.DisplaySsid == "Office" && wifiA.SsidHex == "4F6666696365", "SSID uses byte identity, not profile/display name");
Assert(WifiProfileInventory.Parse(id, "Office profile", 1, WifiXml("4F6666696365")).PolicyManaged, "Managed profile recognized");
Assert(WifiProfileInventory.Parse(id, "Office profile", 0, WifiXml("FF00")).DisplaySsid.StartsWith("SSID hex "), "Non-text SSID has safe hex display");
foreach (string invalid in new[] { "", "GG", new string('A', 66) })
{
    try { WifiProfileInventory.Parse(id, "Office profile", 0, WifiXml(invalid)); throw new Exception("Invalid SSID accepted"); }
    catch (IOException) { checks++; }
}
try { WifiProfileInventory.Parse(id, "Other profile", 0, WifiXml("41")); throw new Exception("Mismatched profile accepted"); } catch (IOException) { checks++; }
try { WifiProfileInventory.Parse(id, "Office profile", 0, "<!DOCTYPE x SYSTEM 'file:///no-such-file'>" + WifiXml("41")); throw new Exception("External XML DTD accepted"); } catch (System.Xml.XmlException) { checks++; }
var wifiDraft = p with { Scope = "WifiSsidDraft", WifiProfileName = wifiA.ProfileName, SsidHex = wifiA.SsidHex };
NetworkSettingsValidation.Validate(wifiDraft); checks++;
Reject(() => NetworkSettingsService.RequireAdapterPlan(wifiDraft), "SSID draft entered global adapter path");
Reject(() => NetworkSettingsValidation.Validate(wifiDraft with { Scope = "Adapter" }), "SSID target silently converted to adapter scope");
Reject(() => NetworkSettingsValidation.Validate(wifiDraft with { SsidHex = "" }), "SSID identity missing");
Reject(() => NetworkSettingsValidation.Validate(wifiDraft with { Category = "Private" }), "Wi-Fi draft category mutation accepted");
string draftJson = JsonSerializer.Serialize(wifiDraft, NetworkSettingsJson.Default.NetworkSettingsProfile);
Assert(!draftJson.Contains("DO-NOT-PERSIST") && !draftJson.Contains("keyMaterial"), "Only target identity retained, not WLAN XML/credentials");
Assert(JsonSerializer.Deserialize(draftJson, NetworkSettingsJson.Default.NetworkSettingsProfile)!.SsidHex == wifiA.SsidHex, "SSID draft byte identity roundtrip");
for (int i = 1; i <= 32; i++) Assert(NetworkSettingsValidation.Prefix(NetworkSettingsService.Mask(i), false) == i, "Mask round trip");
foreach (string bad in new[] { "255.0.255.0", "255.255.128.1", "0.0.0.0", "24", "0xffffff00", "255.255.255", "255.255.255.256" }) Reject(() => NetworkSettingsValidation.Prefix(bad, false), "Bad mask accepted");
foreach (string bad in new[] { "0", "129", "-1", "abc", "64.0" }) Reject(() => NetworkSettingsValidation.Prefix(bad, true), "Bad v6 prefix accepted");
foreach (string bad in new[] { "127.0.0.1", "0.0.0.0", "224.0.0.1", "255.255.255.255", "192.168.001.1", "192.168.1", "10.1", "10.0.0.1;shutdown", "::1" }) Reject(() => NetworkSettingsValidation.Address(bad, false, "IP"), "Bad v4 accepted");
foreach (string bad in new[] { "::", "::1", "ff02::1", "fe80::1%5", "1.2.3.4", "abc" }) Reject(() => NetworkSettingsValidation.Address(bad, true, "IP"), "Bad v6 accepted");
var manual = new NetworkFamilySettings { AddressMode = "Manual", Address = "192.168.50.20", Subnet = "255.255.255.0", Gateway = "192.168.50.1", DnsMode = "Manual", PreferredDns = "1.1.1.1", AlternateDns = "8.8.8.8" };
NetworkSettingsValidation.Validate(p with { IPv4 = manual }); checks++;
foreach (var bad in new[] { manual with { Gateway = "192.168.60.1" }, manual with { Address = "192.168.50.0" }, manual with { Address = "192.168.50.255" }, manual with { Gateway = manual.Address }, manual with { AlternateDns = manual.PreferredDns }, manual with { AddressMode = "invalid" }, manual with { DnsMode = "invalid" } }) Reject(() => NetworkSettingsValidation.Validate(p with { IPv4 = bad }), "Bad profile accepted");
var state = new NetworkAdapterState { Id = id, InterfaceType = 6, Category = "Public", IPv4 = new() { Dhcp = "Enabled", DnsAutomatic = true }, IPv6 = new() { Dhcp = "Enabled", RouterDiscovery = "Enabled", DnsAutomatic = true } };
foreach (string family in new[] { "v4", "v6" })
foreach (string ipMode in new[] { "Unchanged", "Automatic", "Manual" })
foreach (string dnsMode in new[] { "Unchanged", "Automatic", "Manual" })
{
    var settings = family == "v4" ? manual with { AddressMode = ipMode, DnsMode = dnsMode } : new NetworkFamilySettings { AddressMode = ipMode, Address = "2001:db8::20", Subnet = "64", Gateway = "fe80::1", DnsMode = dnsMode, PreferredDns = "2606:4700:4700::1111" };
    var requested = family == "v4" ? p with { IPv4 = settings } : p with { IPv6 = settings };
    var restore = NetworkSettingsService.RestoreProfile(new() { Before = state, Requested = requested });
    var f = family == "v4" ? restore.IPv4 : restore.IPv6;
    Assert(f.AddressMode == (ipMode == "Unchanged" ? "Unchanged" : "Automatic"), "Restore IP only if touched");
    Assert(f.DnsMode == (dnsMode == "Unchanged" ? "Unchanged" : "Automatic"), "Restore DNS only if touched");
    Assert(NetworkSettingsService.VerificationProblem(restore, state) == "", "Automatic original verifies");
}
var staticState = state with { IPv4 = new() { Dhcp = "Disabled", Addresses = [new() { Address = manual.Address, Prefix = 24, Origin = "Manual" }], Gateways = [manual.Gateway], Dns = [manual.PreferredDns, manual.AlternateDns], DnsAutomatic = false } };
Assert(NetworkSettingsService.VerificationProblem(p with { IPv4 = manual }, staticState) == "", "Static read-back matches");
Assert(NetworkSettingsService.VerificationProblem(p with { IPv4 = manual }, staticState with { IPv4 = staticState.IPv4 with { ReadError = null! } }) == "", "Null error marker does not crash successful verification");
Assert(NetworkSettingsService.RestoreProfile(new() { Before = staticState with { IPv4 = staticState.IPv4 with { ReadError = null! } }, Requested = p with { IPv4 = manual } }).IPv4 == manual, "Null error marker does not crash original-state snapshot validation");
Assert(NetworkSettingsService.VerificationProblem(p with { IPv4 = manual }, staticState with { Id = Guid.NewGuid().ToString() }) != "", "Identity mismatch blocked");
Assert(NetworkSettingsService.VerificationProblem(p with { IPv4 = manual }, staticState with { IPv4 = staticState.IPv4 with { Dns = [manual.AlternateDns, manual.PreferredDns] } }) != "", "DNS order checked");
Assert(NetworkSettingsService.VerificationProblem(p with { IPv4 = manual }, staticState with { IPv4 = staticState.IPv4 with { Dhcp = "Enabled" } }) != "", "DHCP mismatch checked");
Assert(NetworkSettingsService.VerificationProblem(p with { IPv4 = manual }, staticState with { IPv4 = staticState.IPv4 with { PersistenceConsistent = false } }) != "", "Persistent state differs");
Assert(NetworkSettingsService.VerificationProblem(p with { IPv4 = manual }, staticState with { IPv4 = staticState.IPv4 with { Addresses = [staticState.IPv4.Addresses[0] with { State = "Duplicate" }] } }) != "", "Duplicate IP is not success");
Reject(() => NetworkSettingsService.RestoreProfile(new() { Before = staticState with { IPv4 = staticState.IPv4 with { PersistenceConsistent = false } }, Requested = p with { IPv4 = manual } }), "Temporary/static-store mismatch must block mutation");
Reject(() => NetworkSettingsService.RestoreProfile(new() { Before = staticState with { IPv4 = staticState.IPv4 with { Addresses = [staticState.IPv4.Addresses[0], staticState.IPv4.Addresses[0]] } }, Requested = p with { IPv4 = manual } }), "Multi-address original must block destructive simplification");
var snapshot = new NetworkSettingsSnapshot { Before = staticState, Requested = p with { IPv4 = manual } };
var roundtrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, NetworkSettingsJson.Default.NetworkSettingsSnapshot), NetworkSettingsJson.Default.NetworkSettingsSnapshot)!;
Assert(NetworkSettingsService.RestoreProfile(roundtrip).IPv4 == manual, "Source-generated snapshot JSON preserves static values");
Assert(!NetworkSettingsScript.Common.Contains("Set-Net") && !NetworkSettingsScript.Common.Contains("Remove-Net"), "Read path contains no network mutation");
Assert(NetworkSettingsScript.Apply.Contains("InterfaceGuid") && NetworkSettingsScript.Apply.Contains("Check-LocalControl"), "Fresh identity and policy preflight");
Console.WriteLine($"PASS: {checks} network settings assertions. No live network mutations.");
foreach (string action in new[] { "printer", "workgroup", "computer", "wins", "route", "drive", "program", "smtp", "wifi", "unknown" }) Reject(() => NetworkAdditionalPolicy.Validate(action, new()), "Blank/unknown additional action accepted: " + action);
NetworkAdditionalPolicy.Validate("smtp", new() { SmtpHost = "smtp.example.com", SmtpPort = 587 }); checks++;
NetworkAdditionalPolicy.Validate("drive", new() { DriveLetter = "Z:", DrivePath = @"\\server\share" }); checks++;
NetworkAdditionalPolicy.Validate("workgroup", new() { Workgroup = "MY-GROUP" }); checks++;
foreach (string input in new[] { "bad input", "1.2.3.4 foo\n<script>", "1.2.3.4 *.example.com", "1.2.3.4 foo # comment" }) Reject(() => NetworkAdditionalPolicy.ValidateHosts(input), "Unsafe hosts entry accepted");
string hosts = "127.0.0.1 localhost\r\n# existing user content\r\n";
string edited = NetworkAdditionalPolicy.MergeHosts(hosts, "192.0.2.1 test.example");
Assert(edited.StartsWith(hosts), "Hosts preserves unrelated user content");
Assert(NetworkAdditionalPolicy.MergeHosts(edited, "192.0.2.2 test.example").Contains("192.0.2.2 test.example"), "Hosts changes only owned block");
Assert(!NetworkAdditionalPolicy.MergeHosts(edited, "").Contains("test.example"), "Blank hosts removes only owned entries");
try { NetworkAdditionalPolicy.MergeHosts(hosts + NetworkAdditionalPolicy.BeginHosts, ""); throw new Exception("Unbalanced hosts markers accepted"); } catch (IOException) { checks++; }

string testRoot = Path.Combine(Path.GetTempPath(), "nwu-network-fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    foreach (int type in new[] { 71, 0, 131 })
    {
        string root = Path.Combine(testRoot, "scope-" + type);
        int writes = 0;
        var scoped = new NetworkSettingsService(root, (_, readOnly) =>
        {
            if (!readOnly) { writes++; throw new Exception("Forbidden adapter-wide mutation reached executor."); }
            return Task.FromResult(JsonSerializer.Serialize(new[] { state with { InterfaceType = type, Status = "Up" } }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
        }, () => true);
        try { await scoped.ApplyAsync(p with { IPv4 = manual }); throw new Exception("Non-Ethernet Apply accepted."); }
        catch (InvalidOperationException) { checks++; }
        Assert(writes == 0 && scoped.Snapshots().Length == 0, "Wi-Fi/unknown blocked before snapshot or write");
        try { await scoped.RequireEthernetAsync(id); throw new Exception("Non-Ethernet automatic activation accepted."); }
        catch (InvalidOperationException) { checks++; }
        Directory.CreateDirectory(scoped.SnapshotDirectory);
        string path = Path.Combine(scoped.SnapshotDirectory, "old-snapshot.json");
        // Even a forged/old Ethernet snapshot cannot override the current adapter's actual type.
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, NetworkSettingsJson.Default.NetworkSettingsSnapshot));
        try { await scoped.RestoreAsync(path); throw new Exception("Non-Ethernet restore accepted."); }
        catch (InvalidOperationException) { checks++; }
        Assert(writes == 0, "Restore rechecks live type before executor");
    }
    Assert(NetworkSettingsScript.Apply.IndexOf("$a.InterfaceType -ne 6", StringComparison.Ordinal) < NetworkSettingsScript.Apply.IndexOf("Set-NetIPInterface", StringComparison.Ordinal), "PowerShell Wi-Fi guard precedes mutation");
    Assert(NetworkAdditionalService.Script.Contains("$action -in @('wins','route') -and $a.InterfaceType -ne 6"), "Wi-Fi WINS/routes blocked in native action script");
    foreach (string scenario in new[] { "success", "apply-failure", "verification-failure", "recovery-failure" })
    {
        string root = Path.Combine(testRoot, scenario);
        NetworkAdapterState current = state with { Status = "Up" };
        int writes = 0;
        NetworkSettingsService simulated = new(root, (script, readOnly) =>
        {
            if (readOnly) return Task.FromResult(JsonSerializer.Serialize(new[] { current }, NetworkSettingsJson.Default.NetworkAdapterStateArray));
            writes++;
            Assert(Directory.GetFiles(Path.Combine(root, "Snapshots"), "*.json").Length == 1, "Durable snapshot exists before every mutation");
            string encoded = System.Text.RegularExpressions.Regex.Match(script, "FromBase64String\\('([^']+)'\\)").Groups[1].Value;
            var requested = JsonSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), NetworkSettingsJson.Default.NetworkSettingsProfile)!;
            if (writes == 1)
            {
                current = staticState with { Status = "Up" };
                if (scenario is "apply-failure" or "recovery-failure") throw new IOException("Simulated partial mutation failure");
                if (scenario == "verification-failure") current = current with { IPv4 = current.IPv4 with { Dns = [] } };
            }
            else
            {
                if (scenario == "recovery-failure") throw new IOException("Simulated restore failure");
                Assert(requested.IPv4.AddressMode == "Automatic" && requested.IPv4.DnsMode == "Automatic", "Recovery uses original modes, not requested settings");
                current = state with { Status = "Up" };
            }
            return Task.FromResult("");
        }, () => true);
        try
        {
            await simulated.ApplyAsync(p with { IPv4 = manual });
            Assert(scenario == "success" && writes == 1, "Only successful read-back is success");
        }
        catch (IOException ex)
        {
            Assert(scenario != "success" && writes == 2, "Failed/unknown apply attempts recovery once");
            Assert(scenario == "recovery-failure" ? ex.Message.Contains("NOT verified") : ex.Message.Contains("restored and read back"), "Recovery outcome is explicit");
        }
        Assert(simulated.Snapshots().Length == 1, "Snapshots retained on success/failure");
    }
    var library = new NetworkSettingsService(Path.Combine(testRoot, "library"));
    library.SaveProfiles([p]); Assert(library.LoadProfiles().Single().Id == p.Id, "Profile library roundtrip");
    File.WriteAllText(library.ProfilesPath, "corrupt");
    try { library.SaveProfiles([p]); throw new Exception("Corrupt library overwritten"); } catch (JsonException) { checks++; }
    Assert(File.ReadAllText(library.ProfilesPath) == "corrupt", "Corrupt profile store preserved");
}
finally { Directory.Delete(testRoot, true); }
Console.WriteLine($"PASS: {checks} total network policy/workflow assertions (simulated Apply/recovery and temporary file fixtures only).");

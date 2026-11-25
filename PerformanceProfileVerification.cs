using System;
using System.Collections.Generic;
using System.Linq;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal readonly record struct ProfilePowerPair(uint Ac, uint Dc)
    {
        public override string ToString() => $"AC {Ac} / DC {Dc}";
    }

    internal readonly record struct ProfileRegistryValue(bool Readable, bool Exists, uint Value)
    {
        public bool Is(uint value) => Readable && Exists && Value == value;
        public bool IsAbsent => Readable && !Exists;
        public override string ToString() => !Readable ? "UNAVAILABLE" : Exists ? Value.ToString() : "<absent>";
    }

    internal readonly record struct ProfileNetworkInterface(
        string Name, ProfileRegistryValue TcpNoDelay, ProfileRegistryValue TcpAckFrequency);
    internal readonly record struct ProfileRscAdapter(string Name, bool Ipv4Enabled, bool Ipv6Enabled);
    internal sealed record ProfileVerificationInput(
        GamingLiveMmcssSnapshot Mmcss,
        string ActivePowerGuid,
        IReadOnlyDictionary<string, ProfilePowerPair?> Power,
        bool BcdReadable,
        string? DynamicTick,
        string? Hpet,
        bool InterfacesReadable,
        IReadOnlyList<ProfileNetworkInterface> Interfaces,
        ProfileRegistryValue GlobalTcpNoDelay,
        ProfileRegistryValue NetworkThrottling,
        ProfileRegistryValue QosReserve,
        ProfileRegistryValue QosTos,
        bool RscReadable,
        IReadOnlyList<ProfileRscAdapter> Rsc);

    internal readonly record struct ProfileVerificationCheck(
        string Name, string Expected, string Actual, bool Pass);
    internal sealed record ProfileVerificationResult(string Profile, IReadOnlyList<ProfileVerificationCheck> Checks)
    {
        public int Matched => Checks.Count(check => check.Pass);
        public int Total => Checks.Count;
        public bool Verified => Total == 23 && Matched == Total;
    }

    internal sealed record PerformanceProfileState(ProfileVerificationResult Best)
    {
        public bool Verified => Best.Verified;
        public string Profile => Verified ? Best.Profile : "Custom";
        public string DisplayText => Verified ? $"{Profile} · Configuration verified ({Best.Matched}/{Best.Total})" : $"Custom · {Best.Matched}/{Best.Total} settings match {Best.Profile}";
    }

    /// <summary>Pure 23-check evaluator matching the final reference profile verifier.</summary>
    internal static class PerformanceProfileVerification
    {
        public static readonly string[] Profiles = { "Competitive Gaming", "Optimized Gaming", "Balanced" };
        public static readonly string[] PowerAliases =
            { "PROCTHROTTLEMIN", "PROCTHROTTLEMAX", "PERFINCPOL", "CPMINCORES", "CPMAXCORES" };

        public static PerformanceProfileState SelectBest(IEnumerable<ProfileVerificationResult> results) =>
            new(results.OrderByDescending(result => result.Matched).First());

        public static ProfileVerificationResult Evaluate(
            string profile, ProfileVerificationInput input, string? targetPowerGuid,
            IReadOnlyDictionary<string, ProfilePowerPair?> expectedPower)
        {
            if (!Profiles.Contains(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
            bool competitive = profile == "Competitive Gaming";
            bool gaming = profile != "Balanced";
            GamingLiveMmcssSnapshot m = input.Mmcss;
            List<ProfileVerificationCheck> checks = new();
            void Add(string name, string expected, string actual, bool pass) =>
                checks.Add(new(name, expected, actual, pass));
            void Number(string name, int expected, int? actual) =>
                Add(name, expected.ToString(), actual?.ToString() ?? "UNAVAILABLE", actual == expected);
            void Text(string name, string expected, string actual) =>
                Add(name, expected, actual, string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase));
            void Power(string name, string alias)
            {
                input.Power.TryGetValue(alias, out ProfilePowerPair? actual);
                expectedPower.TryGetValue(alias, out ProfilePowerPair? expected);
                Add(name, Pair(expected), Pair(actual), actual.HasValue && expected.HasValue && actual == expected);
            }
            Text("MMCSS Profile", profile, m.Profile);
            Number("Win32PrioritySeparation", competitive ? 0x24 : gaming ? 0x18 : 0x02, m.Win32PrioritySeparation);
            Number("SystemResponsiveness", competitive ? 1 : 20, m.SystemResponsiveness);
            Number("Games Priority", profile == "Optimized Gaming" ? 4 : 2, m.GamesPriority);
            Number("GPU Priority", profile == "Optimized Gaming" ? 6 : 8, m.GpuPriority);
            Text("Scheduling Category", competitive ? "High" : "Medium", m.SchedulingCategory);
            Text("SFIO Priority", gaming ? "High" : "Normal", m.SfioPriority);
            Number("Clock Rate", 10000, m.ClockRate);
            Number("NoLazyMode", competitive ? 1 : 0, m.NoLazyMode);
            Number("AlwaysOn", competitive ? 1 : 0, m.AlwaysOn);
            Add("Power Plan", targetPowerGuid ?? "target unavailable", input.ActivePowerGuid,
                Guid.TryParse(targetPowerGuid, out Guid target) &&
                Guid.TryParse(input.ActivePowerGuid, out Guid active) && target == active);
            Power("CPU Min", "PROCTHROTTLEMIN");
            Power("CPU Max", "PROCTHROTTLEMAX");
            Power("PerfIncPolicy", "PERFINCPOL");
            input.Power.TryGetValue("CPMINCORES", out ProfilePowerPair? actualMin);
            input.Power.TryGetValue("CPMAXCORES", out ProfilePowerPair? actualMax);
            expectedPower.TryGetValue("CPMINCORES", out ProfilePowerPair? expectedMin);
            expectedPower.TryGetValue("CPMAXCORES", out ProfilePowerPair? expectedMax);
            Add("Core Parking", $"Min {Pair(expectedMin)}; Max {Pair(expectedMax)}",
                $"Min {Pair(actualMin)}; Max {Pair(actualMax)}",
                actualMin.HasValue && actualMax.HasValue && expectedMin.HasValue && expectedMax.HasValue &&
                actualMin == expectedMin && actualMax == expectedMax);
            Add("Dynamic Tick", competitive ? "Yes" : "DEFAULT / not set",
                input.BcdReadable ? input.DynamicTick ?? "DEFAULT" : "UNAVAILABLE",
                input.BcdReadable && (competitive ? string.Equals(input.DynamicTick, "Yes", StringComparison.OrdinalIgnoreCase) : string.IsNullOrWhiteSpace(input.DynamicTick)));
            Add("HPET", competitive ? "No" : "DEFAULT / not set",
                input.BcdReadable ? input.Hpet ?? "DEFAULT" : "UNAVAILABLE",
                input.BcdReadable && (competitive ? string.Equals(input.Hpet, "No", StringComparison.OrdinalIgnoreCase) : string.IsNullOrWhiteSpace(input.Hpet)));
            bool naglePass = input.InterfacesReadable && (gaming
                ? input.Interfaces.Count > 0 && input.Interfaces.All(item => item.TcpNoDelay.Is(1) && item.TcpAckFrequency.Is(1))
                : input.Interfaces.All(item => item.TcpNoDelay.IsAbsent && item.TcpAckFrequency.IsAbsent));
            Add("Nagle / TCP ACK", gaming ? "TCPNoDelay=1 + TcpAckFrequency=1 on every managed interface" : "Both overrides absent",
                !input.InterfacesReadable ? "UNAVAILABLE" : string.Join("; ", input.Interfaces.Select(item =>
                    $"{item.Name}: {item.TcpNoDelay}/{item.TcpAckFrequency}")), naglePass);
            void Registry(string name, ProfileRegistryValue actual, uint value) =>
                Add(name, gaming ? value.ToString() : "<absent>", actual.ToString(), gaming ? actual.Is(value) : actual.IsAbsent);
            Registry("Global TCP NoDelay", input.GlobalTcpNoDelay, 1);
            Registry("Network Throttling", input.NetworkThrottling, uint.MaxValue);
            Registry("QoS Reserved Bandwidth", input.QosReserve, 0);
            Registry("QoS User TOS / DSCP", input.QosTos, 0);
            // An empty successful enumeration is the reference's non-applicable
            // case. A failed query is not silently treated as an empty result.
            bool rscPass = input.RscReadable && input.Rsc.All(item => competitive
                ? !item.Ipv4Enabled && !item.Ipv6Enabled : item.Ipv4Enabled && item.Ipv6Enabled);
            Add("Receive Segment Coalescing (RSC)", competitive ? "IPv4 OFF / IPv6 OFF" : "IPv4 ON / IPv6 ON",
                !input.RscReadable ? "UNAVAILABLE" : input.Rsc.Count == 0 ? "NOT APPLICABLE" :
                    string.Join("; ", input.Rsc.Select(item => $"{item.Name}: IPv4={item.Ipv4Enabled}, IPv6={item.Ipv6Enabled}")), rscPass);
            return new(profile, checks);
        }

        private static string Pair(ProfilePowerPair? pair) => pair?.ToString() ?? "UNAVAILABLE";
    }
}

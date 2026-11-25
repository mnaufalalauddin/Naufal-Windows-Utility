using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

// Share a pending read across Analyze/retry windows for the same service/item.
// A timeout only stops waiting; it must not spawn another hung Windows worker.
internal static class CatalogStateReader
{
    private static readonly ConditionalWeakTable<IToolToggleService, Dictionary<string, BoundedReadProbe<ToolToggleState>>> Probes = new();

    internal static Task<ToolToggleState> ReadAsync(IToolToggleService service, ToolToggleDefinition definition, TimeSpan timeout)
    {
        var items = Probes.GetValue(service, static _ => new(StringComparer.OrdinalIgnoreCase));
        BoundedReadProbe<ToolToggleState> probe;
        lock (items)
        {
            if (!items.TryGetValue(definition.Id, out probe!)) items[definition.Id] = probe = new();
        }
        return probe.ReadTaskAsync(async () =>
        {
            var state = await service.ReadStateAsync(definition).ConfigureAwait(false);
            return state.IsAvailable ? state with { RecoverySource = service.DescribeRecovery(definition) } : state;
        }, timeout);
    }
}

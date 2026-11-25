using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys;

// These three actions participate in the original Essential bulk catalog.
// Cleanup and settings launchers intentionally remain individual actions.
internal sealed class EssentialBulkActionsService : IToolToggleService
{
    internal static readonly string[] BulkIds = { "IconCache", "NtfsPerformance", "StoragePowerLatency" };
    private readonly IToolActionService _actions;
    private readonly Func<string, Task<ToolToggleState>> _read;
    private readonly Func<string, string> _recovery;
    private readonly Dictionary<string, ToolActionDefinition> _definitions;

    public EssentialBulkActionsService(IToolActionService actions, Func<string, Task<ToolToggleState>> read, Func<string, string> recovery)
    {
        _actions = actions;
        _read = read;
        _recovery = recovery;
        _definitions = BulkIds.ToDictionary(id => id,
            id => actions.GetActions().Single(action => action.Id == id && action.SupportsRestore),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ToolToggleDefinition> GetDefinitions() => _definitions.Values.Select(action =>
        new ToolToggleDefinition(action.Id, action.Category, action.Name, action.Description,
            action.RequiresAdministrator, action.Id == "IconCache" || action.Id == "NtfsPerformance",
            action.Id == "IconCache" ? ToolToggleTier.Safe : ToolToggleTier.Advanced)).ToArray();

    public string DescribeRecovery(ToolToggleDefinition definition) => _recovery(Resolve(definition).Id);

    public Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition) => _read(Resolve(definition).Id);

    public async Task<ToolToggleOperationResult> SetStateAsync(ToolToggleDefinition definition, bool targetOn)
    {
        ToolActionDefinition action = Resolve(definition);
        ToolActionResult result = targetOn ? await _actions.RunAsync(action) : await _actions.RestoreAsync(action);
        ToolToggleState state = await _read(action.Id);
        bool unavailable = result.SkippedUnavailable && state.IsConfirmedUnavailable;
        // The action backend verifies the saved values, which can legitimately
        // still match the applied preset after Restore. OFF is not proof of restore.
        bool verified = result.Success && state.IsAvailable && (!targetOn || state.IsOn);
        return new(verified, verified, result.Message, state, SkippedUnavailable: unavailable);
    }

    private ToolActionDefinition Resolve(ToolToggleDefinition definition) =>
        _definitions.TryGetValue(definition.Id, out var action) ? action : throw new KeyNotFoundException(definition.Id);
}

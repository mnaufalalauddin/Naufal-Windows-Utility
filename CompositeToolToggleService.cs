using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Naufal_Windows_Tech_s_Powertoys
{
    internal sealed class CompositeToolToggleService : IToolToggleService, ICatalogPlanSource
    {
        private readonly IReadOnlyDictionary<string, (IToolToggleService Service, ToolToggleDefinition Definition)> _items;

        public CompositeToolToggleService(params IToolToggleService[] services)
        {
            Dictionary<string, (IToolToggleService, ToolToggleDefinition)> items =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (IToolToggleService service in services)
            {
                foreach (ToolToggleDefinition definition in service.GetDefinitions())
                {
                    if (!items.TryAdd(definition.Id, (service, definition)))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate toggle definition id: {definition.Id}");
                    }
                }
            }
            _items = items;
        }

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() =>
            _items.Values.Select(item => item.Definition).ToArray();

        public IReadOnlyList<CatalogPlanAction> GetPlanActions(ToolToggleDefinition definition)
        {
            var owner = Resolve(definition);
            return CatalogEffectPlan.Expand(owner.Service, owner.Definition);
        }

        public Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition) =>
            Resolve(definition).Service.ReadStateAsync(Resolve(definition).Definition);

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            Resolve(definition).Service.DescribeRecovery(Resolve(definition).Definition);

        public Task<ToolToggleOperationResult> SetStateAsync(
            ToolToggleDefinition definition,
            bool targetOn) =>
            Resolve(definition).Service.SetStateAsync(Resolve(definition).Definition, targetOn);

        public Task<ToolToggleOperationResult> RestoreOriginalAsync(
            ToolToggleDefinition definition) =>
            Resolve(definition).Service.RestoreOriginalAsync(Resolve(definition).Definition);

        public Task<ToolToggleOperationResult> RestoreWindowsDefaultAsync(
            ToolToggleDefinition definition) =>
            Resolve(definition).Service.RestoreWindowsDefaultAsync(Resolve(definition).Definition);

        private (IToolToggleService Service, ToolToggleDefinition Definition) Resolve(
            ToolToggleDefinition definition)
        {
            return _items.TryGetValue(definition.Id, out var item)
                ? item
                : throw new KeyNotFoundException(definition.Id);
        }
    }

    internal sealed class FilteredToolToggleService : IToolToggleService, ICatalogPlanSource
    {
        private readonly IToolToggleService _source;
        private readonly IReadOnlyDictionary<string, ToolToggleDefinition> _definitions;

        public FilteredToolToggleService(
            IToolToggleService source,
            params string[] includedIds)
        {
            _source = source;
            HashSet<string> included = new(includedIds, StringComparer.OrdinalIgnoreCase);
            _definitions = source.GetDefinitions()
                .Where(definition => included.Contains(definition.Id))
                .ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase);

            string[] missing = included
                .Where(id => !_definitions.ContainsKey(id))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Toggle definitions were not found: {string.Join(", ", missing)}");
            }
        }

        public IReadOnlyList<ToolToggleDefinition> GetDefinitions() =>
            _definitions.Values.ToArray();

        public IReadOnlyList<CatalogPlanAction> GetPlanActions(ToolToggleDefinition definition) =>
            CatalogEffectPlan.Expand(_source, Resolve(definition));

        public Task<ToolToggleState> ReadStateAsync(ToolToggleDefinition definition) =>
            _source.ReadStateAsync(Resolve(definition));

        public string DescribeRecovery(ToolToggleDefinition definition) =>
            _source.DescribeRecovery(Resolve(definition));

        public Task<ToolToggleOperationResult> SetStateAsync(
            ToolToggleDefinition definition,
            bool targetOn) =>
            _source.SetStateAsync(Resolve(definition), targetOn);

        public Task<ToolToggleOperationResult> RestoreOriginalAsync(
            ToolToggleDefinition definition) =>
            _source.RestoreOriginalAsync(Resolve(definition));

        public Task<ToolToggleOperationResult> RestoreWindowsDefaultAsync(
            ToolToggleDefinition definition) =>
            _source.RestoreWindowsDefaultAsync(Resolve(definition));

        private ToolToggleDefinition Resolve(ToolToggleDefinition definition) =>
            _definitions.TryGetValue(definition.Id, out ToolToggleDefinition sourceDefinition)
                ? sourceDefinition
                : throw new KeyNotFoundException(definition.Id);
    }

    internal sealed class FilteredToolActionService : IToolActionService
    {
        private readonly IToolActionService _source;
        private readonly IReadOnlyDictionary<string, ToolActionDefinition> _actions;

        public FilteredToolActionService(
            IToolActionService source,
            params string[] includedIds)
        {
            _source = source;
            HashSet<string> included = new(includedIds, StringComparer.OrdinalIgnoreCase);
            _actions = source.GetActions()
                .Where(action => included.Contains(action.Id))
                .ToDictionary(action => action.Id, StringComparer.OrdinalIgnoreCase);

            string[] missing = included
                .Where(id => !_actions.ContainsKey(id))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Tool actions were not found: {string.Join(", ", missing)}");
            }
        }

        public IReadOnlyList<ToolActionDefinition> GetActions() =>
            _actions.Values.ToArray();

        public Task<ToolActionResult> RunAsync(ToolActionDefinition definition) =>
            _source.RunAsync(Resolve(definition));

        public Task<ToolActionResult> RestoreAsync(ToolActionDefinition definition) =>
            _source.RestoreAsync(Resolve(definition));

        private ToolActionDefinition Resolve(ToolActionDefinition definition) =>
            _actions.TryGetValue(definition.Id, out ToolActionDefinition sourceDefinition)
                ? sourceDefinition
                : throw new KeyNotFoundException(definition.Id);
    }
}

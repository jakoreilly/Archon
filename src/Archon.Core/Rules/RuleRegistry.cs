using System.Reflection;

namespace Archon.Core.Rules;

/// <summary>A descriptor together with the rule that produces it.</summary>
public sealed record RegisteredRule(RuleDescriptor Descriptor, IRule Rule, string PackName);

/// <summary>
/// The single place every rule is known. Built-in packs are registered by the host; additional
/// packs are loaded from assembly paths named in configuration, which is how a private or
/// organisation-specific rule set is added without modifying or rebuilding this suite.
/// </summary>
public sealed class RuleRegistry
{
    private readonly Dictionary<string, RegisteredRule> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IRule> _rules = new();
    private readonly List<string> _loadDiagnostics = new();

    /// <summary>Distinct rule instances, each of which may report several ids.</summary>
    public IReadOnlyList<IRule> Rules => _rules;

    /// <summary>Every reportable condition across every registered rule.</summary>
    public IReadOnlyCollection<RegisteredRule> Descriptors => _byId.Values;

    /// <summary>Messages describing packs that could not be loaded. Loading never throws.</summary>
    public IReadOnlyList<string> LoadDiagnostics => _loadDiagnostics;

    public void Add(IRulePack pack)
    {
        foreach (IRule rule in pack.CreateRules())
        {
            if (rule.Descriptors.Count == 0)
            {
                _loadDiagnostics.Add($"A rule in pack '{pack.Name}' declares no descriptors and was ignored.");
                continue;
            }

            string? conflicting = rule.Descriptors.FirstOrDefault(d => _byId.ContainsKey(d.Id))?.Id;
            if (conflicting is not null)
            {
                _loadDiagnostics.Add($"Rule id '{conflicting}' from pack '{pack.Name}' was ignored: that id is already registered.");
                continue;
            }

            foreach (RuleDescriptor descriptor in rule.Descriptors)
            {
                _byId[descriptor.Id] = new RegisteredRule(descriptor, rule, pack.Name);
            }
            _rules.Add(rule);
        }
    }

    public RegisteredRule? Find(string ruleId) => _byId.GetValueOrDefault(ruleId);

    public IEnumerable<IRule> ByScope(RuleScope scope) => _rules.Where(r => r.Scope == scope);

    /// <summary>
    /// Loads every <see cref="IRulePack"/> implementation from an assembly on disk. A pack that
    /// cannot be loaded is recorded in <see cref="LoadDiagnostics"/> and skipped, so one bad
    /// external pack degrades to a warning rather than preventing analysis.
    /// </summary>
    public void AddFromAssembly(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            _loadDiagnostics.Add($"Rule pack '{assemblyPath}' was not found.");
            return;
        }

        try
        {
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
            var packTypes = assembly.GetTypes()
                .Where(t => typeof(IRulePack).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                .ToList();

            if (packTypes.Count == 0)
            {
                _loadDiagnostics.Add($"Rule pack '{assemblyPath}' contains no IRulePack implementation.");
                return;
            }

            foreach (Type packType in packTypes)
            {
                if (Activator.CreateInstance(packType) is IRulePack pack)
                {
                    Add(pack);
                }
            }
        }
        catch (Exception ex)
        {
            _loadDiagnostics.Add($"Rule pack '{assemblyPath}' failed to load: {ex.Message}");
        }
    }
}

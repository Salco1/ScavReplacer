namespace ScavReplacer;

public sealed class ScavReplacerConfig
{
    public bool Enabled { get; init; } = true;

    // Replace these spawn types (case-insensitive)
    public List<string> FromWildSpawnTypes { get; init; } = new() { "assault", "marksman" };

    // Target spawn type (case-insensitive), e.g. "pmcBot" (Raider) or "exUsec" (Rogue)
    public string ToWildSpawnType { get; init; } = "pmcBot";

    // Patch switches
    public bool PatchWaves { get; init; } = true;
    public bool PatchMinMaxBots { get; init; } = true;

    // Optional map filtering (Locations.* property names, e.g. Shoreline, Bigmap)
    public List<string> OnlyMaps { get; init; } = new();
    public List<string> ExcludeMaps { get; init; } = new();
}

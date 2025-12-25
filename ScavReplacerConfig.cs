namespace ScavReplacer;

public sealed class ScavReplacerConfig
{
    public bool Enabled { get; init; } = true;
    public List<string> FromWildSpawnTypes { get; init; } = new() { "assault", "marksman" };
    public string ToWildSpawnType { get; init; } = "pmcBot";

    public List<string> OnlyMaps { get; init; } = new();
    public List<string> ExcludeMaps { get; init; } = new();

    public bool PatchWaves { get; init; } = true;
    public bool PatchMinMaxBots { get; init; } = true;

    public bool DeepPatchEnabled { get; init; } = true;
    public bool PatchDictionaryKeys { get; init; } = true;

    public bool RoutePatchingEnabled { get; init; } = true;
    public bool PatchOnLocalStart { get; init; } = true;
    public bool PatchOnRaidConfiguration { get; init; } = true;
    public bool PatchOnLocalEnd { get; init; } = true;

    public bool PatchOnBotGenerate { get; init; } = true;
    public bool DebugDump { get; init; } = false;
}

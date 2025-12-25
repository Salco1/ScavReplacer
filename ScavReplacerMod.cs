using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace ScavReplacer;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 900000)]
public sealed class ScavReplacerMod(
    DatabaseService databaseService,
    ModHelper modHelper,
    ILogger<ScavReplacerMod> logger
) : IOnLoad
{
    private static readonly object StateLock = new();

    private static ScavReplacerConfig _config = new();
    private static HashSet<string> _fromSet = new(StringComparer.OrdinalIgnoreCase);
    private static string _toValue = "pmcBot";
    private static string _modRoot = string.Empty;
    private static DatabaseService? _db;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static readonly string[] SpawnTypePropertyNames =
    {
        "WildSpawnType",
        "Role",
        "SpawnType",
        "BotType",
        "BotRole",
        "BossName",
        "BossEscortType"
    };

    private static readonly HashSet<string> SpawnContainerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Waves",
        "MinMaxBots",
        "BossLocationSpawn",
        "BossLocationSpawnInfo"
    };

    public Task OnLoad()
    {
        var config = LoadConfig(out var modRoot);
        var fromSet = new HashSet<string>(config.FromWildSpawnTypes ?? new(), StringComparer.OrdinalIgnoreCase);
        var toValue = (config.ToWildSpawnType ?? "pmcBot").Trim();

        if (string.IsNullOrWhiteSpace(toValue))
            toValue = "pmcBot";

        if (fromSet.Count == 0)
        {
            fromSet.Add("assault");
            fromSet.Add("marksman");
        }

        lock (StateLock)
        {
            _config = config;
            _fromSet = fromSet;
            _toValue = toValue;
            _modRoot = modRoot;
            _db = databaseService;
        }

        if (config.Enabled)
        {
            var startup = Patcher.PatchLocations(databaseService, config, fromSet, toValue);
            if (config.DebugDump)
                Patcher.WriteDebugSummary(modRoot, "Startup", config, fromSet, toValue, startup);
        }

        logger.LogInformation("[SALCO'S ScavReplacer successfully loaded]");
        return Task.CompletedTask;
    }

    private ScavReplacerConfig LoadConfig(out string modRoot)
    {
        modRoot = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        try
        {
            var cfgDirA = Path.Combine(modRoot, "config");
            var cfgDirB = Path.Combine(modRoot, "Config");

            var jsoncA = Path.Combine(cfgDirA, "config.jsonc");
            var jsonA = Path.Combine(cfgDirA, "config.json");
            var jsoncB = Path.Combine(cfgDirB, "config.jsonc");
            var jsonB = Path.Combine(cfgDirB, "config.json");

            var cfgPath =
                File.Exists(jsoncA) ? jsoncA :
                File.Exists(jsonA) ? jsonA :
                File.Exists(jsoncB) ? jsoncB :
                File.Exists(jsonB) ? jsonB :
                null;

            if (cfgPath is null)
                return new ScavReplacerConfig();

            var raw = File.ReadAllText(cfgPath);
            return JsonSerializer.Deserialize<ScavReplacerConfig>(raw, JsonOptions) ?? new ScavReplacerConfig();
        }
        catch
        {
            return new ScavReplacerConfig();
        }
    }

    internal static class Patcher
    {
        internal sealed record PatchResult(int TotalReplaced, Dictionary<string, int> ReplacedByMap);

        internal static PatchResult PatchLocations(
            DatabaseService databaseService,
            ScavReplacerConfig config,
            HashSet<string> fromSet,
            string toValue,
            string? overrideMap = null
        )
        {
            var replacedByMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var total = 0;

            var locationsRoot = databaseService.GetLocations();
            var locationsType = locationsRoot.GetType();

            foreach (var mapProp in locationsType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var mapName = mapProp.Name;

                if (mapName.Equals("Base", StringComparison.OrdinalIgnoreCase) ||
                    mapName.Equals("ExtensionData", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(overrideMap) &&
                    !mapName.Equals(overrideMap, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (config.OnlyMaps is { Count: > 0 } && !config.OnlyMaps.Contains(mapName, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (config.ExcludeMaps is { Count: > 0 } && config.ExcludeMaps.Contains(mapName, StringComparer.OrdinalIgnoreCase))
                    continue;

                object? locationObj;
                try
                {
                    locationObj = mapProp.GetValue(locationsRoot);
                }
                catch
                {
                    continue;
                }

                if (locationObj is null)
                    continue;

                var baseObj = GetPropValueCI(locationObj, "Base") ?? locationObj;

                var replaced = 0;

                if (config.PatchWaves)
                    replaced += PatchContainer(baseObj, "Waves", config, fromSet, toValue);

                if (config.PatchMinMaxBots)
                    replaced += PatchContainer(baseObj, "MinMaxBots", config, fromSet, toValue);

                if (config.DeepPatchEnabled)
                {
                    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    PatchAny(baseObj, "Base", config, fromSet, toValue, visited, ref replaced);
                }

                if (replaced > 0)
                {
                    replacedByMap[mapName] = replaced;
                    total += replaced;
                }
            }

            return new PatchResult(total, replacedByMap);
        }

        internal static int PatchBotGenerateJson(ref string outputJson, HashSet<string> fromSet, string toValue)
        {
            if (string.IsNullOrWhiteSpace(outputJson))
                return 0;

            var trimmed = outputJson.TrimStart();
            if (!(trimmed.StartsWith('{') || trimmed.StartsWith('[')))
                return 0;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(outputJson);
            }
            catch
            {
                return 0;
            }

            if (root is null)
                return 0;

            var replaced = 0;
            PatchJsonNode(root, fromSet, toValue, ref replaced);

            if (replaced > 0)
            {
                try
                {
                    outputJson = root.ToJsonString();
                }
                catch
                {
                    return 0;
                }
            }

            return replaced;
        }

        internal static void WriteDebugSummary(string modRoot, string tag, ScavReplacerConfig config, HashSet<string> fromSet, string toValue, PatchResult patch)
        {
            try
            {
                var dbgDir = Path.Combine(modRoot, "_debug");
                Directory.CreateDirectory(dbgDir);
                var path = Path.Combine(dbgDir, "patched_summary.txt");

                var lines = new List<string>
                {
                    "============================================================",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {tag}",
                    $"From=[{string.Join(", ", fromSet)}] To=[{toValue}]",
                    $"TotalReplaced={patch.TotalReplaced}"
                };

                if (patch.ReplacedByMap.Count == 0)
                {
                    lines.Add("(no map changes)");
                }
                else
                {
                    foreach (var kv in patch.ReplacedByMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                        lines.Add($"{kv.Key}: replaced {kv.Value}");
                }

                lines.Add(string.Empty);
                File.AppendAllLines(path, lines);
            }
            catch
            {
            }
        }

        private static void PatchJsonNode(JsonNode node, HashSet<string> fromSet, string toValue, ref int replaced)
        {
            if (node is JsonObject obj)
            {
                foreach (var kv in obj.ToList())
                {
                    var key = kv.Key;
                    var val = kv.Value;

                    if (val is null)
                        continue;

                    if (IsSpawnKey(key) && val is JsonValue jv)
                    {
                        if (TryGetString(jv, out var s) && fromSet.Contains(s))
                        {
                            obj[key] = toValue;
                            replaced++;
                            continue;
                        }
                    }

                    PatchJsonNode(val, fromSet, toValue, ref replaced);
                }

                return;
            }

            if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is null)
                        continue;

                    PatchJsonNode(item, fromSet, toValue, ref replaced);
                }
            }
        }

        private static bool TryGetString(JsonValue value, out string s)
        {
            s = string.Empty;
            try
            {
                var v = value.GetValue<object?>();
                if (v is string str)
                {
                    s = str;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSpawnKey(string key)
            => SpawnTypePropertyNames.Contains(key, StringComparer.OrdinalIgnoreCase);

        private static int PatchContainer(object baseObj, string containerName, ScavReplacerConfig config, HashSet<string> fromSet, string toValue)
        {
            var container = GetPropValueCI(baseObj, containerName);
            if (container is null)
                return 0;

            var replaced = 0;

            if (container is IDictionary dict)
            {
                if (config.PatchDictionaryKeys)
                    PatchDictionaryKeys(dict, containerName, fromSet, toValue, ref replaced);

                foreach (DictionaryEntry de in dict)
                {
                    if (de.Value is null)
                        continue;

                    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    PatchAny(de.Value, containerName, config, fromSet, toValue, visited, ref replaced);
                }

                return replaced;
            }

            if (container is IEnumerable enumerable && container is not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is null)
                        continue;

                    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    PatchAny(item, containerName, config, fromSet, toValue, visited, ref replaced);
                }

                return replaced;
            }

            {
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                PatchAny(container, containerName, config, fromSet, toValue, visited, ref replaced);
                return replaced;
            }
        }

        private static void PatchAny(object node, string contextName, ScavReplacerConfig config, HashSet<string> fromSet, string toValue, HashSet<object> visited, ref int replaced)
        {
            if (node is null)
                return;

            if (node is string)
                return;

            var type = node.GetType();
            if (type.IsValueType)
                return;

            if (!visited.Add(node))
                return;

            replaced += TryPatchSpawnTypeProperties(node, fromSet, toValue);

            if (node is IDictionary dict)
            {
                if (config.PatchDictionaryKeys)
                    PatchDictionaryKeys(dict, contextName, fromSet, toValue, ref replaced);

                foreach (DictionaryEntry de in dict)
                {
                    if (de.Value is null)
                        continue;

                    PatchAny(de.Value, contextName, config, fromSet, toValue, visited, ref replaced);
                }

                return;
            }

            if (node is IEnumerable enumerable && node is not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is null)
                        continue;

                    PatchAny(item, contextName, config, fromSet, toValue, visited, ref replaced);
                }

                return;
            }

            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!prop.CanRead)
                    continue;

                if (prop.GetIndexParameters().Length > 0)
                    continue;

                object? value;
                try
                {
                    value = prop.GetValue(node);
                }
                catch
                {
                    continue;
                }

                if (value is null)
                    continue;

                if (value is string)
                    continue;

                if (value.GetType().IsValueType)
                    continue;

                PatchAny(value, prop.Name, config, fromSet, toValue, visited, ref replaced);
            }
        }

        private static void PatchDictionaryKeys(IDictionary dict, string contextName, HashSet<string> fromSet, string toValue, ref int replaced)
        {
            var keysToChange = new List<string>();

            foreach (DictionaryEntry de in dict)
            {
                if (de.Key is not string key)
                    continue;

                if (!fromSet.Contains(key))
                    continue;

                if (SpawnContainerNames.Contains(contextName) || (de.Value is not null && HasAnySpawnTypeProperty(de.Value)))
                    keysToChange.Add(key);
            }

            if (keysToChange.Count == 0)
                return;

            foreach (var oldKey in keysToChange)
            {
                try
                {
                    if (dict.Contains(toValue))
                    {
                        dict.Remove(oldKey);
                    }
                    else
                    {
                        var val = dict[oldKey];
                        dict.Remove(oldKey);
                        dict[toValue] = val;
                    }

                    replaced++;
                }
                catch
                {
                }
            }
        }

        private static bool HasAnySpawnTypeProperty(object obj)
        {
            var t = obj.GetType();
            foreach (var propName in SpawnTypePropertyNames)
            {
                var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (p is null || !p.CanRead)
                    continue;

                return true;
            }

            return false;
        }

        private static int TryPatchSpawnTypeProperties(object entry, HashSet<string> fromSet, string toValue)
        {
            var t = entry.GetType();
            var replaced = 0;

            foreach (var propName in SpawnTypePropertyNames)
            {
                var prop = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (prop is null || !prop.CanRead)
                    continue;

                object? current;
                try
                {
                    current = prop.GetValue(entry);
                }
                catch
                {
                    continue;
                }

                if (current is null)
                    continue;

                var currentName = current.ToString() ?? string.Empty;
                if (!fromSet.Contains(currentName))
                    continue;

                if (!TryBuildAssignment(prop.PropertyType, toValue, out var assignment))
                    continue;

                if (TrySetProperty(entry, prop, assignment))
                    replaced++;
            }

            return replaced;
        }

        private static bool TryBuildAssignment(Type targetType, string toValue, out object assignment)
        {
            assignment = toValue;

            if (targetType == typeof(string))
            {
                assignment = toValue;
                return true;
            }

            if (targetType.IsEnum)
            {
                try
                {
                    assignment = Enum.Parse(targetType, toValue, ignoreCase: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying is not null && underlying.IsEnum)
            {
                try
                {
                    assignment = Enum.Parse(underlying, toValue, ignoreCase: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (targetType == typeof(object))
            {
                assignment = toValue;
                return true;
            }

            return false;
        }

        private static bool TrySetProperty(object target, PropertyInfo prop, object value)
        {
            if (!prop.CanWrite)
                return false;

            try
            {
                prop.SetValue(target, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object? GetPropValueCI(object obj, string propName)
        {
            var t = obj.GetType();
            var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (p is null || !p.CanRead)
                return null;

            try
            {
                return p.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    [Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 900000)]
    public sealed class ScavReplacerStaticRouters(JsonUtil jsonUtil) : StaticRouter(jsonUtil, GetCustomRoutes())
    {
        private static List<RouteAction> GetCustomRoutes()
        {
            return
            [
                new RouteAction("/client/match/local/start",
                    (url, info, sessionId, output) =>
                    {
                        PatchNow(info, patchLocalStart: true, patchRaidConfig: false, patchLocalEnd: false);
                        return new ValueTask<object>(output);
                    },
                    typeof(StartLocalRaidRequestData)
                ),
                new RouteAction("/client/raid/configuration",
                    (url, info, sessionId, output) =>
                    {
                        PatchNow(null, patchLocalStart: false, patchRaidConfig: true, patchLocalEnd: false);
                        return new ValueTask<object>(output);
                    }
                ),
                new RouteAction("/client/match/local/end",
                    (url, info, sessionId, output) =>
                    {
                        PatchNow(null, patchLocalStart: false, patchRaidConfig: false, patchLocalEnd: true);
                        return new ValueTask<object>(output);
                    }
                ),
                new RouteAction("/client/game/bot/generate",
                    (url, info, sessionId, output) =>
                    {
                        var cfg = GetState(out var fromSet, out var toValue, out var modRoot, out var db);
                        if (cfg is null || db is null)
                            return new ValueTask<object>(output);

                        if (!cfg.Enabled || !cfg.PatchOnBotGenerate)
                            return new ValueTask<object>(output);

                        var mutable = output;
                        var replaced = Patcher.PatchBotGenerateJson(ref mutable, fromSet, toValue);

                        if (replaced > 0 && cfg.DebugDump)
                        {
                            var patch = new Patcher.PatchResult(replaced, new Dictionary<string, int>());
                            Patcher.WriteDebugSummary(modRoot, "BotGenerate", cfg, fromSet, toValue, patch);
                        }

                        return new ValueTask<object>(replaced > 0 ? (object)mutable : output);
                    }
                )
            ];
        }

        private static void PatchNow(object? info, bool patchLocalStart, bool patchRaidConfig, bool patchLocalEnd)
        {
            var cfg = GetState(out var fromSet, out var toValue, out _, out var db);
            if (cfg is null || db is null)
                return;

            if (!cfg.Enabled || !cfg.RoutePatchingEnabled)
                return;

            if (patchLocalStart && !cfg.PatchOnLocalStart)
                return;

            if (patchRaidConfig && !cfg.PatchOnRaidConfiguration)
                return;

            if (patchLocalEnd && !cfg.PatchOnLocalEnd)
                return;

            string? mapOverride = null;
            if (cfg.OnlyMaps is { Count: 1 })
            {
                mapOverride = cfg.OnlyMaps[0];
            }
            else if (info is StartLocalRaidRequestData start && !string.IsNullOrWhiteSpace(start.Location))
            {
                mapOverride = start.Location;
            }

            var patch = Patcher.PatchLocations(db, cfg, fromSet, toValue, mapOverride);
            if (cfg.DebugDump && patch.TotalReplaced > 0)
                Patcher.WriteDebugSummary(_modRoot, "RoutePatch", cfg, fromSet, toValue, patch);
        }

        private static ScavReplacerConfig? GetState(out HashSet<string> fromSet, out string toValue, out string modRoot, out DatabaseService? db)
        {
            lock (StateLock)
            {
                fromSet = new HashSet<string>(_fromSet, StringComparer.OrdinalIgnoreCase);
                toValue = _toValue;
                modRoot = _modRoot;
                db = _db;
                return _config;
            }
        }
    }
}

using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Services;

namespace ScavReplacer;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public sealed class ScavReplacerMod(
    DatabaseService databaseService,
    ModHelper modHelper,
    ILogger<ScavReplacerMod> logger
) : IOnLoad
{
    // Allow JSONC and trailing commas for readable configs.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // Only patch well-known spawn type keys.
    private static readonly string[] SpawnTypePropertyNames =
    {
        "WildSpawnType",
        "Role",
        "SpawnType"
    };

    public Task OnLoad()
    {
        var config = LoadConfig();
        if (!config.Enabled)
            return Task.CompletedTask;

        var fromSet = new HashSet<string>(config.FromWildSpawnTypes, StringComparer.OrdinalIgnoreCase);

        logger.LogInformation(
            "[SALCO'S ScavReplacer] Patching spawns. From=[{From}] To=[{To}]",
            string.Join(", ", fromSet),
            config.ToWildSpawnType
        );

        var locationsRoot = databaseService.GetLocations();
        var locationsType = locationsRoot.GetType();

        foreach (var mapProp in locationsType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var mapName = mapProp.Name;

            // Skip non-map properties commonly present on the locations root object.
            if (mapName.Equals("Base", StringComparison.OrdinalIgnoreCase) ||
                mapName.Equals("ExtensionData", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (config.OnlyMaps.Count > 0 && !config.OnlyMaps.Contains(mapName, StringComparer.OrdinalIgnoreCase))
                continue;

            if (config.ExcludeMaps.Contains(mapName, StringComparer.OrdinalIgnoreCase))
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

            if (config.PatchWaves)
                PatchContainer(baseObj, "Waves", fromSet, config.ToWildSpawnType);

            if (config.PatchMinMaxBots)
                PatchContainer(baseObj, "MinMaxBots", fromSet, config.ToWildSpawnType);
        }

        return Task.CompletedTask;
    }

    private ScavReplacerConfig LoadConfig()
    {
        try
        {
            var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            var cfgPath = Path.Combine(modPath, "config", "config.jsonc");

            if (!File.Exists(cfgPath))
                return new ScavReplacerConfig();

            var json = File.ReadAllText(cfgPath);

            return JsonSerializer.Deserialize<ScavReplacerConfig>(json, JsonOptions)
                   ?? new ScavReplacerConfig();
        }
        catch
        {
            return new ScavReplacerConfig();
        }
    }

    private static void PatchContainer(
        object baseObj,
        string containerName,
        HashSet<string> fromSet,
        string toValue
    )
    {
        var container = GetPropValueCI(baseObj, containerName);
        if (container is null)
            return;

        // Dictionary-like container: patch values.
        if (container is IDictionary dict)
        {
            foreach (DictionaryEntry de in dict)
            {
                if (de.Value is null)
                    continue;

                PatchEntry(de.Value, fromSet, toValue);
            }

            return;
        }

        // List-like container: patch each item.
        if (container is IEnumerable enumerable && container is not string)
        {
            foreach (var item in enumerable)
            {
                if (item is null)
                    continue;

                PatchEntry(item, fromSet, toValue);
            }
        }
    }

    private static void PatchEntry(
        object entry,
        HashSet<string> fromSet,
        string toValue
    )
    {
        var t = entry.GetType();

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

            TrySetProperty(entry, prop, assignment);
        }
    }

    // Build a correctly-typed value for string / enum / Nullable<enum> / object.
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
                var parsed = Enum.Parse(underlying, toValue, ignoreCase: true);
                assignment = Activator.CreateInstance(targetType, parsed)!; // Nullable<T>(T value)
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
        try
        {
            var setMethod = prop.GetSetMethod(nonPublic: true);
            if (setMethod is null)
                return false;

            setMethod.Invoke(target, new[] { value });
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
}

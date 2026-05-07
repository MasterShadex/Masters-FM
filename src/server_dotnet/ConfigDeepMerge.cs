using System.Text.Json;
using System.Text.Json.Nodes;

namespace MastersFM.Server;

/// <summary>
/// Deep-merge utility ported from server.js:121-135.
///
/// Semantics (MUST match server.js exactly):
///   - For each key in source:
///       - If key NOT in target: target[key] = source[key]  (new key from source)
///       - If both values are non-null, non-array JSON objects: recurse
///       - Else: target value wins -- no overwrite
///   - Arrays are NEVER element-merged; whole-array values are kept from target.
///
/// Direction: TARGET wins for existing keys. SOURCE fills in MISSING keys only.
/// Usage for loading defaults into existing config: Merge(existingConfig, defaults)
/// The same function with swapped arguments gives "source wins" (not used here).
/// </summary>
internal static class ConfigDeepMerge
{
    /// <summary>
    /// Merges <paramref name="source"/> into <paramref name="target"/>, returning a new
    /// JsonObject. Neither input is mutated. Target values win; source fills missing keys.
    /// </summary>
    public static JsonObject Merge(JsonObject target, JsonObject source)
    {
        // Clone target so we never mutate the input.
        var result = JsonNode.Parse(target.ToJsonString())!.AsObject();

        foreach (var kvp in source)
        {
            var key    = kvp.Key;
            var srcVal = kvp.Value;

            if (!result.ContainsKey(key))
            {
                // New key present in source but not in target: add it (server.js: out[k] = source[k])
                result[key] = srcVal?.DeepClone();
            }
            else
            {
                var tgtVal = result[key];

                // Both must be non-null, non-array objects to recurse (server.js condition)
                bool bothObjects =
                    tgtVal is JsonObject tgtObj
                    && srcVal is JsonObject srcObj
                    && tgtObj.GetValueKind() == JsonValueKind.Object
                    && srcObj.GetValueKind() == JsonValueKind.Object;

                if (bothObjects)
                {
                    // Recurse on nested objects (server.js: out[k] = deepMergeConfig(target[k], source[k]))
                    result[key] = Merge((JsonObject)tgtVal!, (JsonObject)srcVal!);
                }
                // else: target value wins -- no change (server.js: implicit)
            }
        }

        return result;
    }
}

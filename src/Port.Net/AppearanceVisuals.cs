using System.Collections;

namespace Port.Net;

/// <summary>
/// Thin PC Appearance / GenericVisualizer stand-in for ghost observe.
/// Mutates Sprite layer visibility/state by map key — never wipes the YAML stack.
/// </summary>
public static class AppearanceVisuals
{
    public static bool TryExtract(object state, out Dictionary<string, string> visuals)
    {
        visuals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tn = state.GetType().Name;
        if (!tn.Contains("Appearance", StringComparison.OrdinalIgnoreCase))
            return false;

        var t = state.GetType();
        // AppearanceComponentState often exposes Data / AppearanceData / Visuals as IDictionary.
        foreach (var name in new[] { "AppearanceData", "Data", "Visuals", "ComponentData" })
        {
            var bag = t.GetProperty(name)?.GetValue(state)
                      ?? t.GetField(name)?.GetValue(state);
            if (bag is null) continue;
            if (TryReadBag(bag, visuals) && visuals.Count > 0)
                return true;
        }

        // Some forks ship Appearance as flat key/value properties.
        foreach (var prop in t.GetProperties())
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            var v = prop.GetValue(state);
            if (v is null || v is string { Length: > 64 }) continue;
            if (v is bool or Enum || v.GetType().IsPrimitive || v is string)
                visuals[prop.Name] = v.ToString() ?? "";
        }

        return visuals.Count > 0;
    }

    static bool TryReadBag(object bag, Dictionary<string, string> dest)
    {
        if (bag is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var k = entry.Key?.ToString();
                if (string.IsNullOrWhiteSpace(k)) continue;
                dest[NormalizeKey(k)] = entry.Value?.ToString() ?? "";
            }
            return dest.Count > 0;
        }

        // KeyValuePair enumerable / custom Appearance data wrappers.
        if (bag is IEnumerable en && bag is not string)
        {
            foreach (var item in en)
            {
                if (item is null) continue;
                var it = item.GetType();
                var key = it.GetProperty("Key")?.GetValue(item)
                          ?? it.GetProperty("Item1")?.GetValue(item)
                          ?? it.GetField("Key")?.GetValue(item);
                var val = it.GetProperty("Value")?.GetValue(item)
                          ?? it.GetProperty("Item2")?.GetValue(item)
                          ?? it.GetField("Value")?.GetValue(item);
                var ks = key?.ToString();
                if (string.IsNullOrWhiteSpace(ks)) continue;
                dest[NormalizeKey(ks)] = val?.ToString() ?? "";
            }
            return dest.Count > 0;
        }

        return false;
    }

    static string NormalizeKey(string key)
    {
        // "enum.DoorVisuals.State" / "DoorVisuals.State" / "State"
        var s = key.Trim().Trim('"', '\'');
        if (s.StartsWith("enum.", StringComparison.OrdinalIgnoreCase))
            s = s[5..];
        return s;
    }

    /// <summary>
    /// Apply Appearance + door state onto an existing prototype/network layer stack.
    /// </summary>
    public static void ApplyToSprite(
        GameStateDecoder.SpriteVisual visual,
        Dictionary<string, string>? appearance,
        string? doorState)
    {
        if (visual.Layers.Count == 0)
            return;

        appearance ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var powered = IsTruthy(Find(appearance, "ComputerVisuals.Powered", "Powered", "PowerDeviceVisuals.Powered"));
        var latheRunning = IsTruthy(Find(appearance, "LatheVisuals.IsRunning", "IsRunning"));
        var latheInserting = IsTruthy(Find(appearance, "LatheVisuals.IsInserting", "MaterialStorageVisuals.Inserting", "IsInserting"));
        var welded = IsTruthy(Find(appearance, "WeldableVisuals.IsWelded", "IsWelded", "Weldable"));
        var bolted = IsTruthy(Find(appearance, "DoorVisuals.BoltLights", "BoltLights", "BoltsDown"));
        var emergency = IsTruthy(Find(appearance, "DoorVisuals.EmergencyLights", "EmergencyAccess", "BaseEmergency"));
        var panelOpen = IsTruthy(Find(appearance, "WiresVisuals.MaintenancePanelState", "MaintenancePanelState", "Panel"));
        var lightOn = IsLightOn(Find(appearance, "PoweredLightVisuals.Bulb", "PoweredLightVisuals.BulbState", "BulbState", "Bulb"));
        var lightState = Find(appearance, "PoweredLightVisuals.Bulb", "PoweredLightVisuals.BulbState", "BulbState");

        for (var i = 0; i < visual.Layers.Count; i++)
        {
            var layer = visual.Layers[i];
            var map = layer.MapKey ?? "";
            var st = layer.State ?? "";
            var visible = layer.Visible;
            var state = layer.State;

            // --- Doors ---
            if (IsDoorBase(map, st) && !string.IsNullOrWhiteSpace(doorState))
            {
                state = DoorVisualState(doorState!);
                visible = true;
            }
            else if (map.Contains("BaseUnlit", StringComparison.OrdinalIgnoreCase)
                     || st.Equals("closed_unlit", StringComparison.OrdinalIgnoreCase)
                     || st.Equals("open_unlit", StringComparison.OrdinalIgnoreCase))
            {
                // Unlit door overlay follows open/closed only when Appearance says powered.
                if (!string.IsNullOrWhiteSpace(doorState) && powered == true)
                {
                    var baseSt = DoorVisualState(doorState!);
                    state = baseSt + "_unlit";
                    visible = true;
                }
                else
                    visible = false;
            }
            else if (map.Contains("Weldable", StringComparison.OrdinalIgnoreCase)
                     || st.Equals("welded", StringComparison.OrdinalIgnoreCase))
                visible = welded == true;
            else if (map.Contains("BaseBolted", StringComparison.OrdinalIgnoreCase)
                     || st.Equals("bolted_unlit", StringComparison.OrdinalIgnoreCase))
                visible = bolted == true;
            else if (map.Contains("BaseEmergency", StringComparison.OrdinalIgnoreCase)
                     || st.Equals("emergency_unlit", StringComparison.OrdinalIgnoreCase))
                visible = emergency == true;
            else if (map.Contains("MaintenancePanel", StringComparison.OrdinalIgnoreCase)
                     || map.Contains("WiresVisual", StringComparison.OrdinalIgnoreCase)
                     || st.Equals("panel_open", StringComparison.OrdinalIgnoreCase))
                visible = panelOpen == true;
            else if (map.Contains("Electrified", StringComparison.OrdinalIgnoreCase)
                     || map.Contains("BaseEmagging", StringComparison.OrdinalIgnoreCase)
                     || st.Equals("sparks", StringComparison.OrdinalIgnoreCase))
                visible = false;

            // --- Computers ---
            else if (map.Contains("computerLayerScreen", StringComparison.OrdinalIgnoreCase)
                     || map.Contains("ComputerVisualLayers.Screen", StringComparison.OrdinalIgnoreCase)
                     || (map.Contains("Screen", StringComparison.OrdinalIgnoreCase)
                         && map.Contains("Computer", StringComparison.OrdinalIgnoreCase)))
                visible = powered != false; // default on if Appearance missing
            else if (map.Contains("computerLayerKeys", StringComparison.OrdinalIgnoreCase)
                     || map.Contains("ComputerVisualLayers.Keyboard", StringComparison.OrdinalIgnoreCase)
                        && st.Contains("key", StringComparison.OrdinalIgnoreCase)
                     || (map.Contains("Keys", StringComparison.OrdinalIgnoreCase)
                         && map.Contains("Computer", StringComparison.OrdinalIgnoreCase)))
                visible = powered != false;

            // --- Lights ---
            else if (map.Contains("PoweredLightVisualLayers.Base", StringComparison.OrdinalIgnoreCase)
                     || (map.Contains("Base", StringComparison.OrdinalIgnoreCase)
                         && map.Contains("Light", StringComparison.OrdinalIgnoreCase)
                         && !map.Contains("Glow", StringComparison.OrdinalIgnoreCase)))
            {
                // PC spriteStateMap: On/Off → "base". Never remap to missing "on"/"off" states.
                if (!string.IsNullOrWhiteSpace(lightState))
                {
                    var mapped = MapLightBulbState(lightState!);
                    if (!string.IsNullOrWhiteSpace(mapped))
                        state = mapped;
                }
                visible = true;
            }
            else if (map.Contains("Glow", StringComparison.OrdinalIgnoreCase)
                     || st.Equals("glow", StringComparison.OrdinalIgnoreCase))
                // Default on when Appearance missing (station lights usually lit).
                visible = lightOn != false;

            // --- Lathes / techfabs ---
            else if (st.Equals("inserting", StringComparison.OrdinalIgnoreCase)
                     || map.Contains("Inserting", StringComparison.OrdinalIgnoreCase))
                visible = latheInserting == true;
            else if (st.Equals("unlit", StringComparison.OrdinalIgnoreCase))
                visible = powered != false || latheRunning == true || powered is null;

            if (visible != layer.Visible || !string.Equals(state, layer.State, StringComparison.Ordinal))
                visual.Layers[i] = layer with { Visible = visible, State = state };
        }
    }

    static bool? IsTruthy(string? value)
    {
        if (value is null) return null;
        if (bool.TryParse(value, out var b)) return b;
        if (value.Equals("On", StringComparison.OrdinalIgnoreCase)
            || value.Equals("True", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Powered", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Open", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Visible", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value.Equals("Off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("False", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Unpowered", StringComparison.OrdinalIgnoreCase)
            || value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Closed", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Empty", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Broken", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Burned", StringComparison.OrdinalIgnoreCase)
            || value.Equals("None", StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }

    static bool? IsLightOn(string? value)
    {
        if (value is null) return null; // unknown — keep YAML default (often glow hidden)
        if (value.Equals("On", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Normal", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    static string MapLightBulbState(string appearance) =>
        appearance.ToLowerInvariant() switch
        {
            "on" or "normal" or "off" => "base",
            "broken" => "broken",
            "burned" or "burnt" => "burned",
            "empty" => "empty",
            _ => appearance.ToLowerInvariant(),
        };

    static string? Find(Dictionary<string, string> appearance, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (appearance.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
            // Suffix match: "DoorVisuals.State" vs full enum string
            foreach (var (k, val) in appearance)
            {
                if (k.EndsWith(key, StringComparison.OrdinalIgnoreCase)
                    || k.Equals(key, StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith(k, StringComparison.OrdinalIgnoreCase))
                    return val;
            }
        }
        return null;
    }

    static bool IsDoorBase(string map, string state) =>
        map.Contains("DoorVisualLayers.Base", StringComparison.OrdinalIgnoreCase)
        && !map.Contains("Unlit", StringComparison.OrdinalIgnoreCase)
        && !map.Contains("Bolted", StringComparison.OrdinalIgnoreCase)
        && !map.Contains("Emergency", StringComparison.OrdinalIgnoreCase)
        && !map.Contains("Emag", StringComparison.OrdinalIgnoreCase)
        || state.Equals("closed", StringComparison.OrdinalIgnoreCase)
        || state.Equals("open", StringComparison.OrdinalIgnoreCase)
        || state.Equals("opening", StringComparison.OrdinalIgnoreCase)
        || state.Equals("closing", StringComparison.OrdinalIgnoreCase);

    static string DoorVisualState(string doorState) =>
        doorState.ToLowerInvariant() switch
        {
            "open" or "opened" => "open",
            "opening" => "opening",
            "closing" => "closing",
            "denying" or "deny" => "deny",
            "emagging" => "closed",
            _ => "closed",
        };
}

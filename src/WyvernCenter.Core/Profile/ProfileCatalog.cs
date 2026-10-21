using System.Globalization;
using System.Reflection;
using System.Text.Json;
using WyvernCenter.Core.Scm;
using WyvernCenter.Core.Wmi2;

namespace WyvernCenter.Core.Profile;

/// <summary>Model profiles shipped as embedded JSON (Profiles/*.json), matched by EC firmware version.</summary>
public static class ProfileCatalog
{
    private static readonly Lazy<IReadOnlyList<ModelProfile>> All = new(LoadEmbedded);

    /// <summary>Every model profile (not the generic fallback).</summary>
    public static IReadOnlyList<ModelProfile> Models => All.Value.Where(p => !p.Generic).ToList();

    /// <summary>The fallback for SCM0 (WMI v1) MSI laptops without a profile (see Profiles/generic-msi.json).</summary>
    public static ModelProfile Generic => GenericFor(FirmwareInterface.Scm);

    /// <summary>The fallback for an interface: generic-msi.json (SCM0) or generic-msi-wmi2.json (WMI v2).</summary>
    public static ModelProfile GenericFor(FirmwareInterface iface) => All.Value.Single(p => p.Generic && p.Interface == iface);

    public static ModelProfile? Match(string firmwareVersion, FirmwareInterface iface = FirmwareInterface.Scm) =>
        Models.FirstOrDefault(p => p.Interface == iface && p.Firmware.Contains(firmwareVersion, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Exact firmware → that model. Same board (the part before the dot, e.g. "16R5EMS1" in "16R5EMS1.102") with
    /// another version → that model, unconfirmed. Any other MSI firmware → the generic profile. No firmware → none.
    /// </summary>
    /// <param name="iface">The interface the firmware answered on: a profile only matches laptops of its own kind.</param>
    public static ModelMatch Resolve(string firmwareVersion, FirmwareInterface iface = FirmwareInterface.Scm)
    {
        if (string.IsNullOrWhiteSpace(firmwareVersion)) return new ModelMatch(null, ModelSupport.None);
        if (Match(firmwareVersion, iface) is { } exact) return new ModelMatch(exact, ModelSupport.Supported);
        var board = Board(firmwareVersion);
        if (board is not null && Models.FirstOrDefault(p => p.Interface == iface && p.Firmware.Any(f => Board(f) == board)) is { } sibling)
            return new ModelMatch(sibling, ModelSupport.SameBoard);
        return new ModelMatch(GenericFor(iface), ModelSupport.Generic);
    }

    private static string? Board(string version)
    {
        var dot = version.IndexOf('.');
        return dot >= 4 ? version[..dot].ToUpperInvariant() : null;
    }

    private static IReadOnlyList<ModelProfile> LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && n.Contains(".Profiles."))
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                return Parse(JsonDocument.Parse(s).RootElement);
            })
            .ToList();
    }

    public static ModelProfile Parse(JsonElement root)
    {
        var settings = new Dictionary<string, SettingDef>();
        foreach (var p in root.GetProperty("settings").EnumerateObject())
        {
            var e = p.Value;
            var route = e.GetProperty("route").GetString() switch
            {
                "scm" => Route.Scm,
                "wmi2" => Route.Wmi2,
                var other => throw new InvalidDataException($"{p.Name}: unknown route '{other}'"),
            };
            var block = route == Route.Wmi2 && e.TryGetProperty("block", out var b)
                ? new Wmi2Block(Enum.Parse<Wmi2Method>(b.GetProperty("method").GetString()!, ignoreCase: true),
                    (byte)Num(b.GetProperty("parameter")), Num(b.GetProperty("byte")))
                : null;
            if (block is { Writable: false } && !(e.TryGetProperty("readOnly", out var bro) && bro.GetBoolean()))
                throw new InvalidDataException($"{p.Name}: WMI2 block settings other than fan-curve points must be read-only.");
            settings[p.Name] = new SettingDef(
                p.Name,
                route,
                route == Route.Scm ? Enum.Parse<ScmClass>(e.GetProperty("class").GetString()!, ignoreCase: true) : null,
                route == Route.Scm ? e.GetProperty("index").GetInt32() : null,
                route == Route.Wmi2 && block is null ? Num(e.GetProperty("offset")) : null,
                e.TryGetProperty("mask", out var m) ? Num(m) : 0xFF,
                e.TryGetProperty("values", out var v) ? v.EnumerateObject().ToDictionary(x => x.Name, x => Num(x.Value)) : null,
                e.TryGetProperty("min", out var mn) ? Num(mn) : null,
                e.TryGetProperty("max", out var mx) ? Num(mx) : null,
                e.TryGetProperty("verified", out var vf) && vf.GetBoolean(),
                e.TryGetProperty("readOnly", out var ro) && ro.GetBoolean(),
                e.TryGetProperty("expect", out var ex) ? Num(ex.GetProperty("mask")) : 0,
                e.TryGetProperty("expect", out var ex2) ? ex2.GetProperty("values").EnumerateArray().Select(Num).ToList() : null,
                block,
                e.TryGetProperty("testable", out var te) && te.GetBoolean());
        }

        var c = root.GetProperty("curve");
        return new ModelProfile(
            root.GetProperty("name").GetString()!,
            root.GetProperty("firmware").EnumerateArray().Select(x => x.GetString()!).ToList(),
            root.GetProperty("msiServices").EnumerateArray().Select(x => x.GetString()!).ToList(),
            settings,
            new CurveRules(Num(c.GetProperty("minSpeed")), Num(c.GetProperty("maxSpeed")),
                           Num(c.GetProperty("floorAtIndex")), Num(c.GetProperty("floorSpeed"))),
            root.TryGetProperty("generic", out var g) && g.GetBoolean(),
            root.TryGetProperty("interface", out var i) && i.GetString() == "wmi2" ? FirmwareInterface.Wmi2 : FirmwareInterface.Scm);
    }

    /// <summary>Numbers may be JSON numbers or "0x.." strings (registers read better in hex).</summary>
    private static int Num(JsonElement e) => e.ValueKind == JsonValueKind.Number
        ? e.GetInt32()
        : e.GetString()!.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(e.GetString()![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(e.GetString()!, CultureInfo.InvariantCulture);
}

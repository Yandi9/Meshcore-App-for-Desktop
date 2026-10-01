using System.Globalization;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace MC1.Windows.Services.VectorTiles;

/// <summary>The parts of a MapLibre GL style this app draws: background, fill, line and symbol (text) layers.</summary>
public sealed class MapStyle
{
    public List<StyleLayer> Layers { get; } = new();
    /// <summary>TileJSON address of the vector source (e.g. https://tiles.openfreemap.org/planet).</summary>
    public string? SourceUrl { get; private set; }
    /// <summary>Tile URL templates given directly in the style.</summary>
    public List<string> SourceTiles { get; } = new();
    public int SourceMaxZoom { get; private set; } = 14;

    public static MapStyle Parse(string json, string sourceDomain = "tiles.openfreemap.org")
    {
        json = json.Replace("__TILEJSON_DOMAIN__", sourceDomain);
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Not a map style");
        var style = new MapStyle();
        if (root["sources"] is JsonObject sources)
        {
            // The first vector source (OpenMapTiles schema).
            foreach (var (_, src) in sources)
            {
                if (src is not JsonObject s || (string?)s["type"] != "vector") continue;
                style.SourceUrl = (string?)s["url"];
                if (s["tiles"] is JsonArray tiles) style.SourceTiles.AddRange(tiles.Select(t => (string?)t).Where(t => t is not null)!);
                if (s["maxzoom"] is JsonValue mz && mz.TryGetValue<double>(out var m)) style.SourceMaxZoom = (int)m;
                break;
            }
        }
        if (root["layers"] is JsonArray layers)
            foreach (var l in layers.OfType<JsonObject>())
            {
                var type = (string?)l["type"] ?? "";
                if (type is not ("background" or "fill" or "line" or "symbol")) continue;
                var layout = l["layout"] as JsonObject;
                style.Layers.Add(new StyleLayer
                {
                    Id = (string?)l["id"] ?? "",
                    Type = type,
                    SourceLayer = (string?)l["source-layer"],
                    MinZoom = l["minzoom"] is JsonValue mn ? (double)mn : 0,
                    MaxZoom = l["maxzoom"] is JsonValue mx ? (double)mx : 24,
                    Filter = l["filter"],
                    Paint = l["paint"] as JsonObject ?? new JsonObject(),
                    Layout = layout ?? new JsonObject(),
                    Visible = (string?)layout?["visibility"] != "none",
                });
            }
        return style;
    }
}

public sealed class StyleLayer
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string? SourceLayer { get; init; }
    public double MinZoom { get; init; }
    public double MaxZoom { get; init; } = 24;
    public JsonNode? Filter { get; init; }
    public JsonObject Paint { get; init; } = new();
    public JsonObject Layout { get; init; } = new();
    public bool Visible { get; init; } = true;

    public bool ShowsAt(double zoom) => Visible && zoom >= MinZoom && zoom < MaxZoom;
}

/// <summary>Evaluates MapLibre style expressions, legacy filters and zoom functions.</summary>
public static class StyleEval
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public readonly record struct Ctx(double Zoom, VectorFeature? Feature);

    public static bool Filter(JsonNode? filter, Ctx ctx)
    {
        if (filter is null) return true;
        return Truthy(Eval(filter, ctx));
    }

    public static object? Prop(JsonObject props, string name, Ctx ctx, object? fallback = null)
    {
        var node = props[name];
        if (node is null) return fallback;
        return Eval(node, ctx) ?? fallback;
    }

    public static double Number(JsonObject props, string name, Ctx ctx, double fallback) =>
        ToNumber(Prop(props, name, ctx)) ?? fallback;

    public static SKColor? Color(JsonObject props, string name, Ctx ctx) => ToColor(Prop(props, name, ctx));

    public static string? String(JsonObject props, string name, Ctx ctx) => Prop(props, name, ctx) switch
    {
        null => null,
        string s => s,
        object o => Stringify(o),
    };

    public static double[]? Numbers(JsonObject props, string name, Ctx ctx) => Prop(props, name, ctx) switch
    {
        List<object?> list => list.Select(v => ToNumber(v) ?? 0).ToArray(),
        _ => null,
    };

    // MARK: Evaluation

    public static object? Eval(JsonNode? node, Ctx ctx)
    {
        switch (node)
        {
            case null: return null;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) return s;
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<double>(out var d)) return d;
                return null;
            case JsonObject o:
                return o["stops"] is JsonArray ? EvalFunction(o, ctx) : null;
            case JsonArray a:
                if (a.Count == 0) return new List<object?>();
                if (a[0] is JsonValue first && first.TryGetValue<string>(out var op)) return EvalOp(op, a, ctx);
                return a.Select(x => Eval(x, ctx)).ToList();
        }
        return null;
    }

    private static object? EvalOp(string op, JsonArray a, Ctx ctx)
    {
        var f = ctx.Feature;
        switch (op)
        {
            case "literal": return a.Count > 1 ? Literal(a[1]) : null;
            case "zoom": return ctx.Zoom;
            case "geometry-type": return f?.GeometryType;
            case "get":
                return a.Count > 1 && Eval(a[1], ctx) is string key ? Get(f, key) : null;
            case "has":
                // Legacy and expression forms agree: ["has", "name"].
                return a.Count > 1 && Eval(a[1], ctx) is string hk && HasProp(f, hk);
            case "!has":
                return !(a.Count > 1 && Eval(a[1], ctx) is string nk && HasProp(f, nk));
            case "!": return !Truthy(a.Count > 1 ? Eval(a[1], ctx) : null);
            case "all":
                for (var i = 1; i < a.Count; i++) if (!Truthy(Eval(a[i], ctx))) return false;
                return true;
            case "any":
                for (var i = 1; i < a.Count; i++) if (Truthy(Eval(a[i], ctx))) return true;
                return false;
            case "none":
                for (var i = 1; i < a.Count; i++) if (Truthy(Eval(a[i], ctx))) return false;
                return true;
            case "==" or "!=" or "<" or "<=" or ">" or ">=":
            {
                if (a.Count < 3) return false;
                object? left, right;
                if (IsLegacyKey(a[1]))
                {
                    left = LegacyGet(f, (string)a[1]!);
                    right = Literal(a[2]);
                }
                else
                {
                    left = Eval(a[1], ctx);
                    right = Eval(a[2], ctx);
                }
                return Compare(op, left, right);
            }
            case "in" or "!in":
            {
                if (a.Count < 3) return op == "!in";
                bool found;
                if (IsLegacyKey(a[1]))
                {
                    var value = LegacyGet(f, (string)a[1]!);
                    found = false;
                    for (var i = 2; i < a.Count && !found; i++) found = EqualsLoose(value, Literal(a[i]));
                }
                else
                {
                    var needle = Eval(a[1], ctx);
                    found = Eval(a[2], ctx) switch
                    {
                        List<object?> list => list.Any(x => EqualsLoose(x, needle)),
                        string hay when needle is string n => hay.Contains(n, StringComparison.Ordinal),
                        _ => false,
                    };
                }
                return op == "in" ? found : !found;
            }
            case "match":
            {
                if (a.Count < 3) return null;
                var input = Eval(a[1], ctx);
                var i = 2;
                for (; i + 1 < a.Count; i += 2)
                {
                    var labels = a[i];
                    var hit = labels is JsonArray arr ? arr.Any(l => EqualsLoose(input, Literal(l))) : EqualsLoose(input, Literal(labels));
                    if (hit) return Eval(a[i + 1], ctx);
                }
                return i < a.Count ? Eval(a[i], ctx) : null;
            }
            case "case":
            {
                var i = 1;
                for (; i + 1 < a.Count; i += 2)
                    if (Truthy(Eval(a[i], ctx))) return Eval(a[i + 1], ctx);
                return i < a.Count ? Eval(a[i], ctx) : null;
            }
            case "coalesce":
                for (var i = 1; i < a.Count; i++)
                    if (Eval(a[i], ctx) is { } v && !(v is string s && s.Length == 0)) return v;
                return null;
            case "concat":
                return string.Concat(Enumerable.Range(1, a.Count - 1).Select(i => Stringify(Eval(a[i], ctx))));
            case "format":
                // ["format", text, {options}, text, {options}…] — the texts only.
                return string.Concat(Enumerable.Range(1, a.Count - 1).Where(i => a[i] is not JsonObject).Select(i => Stringify(Eval(a[i], ctx))));
            case "to-string": return Stringify(a.Count > 1 ? Eval(a[1], ctx) : null);
            case "to-number":
                for (var i = 1; i < a.Count; i++) if (ToNumber(Eval(a[i], ctx)) is { } n) return n;
                return 0.0;
            case "to-boolean": return Truthy(a.Count > 1 ? Eval(a[1], ctx) : null);
            case "number": case "string": case "boolean": case "image": case "to-color":
                return a.Count > 1 ? Eval(a[1], ctx) : null;
            case "upcase": return Stringify(a.Count > 1 ? Eval(a[1], ctx) : null).ToUpperInvariant();
            case "downcase": return Stringify(a.Count > 1 ? Eval(a[1], ctx) : null).ToLowerInvariant();
            case "rgb" or "rgba":
            {
                var n = Enumerable.Range(1, a.Count - 1).Select(i => ToNumber(Eval(a[i], ctx)) ?? 0).ToArray();
                if (n.Length < 3) return null;
                return new SKColor((byte)Math.Clamp(n[0], 0, 255), (byte)Math.Clamp(n[1], 0, 255), (byte)Math.Clamp(n[2], 0, 255),
                    (byte)Math.Clamp((n.Length > 3 ? n[3] : 1) * 255, 0, 255));
            }
            case "+": return Enumerable.Range(1, a.Count - 1).Sum(i => ToNumber(Eval(a[i], ctx)) ?? 0);
            case "*": return Enumerable.Range(1, a.Count - 1).Aggregate(1.0, (acc, i) => acc * (ToNumber(Eval(a[i], ctx)) ?? 0));
            case "-":
                return a.Count == 2 ? -(ToNumber(Eval(a[1], ctx)) ?? 0) : (ToNumber(Eval(a[1], ctx)) ?? 0) - (ToNumber(Eval(a[2], ctx)) ?? 0);
            case "/": return (ToNumber(Eval(a[1], ctx)) ?? 0) / Math.Max(1e-9, ToNumber(Eval(a[2], ctx)) ?? 1);
            case "step":
            {
                if (a.Count < 3) return null;
                var x = ToNumber(Eval(a[1], ctx)) ?? 0;
                var result = Eval(a[2], ctx);
                for (var i = 3; i + 1 < a.Count; i += 2)
                {
                    if (x >= (ToNumber(Eval(a[i], ctx)) ?? double.MaxValue)) result = Eval(a[i + 1], ctx);
                    else break;
                }
                return result;
            }
            case "interpolate" or "interpolate-hcl" or "interpolate-lab":
            {
                if (a.Count < 5 || a[1] is not JsonArray kind) return null;
                var baseValue = (string?)kind[0] == "exponential" && kind.Count > 1 ? ToNumber(Eval(kind[1], ctx)) ?? 1 : 1;
                var x = ToNumber(Eval(a[2], ctx)) ?? 0;
                var stops = new List<(double In, JsonNode? Out)>();
                for (var i = 3; i + 1 < a.Count; i += 2) stops.Add((ToNumber(Eval(a[i], ctx)) ?? 0, a[i + 1]));
                return InterpolateStops(stops, x, baseValue, ctx);
            }
            case "let": case "var": case "collator": case "resolved-locale":
                return null;
            default:
                // An array literal that happens to start with a string, e.g. a font stack.
                return a.Select(x => Eval(x, ctx)).ToList();
        }
    }

    /// <summary>Legacy function: {"base": 1.2, "stops": [[zoom, value], …]} (optionally keyed by a property).</summary>
    private static object? EvalFunction(JsonObject fn, Ctx ctx)
    {
        var stopsNode = (JsonArray)fn["stops"]!;
        var baseValue = fn["base"] is JsonValue bv && bv.TryGetValue<double>(out var bd) ? bd : 1;
        var type = (string?)fn["type"];
        var property = (string?)fn["property"];
        object? input = property is null ? ctx.Zoom : Get(ctx.Feature, property);
        var stops = new List<(JsonNode? In, JsonNode? Out)>();
        foreach (var s in stopsNode.OfType<JsonArray>())
            if (s.Count >= 2)
            {
                // Zoom-and-property functions: [{zoom, value}, out] — use the zoom part.
                var key = s[0] is JsonObject zo ? zo["zoom"] : s[0];
                stops.Add((key, s[1]));
            }
        if (stops.Count == 0) return null;
        if (type == "categorical" || (type is null && input is string))
        {
            foreach (var (i, o) in stops) if (EqualsLoose(input, Literal(i))) return Eval(o, ctx);
            return fn["default"] is { } def ? Eval(def, ctx) : null;
        }
        var x = ToNumber(input) ?? 0;
        if (type == "interval")
        {
            JsonNode? result = stops[0].Out;
            foreach (var (i, o) in stops) if (x >= (ToNumber(Literal(i)) ?? 0)) result = o;
            return Eval(result, ctx);
        }
        return InterpolateStops(stops.Select(s => (ToNumber(Literal(s.In)) ?? 0, s.Out)).ToList(), x, baseValue, ctx);
    }

    private static object? InterpolateStops(List<(double In, JsonNode? Out)> stops, double x, double baseValue, Ctx ctx)
    {
        if (stops.Count == 0) return null;
        if (x <= stops[0].In) return Eval(stops[0].Out, ctx);
        if (x >= stops[^1].In) return Eval(stops[^1].Out, ctx);
        for (var i = 0; i < stops.Count - 1; i++)
        {
            var (x0, o0) = stops[i];
            var (x1, o1) = stops[i + 1];
            if (x < x0 || x > x1) continue;
            var t = Fraction(x, x0, x1, baseValue);
            return Lerp(Eval(o0, ctx), Eval(o1, ctx), t);
        }
        return Eval(stops[^1].Out, ctx);
    }

    private static double Fraction(double x, double x0, double x1, double b)
    {
        var range = x1 - x0;
        if (range <= 0) return 0;
        var progress = x - x0;
        if (Math.Abs(b - 1) < 1e-9) return progress / range;
        return (Math.Pow(b, progress) - 1) / (Math.Pow(b, range) - 1);
    }

    private static object? Lerp(object? a, object? b, double t)
    {
        if (ToNumber(a) is { } na && ToNumber(b) is { } nb && a is not string && b is not string) return na + (nb - na) * t;
        if (ToColor(a) is { } ca && ToColor(b) is { } cb)
            return new SKColor(L(ca.Red, cb.Red), L(ca.Green, cb.Green), L(ca.Blue, cb.Blue), L(ca.Alpha, cb.Alpha));
        if (a is List<object?> la && b is List<object?> lb && la.Count == lb.Count)
            return la.Select((v, i) => (object?)((ToNumber(v) ?? 0) + ((ToNumber(lb[i]) ?? 0) - (ToNumber(v) ?? 0)) * t)).ToList();
        return t < 0.5 ? a : b;
        byte L(byte x, byte y) => (byte)Math.Clamp(Math.Round(x + (y - x) * t), 0, 255);
    }

    // MARK: Helpers

    private static bool IsLegacyKey(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out _);

    private static object? Literal(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v => v.TryGetValue<string>(out var s) ? s : v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<double>(out var d) ? d : null,
        JsonArray a => a.Select(Literal).ToList(),
        _ => null,
    };

    private static object? Get(VectorFeature? f, string key) =>
        f is not null && f.Properties.TryGetValue(key, out var v) ? v : null;

    private static bool HasProp(VectorFeature? f, string key) => key == "$type" || (f?.Properties.ContainsKey(key) ?? false);

    private static object? LegacyGet(VectorFeature? f, string key) => key switch
    {
        "$type" => f?.Kind switch { GeometryKind.Point => "Point", GeometryKind.LineString => "LineString", GeometryKind.Polygon => "Polygon", _ => null },
        "$id" => null,
        _ => Get(f, key),
    };

    private static bool Compare(string op, object? l, object? r)
    {
        switch (op)
        {
            case "==": return EqualsLoose(l, r);
            case "!=": return !EqualsLoose(l, r);
        }
        if (l is string ls && r is string rs)
        {
            var c = string.CompareOrdinal(ls, rs);
            return op switch { "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0 };
        }
        if (ToNumber(l) is not { } a || ToNumber(r) is not { } b || l is string || r is string) return false;
        return op switch { "<" => a < b, "<=" => a <= b, ">" => a > b, _ => a >= b };
    }

    private static bool EqualsLoose(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is string sa) return b is string sb && sa == sb;
        if (a is bool ba) return b is bool bb && ba == bb;
        if (a is double da && b is double db) return Math.Abs(da - db) < 1e-9;
        return Equals(a, b);
    }

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0,
        _ => true,
    };

    public static double? ToNumber(object? v) => v switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, Inv, out var p) => p,
        _ => null,
    };

    public static string Stringify(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString(Inv),
        SKColor c => $"rgba({c.Red},{c.Green},{c.Blue},{c.Alpha / 255.0:0.###})",
        List<object?> l => string.Join(",", l.Select(Stringify)),
        _ => v.ToString() ?? "",
    };

    // MARK: Colours

    private static readonly Dictionary<string, SKColor> ColorCache = new();

    public static SKColor? ToColor(object? v)
    {
        if (v is SKColor c) return c;
        if (v is not string s) return null;
        lock (ColorCache)
        {
            if (ColorCache.TryGetValue(s, out var cached)) return cached;
        }
        var parsed = ParseColor(s);
        if (parsed is { } p) lock (ColorCache) ColorCache[s] = p;
        return parsed;
    }

    private static SKColor? ParseColor(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s == "transparent") return SKColors.Transparent;
        if (s.StartsWith('#'))
        {
            var hex = s[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, Inv, out var rgb))
                return new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, Inv, out var rgba))
                return new SKColor((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
            return null;
        }
        var open = s.IndexOf('(');
        var close = s.LastIndexOf(')');
        if (open > 0 && close > open)
        {
            var fn = s[..open].Trim();
            var parts = s[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            double P(int i, double scale = 1)
            {
                if (i >= parts.Length) return 1 * scale;
                var t = parts[i].Trim();
                var pct = t.EndsWith('%');
                var n = double.TryParse(t.TrimEnd('%'), NumberStyles.Float, Inv, out var val) ? val : 0;
                return pct ? n / 100 * scale : n;
            }
            switch (fn)
            {
                case "rgb" or "rgba":
                    return new SKColor((byte)Math.Clamp(P(0, 255), 0, 255), (byte)Math.Clamp(P(1, 255), 0, 255), (byte)Math.Clamp(P(2, 255), 0, 255),
                        (byte)Math.Clamp(parts.Length > 3 ? P(3) * 255 : 255, 0, 255));
                case "hsl" or "hsla":
                {
                    var h = P(0);
                    var sat = P(1, 1);
                    var light = P(2, 1);
                    // "hsl(0,1%,2%)": percentages were converted to 0..1 above.
                    var alpha = parts.Length > 3 ? P(3) : 1;
                    var (r, g, b) = HslToRgb(h, sat, light);
                    return new SKColor(r, g, b, (byte)Math.Clamp(alpha * 255, 0, 255));
                }
            }
        }
        return s switch
        {
            "black" => SKColors.Black,
            "white" => SKColors.White,
            "red" => SKColors.Red,
            "gray" or "grey" => SKColors.Gray,
            _ => null,
        };
    }

    private static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        double r, g, b;
        if (s <= 0) r = g = b = l;
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }
        return ((byte)Math.Round(Math.Clamp(r, 0, 1) * 255), (byte)Math.Round(Math.Clamp(g, 0, 1) * 255), (byte)Math.Round(Math.Clamp(b, 0, 1) * 255));
    }
}

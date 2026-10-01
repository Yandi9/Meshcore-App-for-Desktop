using System.Text;

namespace MC1.Windows.Services.VectorTiles;

/// <summary>A decoded Mapbox Vector Tile (MVT 2.x): named layers of features in tile coordinates.</summary>
public sealed class VectorTile
{
    public Dictionary<string, VectorLayer> Layers { get; } = new(StringComparer.Ordinal);
}

public sealed class VectorLayer(string name, int extent, List<VectorFeature> features)
{
    public string Name { get; } = name;
    public int Extent { get; } = extent;
    public List<VectorFeature> Features { get; } = features;
}

public enum GeometryKind { Unknown = 0, Point = 1, LineString = 2, Polygon = 3 }

public sealed class VectorFeature(GeometryKind kind, Dictionary<string, object> properties, List<List<(float X, float Y)>> parts)
{
    public GeometryKind Kind { get; } = kind;
    public Dictionary<string, object> Properties { get; } = properties;
    /// <summary>Points: one part per point. Lines: one part per line. Polygons: one part per ring.</summary>
    public List<List<(float X, float Y)>> Parts { get; } = parts;

    /// <summary>"Point", "LineString", "Polygon" or the Multi- variants, as MapLibre's ["geometry-type"] reports them.</summary>
    public string GeometryType => Kind switch
    {
        GeometryKind.Point => Parts.Count > 1 ? "MultiPoint" : "Point",
        GeometryKind.LineString => Parts.Count > 1 ? "MultiLineString" : "LineString",
        GeometryKind.Polygon => CountExteriorRings() > 1 ? "MultiPolygon" : "Polygon",
        _ => "Unknown",
    };

    private int CountExteriorRings()
    {
        var n = 0;
        foreach (var ring in Parts) if (SignedArea(ring) > 0) n++;
        return n;
    }

    public static double SignedArea(List<(float X, float Y)> ring)
    {
        double a = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) a += (double)ring[j].X * ring[i].Y - (double)ring[i].X * ring[j].Y;
        return a / 2;
    }
}

/// <summary>Minimal protobuf decoder for the MVT schema.</summary>
public static class MvtReader
{
    public static VectorTile Read(ReadOnlySpan<byte> data)
    {
        var tile = new VectorTile();
        var r = new Pbf(data);
        while (r.Next(out var field, out var wire))
        {
            if (field == 3 && wire == 2)
            {
                var layer = ReadLayer(r.Bytes());
                if (layer is not null) tile.Layers[layer.Name] = layer;
            }
            else r.Skip(wire);
        }
        return tile;
    }

    private static VectorLayer? ReadLayer(ReadOnlySpan<byte> data)
    {
        var r = new Pbf(data);
        string name = "";
        var extent = 4096;
        var keys = new List<string>();
        var values = new List<object>();
        var rawFeatures = new List<(int Start, int Length)>();
        var baseOffset = 0;
        // Features refer to keys/values that may come after them, so collect first and decode afterwards.
        var copy = data.ToArray();
        while (r.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: name = Encoding.UTF8.GetString(r.Bytes()); break;
                case 2 when wire == 2:
                    var (s, l) = r.BytesRange();
                    rawFeatures.Add((s - baseOffset, l));
                    break;
                case 3 when wire == 2: keys.Add(Encoding.UTF8.GetString(r.Bytes())); break;
                case 4 when wire == 2: values.Add(ReadValue(r.Bytes())); break;
                case 5 when wire == 0: extent = (int)r.Varint(); break;
                default: r.Skip(wire); break;
            }
        }
        var features = new List<VectorFeature>(rawFeatures.Count);
        foreach (var (start, length) in rawFeatures)
        {
            var f = ReadFeature(copy.AsSpan(start, length), keys, values);
            if (f is not null) features.Add(f);
        }
        return new VectorLayer(name, extent <= 0 ? 4096 : extent, features);
    }

    private static object ReadValue(ReadOnlySpan<byte> data)
    {
        var r = new Pbf(data);
        object value = "";
        while (r.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: value = Encoding.UTF8.GetString(r.Bytes()); break;
                case 2 when wire == 5: value = (double)r.Float(); break;
                case 3 when wire == 1: value = r.Double(); break;
                case 4 when wire == 0: value = (double)(long)r.Varint(); break;
                case 5 when wire == 0: value = (double)r.Varint(); break;
                case 6 when wire == 0: value = (double)ZigZag(r.Varint()); break;
                case 7 when wire == 0: value = r.Varint() != 0; break;
                default: r.Skip(wire); break;
            }
        }
        return value;
    }

    private static VectorFeature? ReadFeature(ReadOnlySpan<byte> data, List<string> keys, List<object> values)
    {
        var r = new Pbf(data);
        var kind = GeometryKind.Unknown;
        var props = new Dictionary<string, object>(StringComparer.Ordinal);
        List<uint>? geometry = null;
        while (r.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 2 when wire == 2:
                    var tags = r.PackedVarints();
                    for (var i = 0; i + 1 < tags.Count; i += 2)
                        if (tags[i] < keys.Count && tags[i + 1] < values.Count) props[keys[(int)tags[i]]] = values[(int)tags[i + 1]];
                    break;
                case 3 when wire == 0: kind = (GeometryKind)(int)r.Varint(); break;
                case 4 when wire == 2: geometry = r.PackedVarints(); break;
                default: r.Skip(wire); break;
            }
        }
        if (geometry is null || kind == GeometryKind.Unknown) return null;
        return new VectorFeature(kind, props, DecodeGeometry(geometry, kind));
    }

    private static List<List<(float X, float Y)>> DecodeGeometry(List<uint> cmds, GeometryKind kind)
    {
        var parts = new List<List<(float X, float Y)>>();
        List<(float X, float Y)>? current = null;
        int x = 0, y = 0, i = 0;
        while (i < cmds.Count)
        {
            var cmd = cmds[i] & 0x7;
            var count = (int)(cmds[i] >> 3);
            i++;
            switch (cmd)
            {
                case 1: // MoveTo
                    for (var k = 0; k < count && i + 1 < cmds.Count; k++)
                    {
                        x += (int)ZigZag(cmds[i++]);
                        y += (int)ZigZag(cmds[i++]);
                        current = new List<(float X, float Y)> { (x, y) };
                        parts.Add(current);
                    }
                    break;
                case 2: // LineTo
                    for (var k = 0; k < count && i + 1 < cmds.Count; k++)
                    {
                        x += (int)ZigZag(cmds[i++]);
                        y += (int)ZigZag(cmds[i++]);
                        current?.Add((x, y));
                    }
                    break;
                case 7: // ClosePath
                    if (current is { Count: > 0 } && kind == GeometryKind.Polygon) current.Add(current[0]);
                    break;
                default:
                    return parts; // malformed
            }
        }
        return parts;
    }

    private static long ZigZag(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);

    /// <summary>Tiny protobuf wire-format reader.</summary>
    private ref struct Pbf(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos = 0;

        public bool Next(out int field, out int wire)
        {
            field = wire = 0;
            if (_pos >= _data.Length) return false;
            var key = Varint();
            field = (int)(key >> 3);
            wire = (int)(key & 7);
            return true;
        }

        public ulong Varint()
        {
            ulong result = 0;
            var shift = 0;
            while (_pos < _data.Length)
            {
                var b = _data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 63) break;
            }
            return result;
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var len = (int)Varint();
            len = Math.Clamp(len, 0, _data.Length - _pos);
            var s = _data.Slice(_pos, len);
            _pos += len;
            return s;
        }

        public (int Start, int Length) BytesRange()
        {
            var len = (int)Varint();
            len = Math.Clamp(len, 0, _data.Length - _pos);
            var start = _pos;
            _pos += len;
            return (start, len);
        }

        public float Float()
        {
            var v = BitConverter.ToSingle(_data.Slice(_pos, 4));
            _pos += 4;
            return v;
        }

        public double Double()
        {
            var v = BitConverter.ToDouble(_data.Slice(_pos, 8));
            _pos += 8;
            return v;
        }

        public List<uint> PackedVarints()
        {
            var len = (int)Varint();
            var end = Math.Min(_data.Length, _pos + len);
            var list = new List<uint>(len);
            while (_pos < end) list.Add((uint)Varint());
            _pos = end;
            return list;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: _pos += 8; break;
                case 2: _pos += (int)Varint(); break;
                case 5: _pos += 4; break;
                default: _pos = _data.Length; break;
            }
        }
    }
}

namespace MeshCore;

/// <summary>Cayenne LPP sensor types used by MeshCore telemetry.</summary>
public enum LppSensorType : byte
{
    DigitalInput = 0,
    DigitalOutput = 1,
    AnalogInput = 2,
    AnalogOutput = 3,
    GenericSensor = 100,
    Illuminance = 101,
    Presence = 102,
    Temperature = 103,
    Humidity = 104,
    Accelerometer = 113,
    Barometer = 115,
    Voltage = 116,
    Current = 117,
    Frequency = 118,
    Percentage = 120,
    Altitude = 121,
    Load = 122,
    Concentration = 125,
    Power = 128,
    Distance = 130,
    Energy = 131,
    Direction = 132,
    UnixTime = 133,
    Gyrometer = 134,
    Colour = 135,
    Gps = 136,
    Switch = 142,
}

public static class LppSensorTypeExtensions
{
    public static int DataSize(this LppSensorType t) => t switch
    {
        LppSensorType.DigitalInput or LppSensorType.DigitalOutput or LppSensorType.Presence or LppSensorType.Humidity
            or LppSensorType.Percentage or LppSensorType.Switch => 1,
        LppSensorType.AnalogInput or LppSensorType.AnalogOutput or LppSensorType.Illuminance or LppSensorType.Temperature
            or LppSensorType.Barometer or LppSensorType.Voltage or LppSensorType.Current or LppSensorType.Altitude
            or LppSensorType.Concentration or LppSensorType.Power or LppSensorType.Direction => 2,
        LppSensorType.Colour or LppSensorType.Load => 3,
        LppSensorType.GenericSensor or LppSensorType.Frequency or LppSensorType.Distance or LppSensorType.Energy
            or LppSensorType.UnixTime => 4,
        LppSensorType.Accelerometer or LppSensorType.Gyrometer => 6,
        LppSensorType.Gps => 9,
        _ => 0,
    };

    public static string DisplayName(this LppSensorType t) => t switch
    {
        LppSensorType.DigitalInput => "Digital Input",
        LppSensorType.DigitalOutput => "Digital Output",
        LppSensorType.AnalogInput => "Analog Input",
        LppSensorType.AnalogOutput => "Analog Output",
        LppSensorType.GenericSensor => "Sensor",
        LppSensorType.Illuminance => "Illuminance",
        LppSensorType.Presence => "Presence",
        LppSensorType.Temperature => "Temperature",
        LppSensorType.Humidity => "Humidity",
        LppSensorType.Accelerometer => "Accelerometer",
        LppSensorType.Barometer => "Pressure",
        LppSensorType.Voltage => "Voltage",
        LppSensorType.Current => "Current",
        LppSensorType.Frequency => "Frequency",
        LppSensorType.Percentage => "Percentage",
        LppSensorType.Altitude => "Altitude",
        LppSensorType.Load => "Load",
        LppSensorType.Concentration => "Concentration",
        LppSensorType.Power => "Power",
        LppSensorType.Distance => "Distance",
        LppSensorType.Energy => "Energy",
        LppSensorType.Direction => "Direction",
        LppSensorType.UnixTime => "Time",
        LppSensorType.Gyrometer => "Gyrometer",
        LppSensorType.Colour => "Colour",
        LppSensorType.Gps => "GPS",
        LppSensorType.Switch => "Switch",
        _ => t.ToString(),
    };

    public static string Unit(this LppSensorType t) => t switch
    {
        LppSensorType.Voltage => "V",
        LppSensorType.Temperature => "°C",
        LppSensorType.Humidity or LppSensorType.Percentage => "%",
        LppSensorType.Barometer => "hPa",
        LppSensorType.Illuminance => "lux",
        LppSensorType.Current => "A",
        LppSensorType.Power => "W",
        LppSensorType.Frequency => "Hz",
        LppSensorType.Altitude or LppSensorType.Distance => "m",
        LppSensorType.Energy => "kWh",
        LppSensorType.Direction => "°",
        LppSensorType.Load => "kg",
        LppSensorType.Concentration => "ppm",
        _ => "",
    };
}

public abstract record LppValue
{
    public sealed record Digital(bool Value) : LppValue { public override string ToString() => Value ? "On" : "Off"; }
    public sealed record Integer(long Value) : LppValue { public override string ToString() => Value.ToString(); }
    public sealed record Float(double Value) : LppValue { public override string ToString() => Value.ToString("0.##"); }
    public sealed record Vector3(double X, double Y, double Z) : LppValue { public override string ToString() => $"{X:0.###}, {Y:0.###}, {Z:0.###}"; }
    public sealed record Gps(double Latitude, double Longitude, double Altitude) : LppValue { public override string ToString() => $"{Latitude:0.0000}, {Longitude:0.0000} ({Altitude:0.#} m)"; }
    public sealed record Rgb(byte R, byte G, byte B) : LppValue { public override string ToString() => $"#{R:X2}{G:X2}{B:X2}"; }
    public sealed record Timestamp(DateTimeOffset Value) : LppValue { public override string ToString() => Value.LocalDateTime.ToString("g"); }

    public double? NumericValue => this switch
    {
        Digital d => d.Value ? 1 : 0,
        Integer i => i.Value,
        Float f => f.Value,
        _ => null,
    };
}

public sealed record LppDataPoint(byte Channel, LppSensorType Type, LppValue Value)
{
    public string FormattedValue
    {
        get
        {
            var unit = Type.Unit();
            var v = Value.ToString() ?? "";
            return string.IsNullOrEmpty(unit) ? v : $"{v} {unit}";
        }
    }
}

public static class LppDecoder
{
    public static IReadOnlyList<LppDataPoint> Decode(byte[] data)
    {
        var result = new List<LppDataPoint>();
        var o = 0;
        while (o < data.Length)
        {
            if (o + 2 > data.Length) break;
            var ch = data[o];
            var code = data[o + 1];
            o += 2;
            if (!Enum.IsDefined(typeof(LppSensorType), code)) break;
            var t = (LppSensorType)code;
            var size = t.DataSize();
            if (o + size > data.Length) break;
            var v = data.Slice(o, size);
            o += size;
            if (DecodeValue(t, v) is { } value) result.Add(new LppDataPoint(ch, t, value));
        }
        return result;
    }

    private static LppValue? DecodeValue(LppSensorType t, byte[] d) => t switch
    {
        LppSensorType.DigitalInput or LppSensorType.DigitalOutput or LppSensorType.Presence or LppSensorType.Switch => new LppValue.Digital(d[0] != 0),
        LppSensorType.Percentage => new LppValue.Integer(d[0]),
        LppSensorType.Humidity => new LppValue.Float(d[0] * 0.5),
        LppSensorType.Temperature => new LppValue.Float(I16(d) / 10.0),
        LppSensorType.Barometer => new LppValue.Float(U16(d) / 10.0),
        LppSensorType.Voltage => new LppValue.Float(U16(d) / 100.0),
        LppSensorType.Current => new LppValue.Float(I16(d) / 1000.0),
        LppSensorType.Illuminance => new LppValue.Integer(U16(d)),
        LppSensorType.Altitude => new LppValue.Float(I16(d)),
        LppSensorType.Load => new LppValue.Float(I24(d, 0) / 1000.0),
        LppSensorType.Concentration or LppSensorType.Power or LppSensorType.Direction => new LppValue.Integer(U16(d)),
        LppSensorType.AnalogInput or LppSensorType.AnalogOutput => new LppValue.Float(I16(d) / 100.0),
        LppSensorType.GenericSensor or LppSensorType.Frequency => new LppValue.Integer(U32(d)),
        LppSensorType.Distance or LppSensorType.Energy => new LppValue.Float(U32(d) / 1000.0),
        LppSensorType.UnixTime => new LppValue.Timestamp(DateTimeOffset.FromUnixTimeSeconds(U32(d))),
        LppSensorType.Accelerometer => new LppValue.Vector3(I16(d, 0) / 1000.0, I16(d, 2) / 1000.0, I16(d, 4) / 1000.0),
        LppSensorType.Gyrometer => new LppValue.Vector3(I16(d, 0) / 100.0, I16(d, 2) / 100.0, I16(d, 4) / 100.0),
        LppSensorType.Colour => new LppValue.Rgb(d[0], d[1], d[2]),
        LppSensorType.Gps => new LppValue.Gps(I24(d, 0) / 10000.0, I24(d, 3) / 10000.0, I24(d, 6) / 100.0),
        _ => null,
    };

    internal static double DecodeToDouble(LppSensorType t, byte[] d) => t switch
    {
        LppSensorType.DigitalInput or LppSensorType.DigitalOutput or LppSensorType.Presence or LppSensorType.Switch => d[0],
        LppSensorType.Percentage => d[0],
        LppSensorType.Humidity => d[0] * 0.5,
        LppSensorType.Temperature => I16(d) / 10.0,
        LppSensorType.Barometer => U16(d) / 10.0,
        LppSensorType.Voltage => U16(d) / 100.0,
        LppSensorType.Current => U16(d) / 1000.0,
        LppSensorType.Illuminance or LppSensorType.Concentration or LppSensorType.Power or LppSensorType.Direction => U16(d),
        LppSensorType.Altitude => I16(d),
        LppSensorType.Load => I24(d, 0) / 1000.0,
        LppSensorType.AnalogInput or LppSensorType.AnalogOutput => I16(d) / 100.0,
        LppSensorType.GenericSensor or LppSensorType.Frequency or LppSensorType.UnixTime => U32(d),
        LppSensorType.Distance or LppSensorType.Energy => U32(d) / 1000.0,
        LppSensorType.Accelerometer => I16(d) / 1000.0,
        _ => I16(d) / 100.0,
    };

    private static short I16(byte[] d, int o = 0) => o + 2 <= d.Length ? (short)((d[o] << 8) | d[o + 1]) : (short)0;
    private static ushort U16(byte[] d, int o = 0) => o + 2 <= d.Length ? (ushort)((d[o] << 8) | d[o + 1]) : (ushort)0;
    private static uint U32(byte[] d, int o = 0) => o + 4 <= d.Length ? (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]) : 0;

    private static int I24(byte[] d, int o)
    {
        if (o + 3 > d.Length) return 0;
        var v = (d[o] << 16) | (d[o + 1] << 8) | d[o + 2];
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
        return v;
    }
}

/// <summary>Cayenne LPP encoder (big-endian values).</summary>
public sealed class LppEncoder
{
    private readonly List<byte> _buf = new();
    public int Count => _buf.Count;
    public byte[] Encode() => _buf.ToArray();
    public void Reset() => _buf.Clear();

    private void Header(byte ch, LppSensorType t) { _buf.Add(ch); _buf.Add((byte)t); }
    private void I16(short v) { _buf.Add((byte)((v >> 8) & 0xFF)); _buf.Add((byte)(v & 0xFF)); }
    private void U16(ushort v) { _buf.Add((byte)((v >> 8) & 0xFF)); _buf.Add((byte)(v & 0xFF)); }
    private void I24(int v) { _buf.Add((byte)((v >> 16) & 0xFF)); _buf.Add((byte)((v >> 8) & 0xFF)); _buf.Add((byte)(v & 0xFF)); }

    public LppEncoder AddDigitalInput(byte ch, byte v) { Header(ch, LppSensorType.DigitalInput); _buf.Add(v != 0 ? (byte)1 : (byte)0); return this; }
    public LppEncoder AddDigitalOutput(byte ch, byte v) { Header(ch, LppSensorType.DigitalOutput); _buf.Add(v != 0 ? (byte)1 : (byte)0); return this; }
    public LppEncoder AddAnalogInput(byte ch, double v) { Header(ch, LppSensorType.AnalogInput); I16((short)(v * 100)); return this; }
    public LppEncoder AddAnalogOutput(byte ch, double v) { Header(ch, LppSensorType.AnalogOutput); I16((short)(v * 100)); return this; }
    public LppEncoder AddTemperature(byte ch, double c) { Header(ch, LppSensorType.Temperature); I16((short)(c * 10)); return this; }
    public LppEncoder AddHumidity(byte ch, double pct) { Header(ch, LppSensorType.Humidity); _buf.Add((byte)(pct * 2)); return this; }
    public LppEncoder AddBarometer(byte ch, double hPa) { Header(ch, LppSensorType.Barometer); U16((ushort)(hPa * 10)); return this; }
    public LppEncoder AddIlluminance(byte ch, ushort lux) { Header(ch, LppSensorType.Illuminance); U16(lux); return this; }
    public LppEncoder AddAccelerometer(byte ch, double x, double y, double z) { Header(ch, LppSensorType.Accelerometer); I16((short)(x * 1000)); I16((short)(y * 1000)); I16((short)(z * 1000)); return this; }
    public LppEncoder AddGyrometer(byte ch, double x, double y, double z) { Header(ch, LppSensorType.Gyrometer); I16((short)(x * 100)); I16((short)(y * 100)); I16((short)(z * 100)); return this; }
    public LppEncoder AddGps(byte ch, double lat, double lon, double alt) { Header(ch, LppSensorType.Gps); I24((int)(lat * 10000)); I24((int)(lon * 10000)); I24((int)(alt * 100)); return this; }
    public LppEncoder AddVoltage(byte ch, double volts) { Header(ch, LppSensorType.Voltage); U16((ushort)(volts * 100)); return this; }
    public LppEncoder AddCurrent(byte ch, ushort mA) { Header(ch, LppSensorType.Current); U16(mA); return this; }
}

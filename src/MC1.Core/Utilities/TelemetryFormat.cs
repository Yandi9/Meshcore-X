using System.Globalization;
using MeshCore;

namespace MC1.Core.Utilities;

/// <summary>
/// Telemetry readings in the units the user chose (Settings ▸ General ▸ Metric units): °C/°F, hPa/inHg, m/ft. Port of
/// MeshCore One 1.7's locale units for temperature, pressure and distance (barometer digits follow the pressure unit).
/// </summary>
public static class TelemetryFormat
{
    public static string Label(LppDataPoint p) => L.T(p.Type.DisplayName());

    public static string Value(LppDataPoint p, bool metric)
    {
        var c = CultureInfo.CurrentCulture;
        return (p.Type, p.Value) switch
        {
            (LppSensorType.Temperature, LppValue.Float f) => metric
                ? string.Format(c, "{0:0.#} °C", f.Value)
                : string.Format(c, "{0:0.#} °F", f.Value * 9 / 5 + 32),
            (LppSensorType.Barometer, LppValue.Float f) => metric
                ? string.Format(c, "{0:0.0} hPa", f.Value)
                : string.Format(c, "{0:0.00} inHg", f.Value * 0.0295299830714),
            (LppSensorType.Altitude or LppSensorType.Distance, LppValue.Float f) => Length(f.Value, metric, c),
            (LppSensorType.Altitude or LppSensorType.Distance, LppValue.Integer i) => Length(i.Value, metric, c),
            (_, LppValue.Gps g) => string.Format(CultureInfo.InvariantCulture, "{0:0.0000}, {1:0.0000}", g.Latitude, g.Longitude)
                + " (" + Length(g.Altitude, metric, c) + ")",
            _ => p.FormattedValue,
        };
    }

    private static string Length(double meters, bool metric, CultureInfo c) => metric
        ? string.Format(c, "{0:0.#} m", meters)
        : string.Format(c, "{0:0} ft", meters * 3.28084);
}

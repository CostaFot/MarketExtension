using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace MarketExtension;

// Builds a price sparkline as an inline SVG embedded in a data: URI — the same AOT/trim-safe trick
// PowerToys' Performance Monitor uses for its CPU/RAM graphs (pure System.Xml.Linq, no
// System.Drawing/Skia). Ported from that extension's ChartHelper and generalized for price series:
// it plots every point across the width and normalizes Y to the series' own min/max (prices aren't
// 0-100 percentages), recoloring green/red by overall direction. The only caller is the Ui layer
// (UiCandleSeries) — this is a low-level string builder, like string.Format.
internal static class ChartHelper
{
    // Intrinsic SVG size; the adaptive-card Image stretches it to the card width keeping this 3:1
    // aspect. Larger than Perf Monitor's tile since this is a full detail-page chart.
    private const int ChartHeight = 200;
    private const int ChartWidth = 600;
    private const int Padding = 6; // keep the line off the edges

    private const string UpLineStyle = "fill:none;stroke:rgb(48,209,88);stroke-width:2";
    private const string DownLineStyle = "fill:none;stroke:rgb(255,69,58);stroke-width:2";
    private const string FillStyle = "fill:url(#g);stroke:transparent";

    private const string UpStop1 = "stop-color:rgb(48,209,88);stop-opacity:0.35";
    private const string UpStop2 = "stop-color:rgb(48,209,88);stop-opacity:0.02";
    private const string DownStop1 = "stop-color:rgb(255,69,58);stop-opacity:0.35";
    private const string DownStop2 = "stop-color:rgb(255,69,58);stop-opacity:0.02";

    public static string CreateImageUrl(IReadOnlyList<decimal> closes, bool up) =>
        "data:image/svg+xml;utf8," + CreateChart(closes, up);

    public static string CreateChart(IReadOnlyList<decimal> closes, bool up)
    {
        // <svg height=".." width="..">
        //   <defs><linearGradient id="g" .../></defs>
        //   <polyline points=".." style="fill:url(#g);stroke:transparent" />   (gradient under the line)
        //   <polyline points=".." style="fill:none;stroke:rgb(..)" />          (the price line)
        // </svg>
        var svg = new XElement(
            "svg",
            new XAttribute("height", ChartHeight),
            new XAttribute("width", ChartWidth));

        var line = BuildLinePoints(closes);
        if (line.Length > 0)
        {
            // Close the loop down to the baseline so the gradient fills the area under the line.
            var fill = new StringBuilder(line.ToString());
            fill.Append(CultureInfo.InvariantCulture, $" {ChartWidth - Padding},{ChartHeight - Padding}");
            fill.Append(CultureInfo.InvariantCulture, $" {Padding},{ChartHeight - Padding}");

            svg.Add(CreateGradient(up));
            svg.Add(new XElement(
                "polyline",
                new XAttribute("points", fill.ToString()),
                new XAttribute("style", FillStyle)));
            svg.Add(new XElement(
                "polyline",
                new XAttribute("points", line.ToString()),
                new XAttribute("style", up ? UpLineStyle : DownLineStyle)));
        }

        return svg.ToString(SaveOptions.DisableFormatting);
    }

    // Map the close series to "x,y x,y ..." spread across the chart width, with Y normalized to the
    // series' own min..max so the line uses the full height.
    private static StringBuilder BuildLinePoints(IReadOnlyList<decimal> closes)
    {
        var points = new StringBuilder();
        if (closes.Count == 0)
            return points;

        var min = closes.Min();
        var max = closes.Max();
        var span = max - min;

        double innerW = ChartWidth - (2 * Padding);
        double innerH = ChartHeight - (2 * Padding);

        for (var i = 0; i < closes.Count; i++)
        {
            var x = closes.Count == 1
                ? Padding + (innerW / 2)
                : Padding + (innerW * i / (closes.Count - 1));

            // 0 (=min) sits at the bottom, 1 (=max) at the top; a flat series rides mid-height.
            var norm = span == 0m ? 0.5 : (double)((closes[i] - min) / span);
            var y = Padding + (innerH * (1 - norm));

            points.Append(CultureInfo.InvariantCulture, $"{x:0.##},{y:0.##} ");
        }

        points.Remove(points.Length - 1, 1); // trailing space
        return points;
    }

    private static XElement CreateGradient(bool up) =>
        new(
            "defs",
            new XElement(
                "linearGradient",
                new XAttribute("id", "g"),
                new XAttribute("x1", "0%"),
                new XAttribute("x2", "0%"),
                new XAttribute("y1", "0%"),
                new XAttribute("y2", "100%"),
                new XElement(
                    "stop",
                    new XAttribute("offset", "0%"),
                    new XAttribute("style", up ? UpStop1 : DownStop1)),
                new XElement(
                    "stop",
                    new XAttribute("offset", "100%"),
                    new XAttribute("style", up ? UpStop2 : DownStop2))));
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AzureWorkbench.Views;

/// <summary>Tiny line chart: a polyline over a faint baseline. Null values are gaps.</summary>
public sealed class Sparkline : Control
{
    public const double DefaultWidth = 120, DefaultHeight = 32;
    IReadOnlyList<double?> _values = [];
    IBrush _stroke = VsCode.StatusBar;

    public IReadOnlyList<double?> Values
    {
        get => _values;
        set
        {
            _values = value ?? [];
            InvalidateVisual();
        }
    }

    public IBrush Stroke
    {
        get => _stroke;
        set
        {
            _stroke = value;
            InvalidateVisual();
        }
    }

    public Sparkline()
    {
        Width = DefaultWidth;
        Height = DefaultHeight;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 2 || h <= 2) return;
        const double pad = 2;
        var baseline = h - pad;
        context.DrawLine(new Pen(VsCode.Border, 1), new Point(0, baseline), new Point(w, baseline));
        var values = _values;
        if (values.Count == 0) return;
        var max = values.Where(v => v is { } x && double.IsFinite(x)).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        double X(int i) => values.Count == 1 ? w / 2 : pad + (w - 2 * pad) * i / (values.Count - 1);
        double Y(double v) => max <= 0 ? baseline : baseline - Math.Clamp(v / max, 0, 1) * (baseline - pad);
        var pen = new Pen(_stroke, 1.5, lineJoin: PenLineJoin.Round);
        var run = new List<Point>();
        void Flush()
        {
            if (run.Count == 1)
                context.DrawEllipse(_stroke, null, run[0], 1.5, 1.5);
            else if (run.Count > 1)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(run[0], false);
                    foreach (var p in run.Skip(1)) g.LineTo(p);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
            run.Clear();
        }
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is { } v && double.IsFinite(v)) run.Add(new Point(X(i), Y(v)));
            else Flush();
        }
        Flush();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(Width, availableSize.Width), Math.Min(Height, availableSize.Height));
}

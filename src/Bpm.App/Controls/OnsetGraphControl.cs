using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bpm.App.Controls;

/// <summary>
/// TuneLab-style scope: a dark panel with a faint grid, the onset energy trace of the selected
/// file, and vertical markers at the detected beat positions. Data comes straight from the
/// analysis result, so the graph is exactly what the algorithm saw.
/// </summary>
public sealed class OnsetGraphControl : Control
{
    public static readonly StyledProperty<double[]> CurveProperty =
        AvaloniaProperty.Register<OnsetGraphControl, double[]>("Curve", Array.Empty<double>());

    public static readonly StyledProperty<double[]> TicksProperty =
        AvaloniaProperty.Register<OnsetGraphControl, double[]>("Ticks", Array.Empty<double>());

    public static readonly StyledProperty<double> FrameRateProperty =
        AvaloniaProperty.Register<OnsetGraphControl, double>("FrameRate", 0.0);

    static OnsetGraphControl()
    {
        AffectsRender<OnsetGraphControl>(CurveProperty, TicksProperty, FrameRateProperty);
        ClipToBoundsProperty.OverrideDefaultValue<OnsetGraphControl>(true);
    }

    public double[] Curve
    {
        get => GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public double[] Ticks
    {
        get => GetValue(TicksProperty);
        set => SetValue(TicksProperty, value);
    }

    public double FrameRate
    {
        get => GetValue(FrameRateProperty);
        set => SetValue(FrameRateProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        context.FillRectangle(new SolidColorBrush(Color.Parse("#0B0F14")), new Rect(0, 0, w, h));

        var grid = new Pen(new SolidColorBrush(Color.Parse("#232B33")), 1, DashStyle.Dash);
        for (var i = 1; i < 4; i++)
        {
            context.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));
        }

        for (var i = 1; i < 8; i++)
        {
            context.DrawLine(grid, new Point(w * i / 8, 0), new Point(w * i / 8, h));
        }

        var curve = Curve;
        var rate = FrameRate;
        if (curve is not { Length: > 1 } || rate <= 0)
        {
            return;
        }

        var max = 0.0;
        foreach (var v in curve)
        {
            if (v > max)
            {
                max = v;
            }
        }

        if (max <= 0)
        {
            return;
        }

        var totalSeconds = curve.Length / rate;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var started = false;
            for (var i = 0; i < curve.Length; i++)
            {
                var x = (i / rate) / totalSeconds * w;
                var y = h - 4 - (curve[i] / max) * (h - 10);
                if (!started)
                {
                    g.BeginFigure(new Point(x, y), false);
                    started = true;
                }
                else
                {
                    g.LineTo(new Point(x, y));
                }
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#56D4F5")), 1.2), geometry);

        var marker = new Pen(new SolidColorBrush(Color.Parse("#FFB86C")), 1);
        foreach (var t in Ticks)
        {
            if (t < 0 || t > totalSeconds)
            {
                continue;
            }

            var x = t / totalSeconds * w;
            context.DrawLine(marker, new Point(x, h), new Point(x, h * 0.55));
        }
    }
}

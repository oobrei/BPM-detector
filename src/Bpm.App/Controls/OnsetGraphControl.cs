using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Bpm.App.Controls;

/// <summary>
/// TuneLab-style scope: a dark panel with a faint grid, the onset energy trace of the selected
/// file, and vertical markers at the detected beat positions. Data comes straight from the
/// analysis result, so the graph is exactly what the algorithm saw.
/// </summary>
/// <remarks>
/// Ctrl + mouse wheel zooms around the pointer; left-drag pans. Following the technique TuneLab
/// uses for its piano roll: the wheel only sets a TARGET view, and a frame-rate independent
/// exponential ease (k = 1 - exp(-dt / tau)) walks the view toward it, snapping once it is within
/// a hair of the target. That makes a fast flick of the wheel feel like one continuous glide
/// instead of a series of jumps. Dragging stays 1:1 with the pointer, since any easing there would
/// read as lag. The trace geometry is cached and only rebuilt when the view actually changes.
/// </remarks>
public sealed class OnsetGraphControl : Control
{
    /// <summary>Ease time constant in ms, matching the value TuneLab uses for wheel scrolling.</summary>
    private const double SmoothTauMs = 60;

    private const double MinSpanSeconds = 0.5;
    private const double SnapEpsilon = 5e-4;
    private const double ZoomBase = 1.2;

    private static readonly SolidColorBrush BackgroundBrush = new(Color.Parse("#0B0F14"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#232B33")), 1, DashStyle.Dash);
    private static readonly Pen TracePen = new(new SolidColorBrush(Color.Parse("#56D4F5")), 1.2);
    private static readonly Pen MarkerPen = new(new SolidColorBrush(Color.Parse("#FFB86C")), 1);
    private static readonly Cursor PanCursor = new(StandardCursorType.SizeWestEast);

    private readonly DispatcherTimer _animationTimer;

    // Rendered view and the view being eased toward (both in seconds).
    private double _viewStart;
    private double _viewSpan;
    private double _targetStart;
    private double _targetSpan;
    private double _lastTickMs = double.NaN;

    private bool _dragging;
    private double _dragStartX;
    private double _dragStartViewStart;
    private bool _panCursorActive;

    private double[]? _maxCurve;
    private double _maxValue;

    private StreamGeometry? _geometry;
    private double[]? _geometryCurve;
    private double _geometryRate;
    private double _geometryStart;
    private double _geometrySpan;
    private double _geometryWidth;
    private double _geometryHeight;
    private double _geometryMax;

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
        FocusableProperty.OverrideDefaultValue<OnsetGraphControl>(true);
    }

    public OnsetGraphControl()
    {
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _animationTimer.Tick += (_, _) => Step();
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // New audio: snap straight to the whole track, no easing across unrelated files.
        if (change.Property == CurveProperty || change.Property == FrameRateProperty)
        {
            _animationTimer.Stop();
            _viewStart = 0;
            _viewSpan = 0;
            _targetStart = 0;
            _targetSpan = 0;
            _geometry = null;
            InvalidateVisual();
        }
    }

    private static double TotalSeconds(double[] curve, double rate) =>
        curve is { Length: > 1 } && rate > 0 ? curve.Length / rate : 0;

    /// <summary>A zero span is shorthand for "the whole track"; resolve it before use.</summary>
    private static double Resolve(double span, double total) => span <= 0 ? total : span;

    private static double ClampStart(double start, double total, double span) =>
        Math.Clamp(start, 0, Math.Max(0, total - span));

    private double ResolveSpan(double totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return 0;
        }

        var min = Math.Min(MinSpanSeconds, totalSeconds);
        return Math.Clamp(Resolve(_viewSpan, totalSeconds), min, totalSeconds);
    }

    private double ResolveStart(double totalSeconds, double span) => ClampStart(_viewStart, totalSeconds, span);

    private double NormalisationMax(double[] curve)
    {
        if (ReferenceEquals(_maxCurve, curve))
        {
            return _maxValue;
        }

        var max = 0.0;
        foreach (var v in curve)
        {
            if (v > max)
            {
                max = v;
            }
        }

        _maxCurve = curve;
        _maxValue = max;
        return max;
    }

    private void UpdatePanCursor(bool shouldShow)
    {
        if (shouldShow == _panCursorActive)
        {
            return;
        }

        _panCursorActive = shouldShow;
        Cursor = shouldShow ? PanCursor : Cursor.Default;
    }

    /// <summary>One animation frame: exponential ease toward the target, then snap when close.</summary>
    private void Step()
    {
        var now = Environment.TickCount64;
        var delta = double.IsNaN(_lastTickMs) ? 16.0 : Math.Clamp(now - _lastTickMs, 0.0, 100.0);
        _lastTickMs = now;

        var k = 1 - Math.Exp(-delta / SmoothTauMs);
        var total = TotalSeconds(Curve, FrameRate);

        _viewSpan += (_targetSpan - _viewSpan) * k;
        _viewStart += (_targetStart - _viewStart) * k;

        // Snap at half a screen pixel rather than a fixed number of seconds: sub-pixel movement is
        // invisible, and a seconds-based threshold would keep easing long after the eye stops
        // seeing it (and would mean something different at every zoom level).
        var epsilon = SnapEpsilon;
        var width = Bounds.Width;
        var probe = Math.Abs(_viewSpan) > 0 ? Math.Abs(_viewSpan) : Math.Abs(_targetSpan);
        if (width > 1 && probe > 0)
        {
            epsilon = Math.Max(0.5 * probe / width, SnapEpsilon);
        }

        var settled = true;
        if (Math.Abs(_targetSpan - _viewSpan) < epsilon)
        {
            _viewSpan = _targetSpan;
        }
        else
        {
            settled = false;
        }

        if (Math.Abs(_targetStart - _viewStart) < epsilon)
        {
            _viewStart = _targetStart;
        }
        else
        {
            settled = false;
        }

        if (settled)
        {
            _animationTimer.Stop();
            _lastTickMs = double.NaN;

            // A span that ended at the whole track collapses back to the "fit" shorthand.
            if (total > 0 && _viewSpan >= total - SnapEpsilon)
            {
                _viewSpan = 0;
                _targetSpan = 0;
                _viewStart = 0;
                _targetStart = 0;
            }
        }

        InvalidateVisual();
    }

    private void StartAnimation()
    {
        if (_animationTimer.IsEnabled)
        {
            return;
        }

        _lastTickMs = double.NaN;
        _animationTimer.Start();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0)
        {
            return;
        }

        var total = TotalSeconds(Curve, FrameRate);
        if (total <= 0 || Bounds.Width <= 1)
        {
            return;
        }

        // Build from the target, not the rendered value, so repeated wheel events accumulate
        // instead of fighting the in-flight animation.
        var span = Math.Clamp(Resolve(_targetSpan, total), Math.Min(MinSpanSeconds, total), total);
        var start = ClampStart(_targetStart, total, span);

        var fraction = Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1);
        var anchor = start + fraction * span;

        // Exponential so fractional trackpad deltas glide and flicked wheels scale proportionally.
        var factor = Math.Pow(ZoomBase, -e.Delta.Y);
        var newSpan = Math.Clamp(span * factor, Math.Min(MinSpanSeconds, total), total);
        var newStart = newSpan >= total
            ? 0
            : ClampStart(anchor - fraction * newSpan, total, newSpan);

        _targetSpan = newSpan >= total ? 0 : newSpan;
        _targetStart = newStart;

        // Lift the rendered value out of the "fit" shorthand so easing has real endpoints.
        if (_viewSpan <= 0)
        {
            _viewSpan = total;
        }

        if (_targetSpan <= 0)
        {
            _targetSpan = total;
        }

        StartAnimation();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var total = TotalSeconds(Curve, FrameRate);
        var span = ResolveSpan(total);
        if (total <= 0 || Bounds.Width <= 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartViewStart = ResolveStart(total, span);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var total = TotalSeconds(Curve, FrameRate);
        var span = ResolveSpan(total);

        if (!_dragging)
        {
            UpdatePanCursor(total > 0 && span < total - 1e-9);
            return;
        }

        if (total <= 0 || Bounds.Width <= 1)
        {
            return;
        }

        // Absolute delta from the drag origin: the view tracks the pointer exactly with no drift.
        var deltaX = e.GetPosition(this).X - _dragStartX;
        var secondsPerPixel = span / Bounds.Width;
        var next = ClampStart(_dragStartViewStart - deltaX * secondsPerPixel, total, span);

        // Dragging is 1:1 — no easing — and cancels any zoom still in flight.
        _animationTimer.Stop();
        _lastTickMs = double.NaN;
        _viewStart = next;
        _targetStart = next;
        _viewSpan = span;
        _targetSpan = span;

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        context.FillRectangle(BackgroundBrush, new Rect(0, 0, w, h));

        for (var i = 1; i < 4; i++)
        {
            context.DrawLine(GridPen, new Point(0, h * i / 4), new Point(w, h * i / 4));
        }

        for (var i = 1; i < 8; i++)
        {
            context.DrawLine(GridPen, new Point(w * i / 8, 0), new Point(w * i / 8, h));
        }

        var curve = Curve;
        var rate = FrameRate;
        var totalSeconds = TotalSeconds(curve, rate);
        if (totalSeconds <= 0)
        {
            return;
        }

        var max = NormalisationMax(curve);
        if (max <= 0)
        {
            return;
        }

        var span = ResolveSpan(totalSeconds);
        var start = ResolveStart(totalSeconds, span);

        if (_geometry is null
            || !ReferenceEquals(_geometryCurve, curve)
            || _geometryRate != rate
            || _geometryStart != start
            || _geometrySpan != span
            || _geometryWidth != w
            || _geometryHeight != h
            || _geometryMax != max)
        {
            _geometry = BuildTrace(curve, rate, start, span, w, h, max);
            _geometryCurve = curve;
            _geometryRate = rate;
            _geometryStart = start;
            _geometrySpan = span;
            _geometryWidth = w;
            _geometryHeight = h;
            _geometryMax = max;
        }

        context.DrawGeometry(null, TracePen, _geometry);

        var end = start + span;
        foreach (var t in Ticks)
        {
            if (t < start || t > end)
            {
                continue;
            }

            var x = (t - start) / span * w;
            context.DrawLine(MarkerPen, new Point(x, h), new Point(x, h * 0.55));
        }
    }

    private static StreamGeometry BuildTrace(
        double[] curve,
        double rate,
        double start,
        double span,
        double w,
        double h,
        double max)
    {
        var firstFrame = Math.Max(0, (int)Math.Floor(start * rate) - 1);
        var lastFrame = Math.Min(curve.Length - 1, (int)Math.Ceiling((start + span) * rate) + 1);

        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        var started = false;
        for (var i = firstFrame; i <= lastFrame; i++)
        {
            var x = Math.Clamp((i / rate - start) / span * w, 0, w);
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

        if (started)
        {
            g.EndFigure(false);
        }

        return geometry;
    }
}
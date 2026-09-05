using System.Globalization;
using TurntableSpeed.App.Localization;

namespace TurntableSpeed.App.Drawing;

/// <summary>
/// Renders a <see cref="PlotModel"/>. Deliberately plain: axes, a curve, an optional marker.
/// The charts exist so the user can see whether there is a periodicity or a wow peak at all,
/// which is a question about shape, not decoration.
/// </summary>
public sealed class PlotDrawable : IDrawable
{
    private const float LeftMargin = 44f;
    private const float RightMargin = 10f;
    private const float TopMargin = 10f;
    private const float BottomMargin = 24f;

    public PlotModel Model { get; set; } = PlotModel.Empty;

    public Color CurveColor { get; set; } = Color.FromArgb("#4FC3F7");

    public Color MarkerColor { get; set; } = Color.FromArgb("#FFB74D");

    public Color GridColor { get; set; } = Color.FromArgb("#2A3038");

    public Color TextColor { get; set; } = Color.FromArgb("#8A94A0");

    /// <summary>
    /// What to draw when there is no curve yet. A canvas has no bindings, so the pages set this
    /// from the resource set on every repaint rather than once at construction.
    /// </summary>
    public string EmptyText { get; set; } = AppStrings.NoData;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var plot = new RectF(
            dirtyRect.X + LeftMargin,
            dirtyRect.Y + TopMargin,
            Math.Max(1f, dirtyRect.Width - LeftMargin - RightMargin),
            Math.Max(1f, dirtyRect.Height - TopMargin - BottomMargin));

        var model = Model;

        canvas.FillColor = Color.FromArgb("#141A21");
        canvas.FillRectangle(dirtyRect);

        if (!model.HasData)
        {
            canvas.FontSize = 12f;
            canvas.FontColor = TextColor;
            canvas.DrawString(EmptyText, dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
            return;
        }

        DrawGrid(canvas, plot, model);
        DrawReference(canvas, plot, model);
        DrawCurve(canvas, plot, model);
        DrawMarker(canvas, plot, model);
    }

    private void DrawGrid(ICanvas canvas, RectF plot, PlotModel model)
    {
        canvas.StrokeColor = GridColor;
        canvas.StrokeSize = 1f;
        canvas.FontSize = 10f;
        canvas.FontColor = TextColor;

        for (var i = 0; i <= 4; i++)
        {
            var fraction = i / 4f;

            var y = plot.Bottom - fraction * plot.Height;
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            var value = model.YMin + fraction * (model.YMax - model.YMin);
            canvas.DrawString(
                Tick(value), plot.Left - LeftMargin, y - 7f, LeftMargin - 4f, 14f,
                HorizontalAlignment.Right, VerticalAlignment.Center);

            var x = plot.Left + fraction * plot.Width;
            canvas.DrawLine(x, plot.Top, x, plot.Bottom);
            var xValue = model.XMin + fraction * (model.XMax - model.XMin);
            canvas.DrawString(
                Tick(xValue), x - 24f, plot.Bottom + 4f, 48f, 14f,
                HorizontalAlignment.Center, VerticalAlignment.Top);
        }

        if (!string.IsNullOrEmpty(model.XUnit))
        {
            canvas.DrawString(
                model.XUnit, plot.Right - 40f, plot.Bottom + 4f, 40f, 14f,
                HorizontalAlignment.Right, VerticalAlignment.Top);
        }

        if (!string.IsNullOrEmpty(model.YUnit))
        {
            canvas.DrawString(
                model.YUnit, plot.Left, plot.Top - TopMargin, 60f, 12f,
                HorizontalAlignment.Left, VerticalAlignment.Top);
        }
    }

    private void DrawReference(ICanvas canvas, RectF plot, PlotModel model)
    {
        if (model.ReferenceY is not { } reference || reference < model.YMin || reference > model.YMax)
        {
            return;
        }

        canvas.StrokeColor = Color.FromArgb("#3E4C5A");
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = [4f, 4f];
        var y = MapY(reference, plot, model);
        canvas.DrawLine(plot.Left, y, plot.Right, y);
        canvas.StrokeDashPattern = null;
    }

    private void DrawCurve(ICanvas canvas, RectF plot, PlotModel model)
    {
        var path = new PathF();
        var started = false;
        var count = Math.Min(model.X.Length, model.Y.Length);

        for (var i = 0; i < count; i++)
        {
            if (!double.IsFinite(model.X[i]) || !double.IsFinite(model.Y[i]))
            {
                continue;
            }

            var px = MapX(model.X[i], plot, model);
            var py = MapY(model.Y[i], plot, model);

            if (!started)
            {
                path.MoveTo(px, py);
                started = true;
            }
            else
            {
                path.LineTo(px, py);
            }
        }

        if (!started)
        {
            return;
        }

        if (model.FillUnderCurve)
        {
            var filled = new PathF(path);
            filled.LineTo(plot.Right, plot.Bottom);
            filled.LineTo(plot.Left, plot.Bottom);
            filled.Close();
            canvas.FillColor = CurveColor.WithAlpha(0.18f);
            canvas.FillPath(filled);
        }

        canvas.StrokeColor = CurveColor;
        canvas.StrokeSize = 1.6f;
        canvas.DrawPath(path);
    }

    private void DrawMarker(ICanvas canvas, RectF plot, PlotModel model)
    {
        if (model.MarkerX is not { } marker || marker < model.XMin || marker > model.XMax)
        {
            return;
        }

        var x = MapX(marker, plot, model);

        canvas.StrokeColor = MarkerColor;
        canvas.StrokeSize = 1.4f;
        canvas.DrawLine(x, plot.Top, x, plot.Bottom);

        if (string.IsNullOrEmpty(model.MarkerLabel))
        {
            return;
        }

        canvas.FontSize = 11f;
        canvas.FontColor = MarkerColor;

        // Keep the label inside the plot when the marker sits near the right edge.
        var labelWidth = 96f;
        var labelLeft = x + 4f + labelWidth > plot.Right ? x - 4f - labelWidth : x + 4f;
        var alignment = labelLeft < x ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        canvas.DrawString(
            model.MarkerLabel!, labelLeft, plot.Top + 2f, labelWidth, 14f,
            alignment, VerticalAlignment.Top);
    }

    private static float MapX(double value, RectF plot, PlotModel model)
    {
        var span = model.XMax - model.XMin;
        var fraction = span > 0.0 ? (value - model.XMin) / span : 0.5;
        return plot.Left + (float)(fraction * plot.Width);
    }

    private static float MapY(double value, RectF plot, PlotModel model)
    {
        var span = model.YMax - model.YMin;
        var fraction = span > 0.0 ? (value - model.YMin) / span : 0.5;
        return plot.Bottom - (float)(fraction * plot.Height);
    }

    private static string Tick(double value)
    {
        var magnitude = Math.Abs(value);
        var format = magnitude switch
        {
            0.0 => "0",
            < 0.01 => "0.###E+0",
            < 1.0 => "0.###",
            < 100.0 => "0.##",
            _ => "0",
        };

        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}

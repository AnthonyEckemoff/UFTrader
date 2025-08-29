using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.Generic;

public class MainViewModel
{
    public IEnumerable<ISeries> Series { get; set; }
    public IEnumerable<Axis> XAxes { get; set; }
    public IEnumerable<Axis> YAxes { get; set; }

    public MainViewModel()
    {
        Series = new List<ISeries>
        {
            new LineSeries<double>
            {
                Values = new double[] { 3, 1, 4, 6, 5, 3, 8 },
                Fill = null, // no area fill
                Stroke = new SolidColorPaint(SKColors.DodgerBlue, 2)
            }
        };

        XAxes = new List<Axis>
        {
            new Axis { LabelsRotation = 15 }
        };

        YAxes = new List<Axis>
        {
            new Axis()
        };
    }
}

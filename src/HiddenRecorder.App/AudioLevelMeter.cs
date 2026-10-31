using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HiddenRecorder.App;

/// <summary>Read-only vertical dBFS meter. Threshold uses the same scale as the live level.</summary>
public sealed class AudioLevelMeter : UserControl
{
    private readonly Grid scale = new() { Margin = new Thickness(0, 10, 0, 10) };
    private readonly Border fill = new() { Width = 20, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(36, 0, 0, 0), Background = new SolidColorBrush(Colors.MediumSeaGreen) };
    private readonly Border marker = new() { Width = 36, Height = 3, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Colors.Orange) };
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(AudioLevelMeter), new PropertyMetadata(-90d, Changed));
    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(nameof(Threshold), typeof(double), typeof(AudioLevelMeter), new PropertyMetadata(-25d, Changed));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Threshold { get => (double)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public AudioLevelMeter()
    {
        Width = 78; MinHeight = 160; IsTabStop = false; IsHitTestVisible = false;
        scale.Children.Add(new Border { Width = 20, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(36, 0, 0, 0), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 48, 60)) });
        scale.Children.Add(fill); scale.Children.Add(marker);
        foreach (var db in new[] { 0, -30, -60, -90 })
        {
            var label = new TextBlock { Text = db.ToString(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
            scale.Children.Add(label); scale.SizeChanged += (_, _) => label.Margin = new Thickness(0, 0, 0, Fraction(db) * Math.Max(0, scale.ActualHeight - 16));
        }
        Content = scale; scale.SizeChanged += (_, _) => Update();
    }
    private static double Fraction(double db) => Math.Clamp((double.IsFinite(db) ? db : -90) + 90, 0, 90) / 90;
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((AudioLevelMeter)sender).Update();
    private void Update()
    {
        fill.Height = scale.ActualHeight * Fraction(Level);
        marker.Margin = new Thickness(28, 0, 0, Math.Max(0, (scale.ActualHeight - marker.Height) * Fraction(Threshold)));
        AutomationProperties.SetName(this, $"Микрофон {Level:0.0} dBFS, порог {Threshold:0.0} dBFS");
    }
}

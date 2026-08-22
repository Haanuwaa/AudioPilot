using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioPilot.Controls;
using AudioPilot.Tests.Helpers;
using Path = System.Windows.Shapes.Path;

namespace AudioPilot.Tests.Controls;

public sealed class AppMenuGlyphsTests
{
    [Fact]
    public void Catalog_ExposesOnlyValidFrozenGeometry()
    {
        PropertyInfo[] properties = typeof(AppMenuGlyphs).GetProperties(BindingFlags.Public | BindingFlags.Static);

        Assert.NotEmpty(properties);
        foreach (PropertyInfo property in properties)
        {
            Geometry geometry = Assert.IsType<Geometry>(property.GetValue(null), exactMatch: false);
            Assert.True(geometry.IsFrozen, $"{property.Name} geometry must be frozen so menu instances can share it safely.");
            Assert.False(geometry.Bounds.IsEmpty, $"{property.Name} geometry must have visible bounds.");
        }
    }

    [Fact]
    public void MenuTemplate_PropagatesForegroundToGlyphStroke()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current
                ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            ResourceDictionary theme = new()
            {
                Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative),
            };
            application.Resources.MergedDictionaries.Add(theme);
            try
            {
                Path glyph = new()
                {
                    Data = AppMenuGlyphs.Delete,
                    Height = 14d,
                    Width = 14d,
                };
                glyph.SetBinding(
                    Shape.StrokeProperty,
                    new Binding
                    {
                        Path = new PropertyPath("(0)", TextElement.ForegroundProperty),
                        RelativeSource = RelativeSource.Self,
                    });

                MenuItem item = new()
                {
                    Foreground = Brushes.Magenta,
                    Icon = glyph,
                    Header = "Delete",
                    Style = Assert.IsType<Style>(theme["AppContextMenuItemStyle"]),
                };
                Border host = new() { Child = item };

                host.Measure(new Size(240d, 40d));
                host.Arrange(new Rect(0d, 0d, 240d, 40d));
                item.ApplyTemplate();
                item.UpdateLayout();

                Assert.Same(Brushes.Magenta, glyph.Stroke);

                item.Foreground = Brushes.Cyan;
                BindingOperations.GetBindingExpression(glyph, Shape.StrokeProperty)?.UpdateTarget();
                Assert.Same(Brushes.Cyan, glyph.Stroke);
            }
            finally
            {
                application.Resources.MergedDictionaries.Remove(theme);
            }
        });
    }
}

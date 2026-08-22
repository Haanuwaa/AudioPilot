using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using AudioPilot.Controls;
using AudioPilot.Tests.Helpers;
using Path = System.Windows.Shapes.Path;

namespace AudioPilot.Tests.Controls;

[Collection("WpfApplicationIsolation")]
public sealed class AppMenuGlyphsTests
{
    [VisualIntegrationTheory]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    [InlineData("MainWindow.xaml")]
    [InlineData("RoutineEditorWindow.xaml")]
    public void EditableMenuDelete_PreservesSurroundingTextAndSupportsUndo(string fileName)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(System.IO.Path.Combine(root.FullName, "AudioPilot.sln")))
            {
                root = root.Parent;
            }
            Assert.NotNull(root);
            XDocument document = XDocument.Load(System.IO.Path.Combine(root.FullName, "AudioPilot", fileName));
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement source = new(Assert.Single(document.Descendants(), element =>
                (string?)element.Attribute(xaml + "Key") == "EditableTextContextMenu"));
            source.Attribute(xaml + "Key")!.Remove();
            foreach (XAttribute icon in source.Descendants().Attributes("Icon"))
            {
                icon.Remove();
            }

            var menu = Assert.IsType<ContextMenu>(XamlReader.Parse(source.ToString()));
            var textBox = new TextBox { Text = "Keep selected suffix", ContextMenu = menu };
            var window = new Window { Content = textBox, Width = 360, Height = 120, ShowActivated = false, ShowInTaskbar = false };
            menu.PlacementTarget = textBox;
            try
            {
                window.Show();
                window.UpdateLayout();
                menu.IsOpen = true;
                menu.UpdateLayout();
                MenuItem delete = Assert.Single(menu.Items.OfType<MenuItem>(), item =>
                    item.Command is RoutedCommand { Name: "Delete" });
                delete.GetBindingExpression(MenuItem.CommandTargetProperty)!.UpdateTarget();
                Assert.Same(textBox, delete.CommandTarget);
                var command = Assert.IsType<RoutedCommand>(delete.Command, exactMatch: false);

                textBox.Select(5, 9);
                Assert.True(command.CanExecute(null, delete.CommandTarget));
                command.Execute(null, delete.CommandTarget);
                Assert.Equal("Keep suffix", textBox.Text);
                Assert.True(textBox.CanUndo);
                textBox.Undo();
                Assert.Equal("Keep selected suffix", textBox.Text);

                textBox.IsReadOnly = true;
                command.Execute(null, delete.CommandTarget);
                Assert.Equal("Keep selected suffix", textBox.Text);
            }
            finally
            {
                menu.IsOpen = false;
                window.Close();
            }
        });
    }

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

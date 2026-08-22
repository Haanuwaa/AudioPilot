using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

public sealed class DeferredTabContentBehaviorTests
{
    [Fact]
    public void Template_DoesNotCreateContentBeforeSelection()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            TabItem tabItem = new();
            DataTemplate template = CreateTemplate();

            DeferredTabContentBehavior.SetTemplate(tabItem, template);

            Assert.Null(tabItem.Content);
            Assert.Same(template, DeferredTabContentBehavior.GetTemplate(tabItem));
        });
    }

    [Fact]
    public void Selected_LoadsContentOnceAndReleasesTemplate()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            TabItem tabItem = new();
            DeferredTabContentBehavior.SetTemplate(tabItem, CreateTemplate());

            tabItem.RaiseEvent(new RoutedEventArgs(Selector.SelectedEvent, tabItem));

            Border loadedContent = Assert.IsType<Border>(tabItem.Content);
            Assert.Equal("loaded", loadedContent.Tag);
            Assert.Null(DeferredTabContentBehavior.GetTemplate(tabItem));

            tabItem.RaiseEvent(new RoutedEventArgs(Selector.SelectedEvent, tabItem));
            Assert.Same(loadedContent, tabItem.Content);
        });
    }

    [Fact]
    public void Template_LoadsImmediatelyForAlreadySelectedTab()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            TabItem tabItem = new() { IsSelected = true };

            DeferredTabContentBehavior.SetTemplate(tabItem, CreateTemplate());

            Assert.IsType<Border>(tabItem.Content);
            Assert.Null(DeferredTabContentBehavior.GetTemplate(tabItem));
        });
    }

    [Fact]
    public void Selection_PreservesExistingContent()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Border existingContent = new() { Tag = "existing" };
            TabItem tabItem = new() { Content = existingContent };
            DeferredTabContentBehavior.SetTemplate(tabItem, CreateTemplate());

            tabItem.RaiseEvent(new RoutedEventArgs(Selector.SelectedEvent, tabItem));

            Assert.Same(existingContent, tabItem.Content);
            Assert.Null(DeferredTabContentBehavior.GetTemplate(tabItem));
        });
    }

    private static DataTemplate CreateTemplate()
    {
        const string xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border Tag="loaded" />
            </DataTemplate>
            """;

        return Assert.IsType<DataTemplate>(XamlReader.Parse(xaml));
    }
}

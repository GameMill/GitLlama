using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitLlama.Desktop.Views;
using Xunit;

namespace GitLlama.Desktop.Tests;

public sealed class CategoryResizeTests
{
    [AvaloniaFact]
    public void DragResizesCategoryAndCollapsePreservesItsHeight()
    {
        var list = new ListBox { ItemsSource = Enumerable.Range(1, 80).ToArray(), MaxHeight = 180 };
        var category = new ResizableCategoryPanel(list, 180);
        var expander = new Expander { Header = "Tags", IsExpanded = true, Content = category };
        var window = new Window
        {
            Width = 360,
            Height = 700,
            Content = new ScrollViewer { Content = expander },
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grip = category.Children.OfType<Border>().Single();
            Assert.True(grip.Bounds.Height >= 12);
            var divider = Assert.IsType<Border>(grip.Child);
            var point = divider.TranslatePoint(new Point(100, divider.Bounds.Height / 2), window)!.Value;
            var before = list.Bounds.Height;
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(0, 100));
            window.MouseUp(point + new Vector(0, 100), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(list.Bounds.Height - before, 99, 101);
            var resized = list.Bounds.Height;
            expander.IsExpanded = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(grip.IsEffectivelyVisible);
            expander.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(resized, list.Bounds.Height);

            grip.Focus();
            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(list.Bounds.Height, 179, 181);
        }
        finally
        {
            window.Close();
        }
    }
}

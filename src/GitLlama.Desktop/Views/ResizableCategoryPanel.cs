using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace GitLlama.Desktop.Views;

/// <summary>A category's scrollable content with a bottom resize grip.</summary>
public sealed class ResizableCategoryPanel : Grid
{
    private readonly Control viewport;
    private readonly double defaultMaximum;
    private double startY;
    private double startHeight;
    private IPointer? dragging;

    public ResizableCategoryPanel(Control content, double maximum = 260)
    {
        defaultMaximum = maximum;
        RowDefinitions = new RowDefinitions("Auto,12");
        // The category owns its scrolling boundary; old list caps must not prevent growing it.
        foreach (var child in content.GetLogicalDescendants().OfType<Control>().Prepend(content))
        {
            child.MaxHeight = double.PositiveInfinity;
        }

        // Grid categories can pin their search/header and let the star-sized list scroll independently.
        viewport = content is ListBox or TreeView or Grid || content is RemoteTreeView
            ? content
            : new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };
        viewport.MaxHeight = maximum;
        Children.Add(viewport);

        var divider = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#41434A")),
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var grip = new Border
        {
            Name = "CategoryResizeGrip",
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Focusable = true,
            Child = divider,
        };
        void UpdateDivider() => divider.Background = new SolidColorBrush(
            Color.Parse(dragging is not null || grip.IsPointerOver ? "#6D8EB8" : "#41434A"));
        grip.PointerEntered += (_, _) => UpdateDivider();
        grip.PointerExited += (_, _) => UpdateDivider();
        SetRow(grip, 1);
        AutomationProperties.SetName(grip, "Resize category");
        ToolTip.SetTip(grip, "Drag to resize · Double-click to reset · Arrow keys to adjust");
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
                return;
            startY = e.GetPosition(TopLevel.GetTopLevel(this)).Y;
            startHeight = viewport.Bounds.Height;
            dragging = e.Pointer;
            UpdateDivider();
            e.Pointer.Capture(grip);
            grip.Focus();
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (dragging != e.Pointer)
                return;
            Resize(startHeight + e.GetPosition(TopLevel.GetTopLevel(this)).Y - startY);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (dragging != e.Pointer)
                return;
            dragging = null;
            UpdateDivider();
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) =>
        {
            dragging = null;
            UpdateDivider();
        };
        grip.DoubleTapped += (_, e) =>
        {
            Reset();
            e.Handled = true;
        };
        grip.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down)
                Resize(viewport.Bounds.Height + (e.Key == Key.Up ? -24 : 24));
            else if (e.Key == Key.Home)
                Reset();
            else
                return;
            e.Handled = true;
        };
        Children.Add(grip);
    }

    private void Resize(double height)
    {
        viewport.MaxHeight = double.PositiveInfinity;
        viewport.Height = Math.Clamp(height, 40, 1600);
    }

    private void Reset()
    {
        viewport.Height = double.NaN;
        viewport.MaxHeight = defaultMaximum;
    }
}

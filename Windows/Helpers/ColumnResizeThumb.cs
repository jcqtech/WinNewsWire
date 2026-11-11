using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace WinNewsWire.Helpers;

public sealed class ColumnResizeThumb : UserControl
{
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.Register(
        nameof(Column), typeof(Microsoft.UI.Xaml.Controls.ColumnDefinition), typeof(ColumnResizeThumb), new PropertyMetadata(null));

    public Microsoft.UI.Xaml.Controls.ColumnDefinition? Column
    {
        get => (Microsoft.UI.Xaml.Controls.ColumnDefinition?)GetValue(ColumnProperty);
        set => SetValue(ColumnProperty, value);
    }

    public event EventHandler? WidthChanged;
    public event EventHandler? ResizeCompleted;

    public ColumnResizeThumb()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        var thumb = new Thumb { IsTabStop = false, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        AutomationProperties.SetAccessibilityView(thumb, AccessibilityView.Raw);
        Content = thumb;
        thumb.DragStarted += (_, _) => Focus(FocusState.Pointer);
        thumb.DragDelta += (_, args) =>
        {
            if (Column is { } column) SetWidth(Math.Clamp(column.Width.Value + args.HorizontalChange, column.MinWidth, column.MaxWidth));
        };
        thumb.DragCompleted += (_, _) => ResizeCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs args)
    {
        if (Column is { } column)
        {
            bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                & global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            double step = shift ? 32 : 8;
            double? width = args.Key switch
            {
                VirtualKey.Left => column.Width.Value - step,
                VirtualKey.Right => column.Width.Value + step,
                VirtualKey.Home => column.MinWidth,
                VirtualKey.End => column.MaxWidth,
                _ => null
            };
            if (width is { } value)
            {
                SetWidth(Math.Clamp(value, column.MinWidth, column.MaxWidth));
                ResizeCompleted?.Invoke(this, EventArgs.Empty);
                args.Handled = true;
                return;
            }
        }
        base.OnKeyDown(args);
    }

    private void SetWidth(double value)
    {
        var column = Column ?? throw new InvalidOperationException("The resize handle has no column.");
        if (!IsEnabled) throw new InvalidOperationException("The resize handle is disabled.");
        if (!double.IsFinite(value) || value < column.MinWidth || value > column.MaxWidth)
            throw new ArgumentOutOfRangeException(nameof(value));
        double oldValue = column.Width.Value;
        column.Width = new GridLength(value);
        WidthChanged?.Invoke(this, EventArgs.Empty);
        FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
            RangeValuePatternIdentifiers.ValueProperty, oldValue, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ResizePeer(this);

    private sealed class ResizePeer(ColumnResizeThumb owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(ColumnResizeThumb);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
        protected override object GetPatternCore(PatternInterface patternInterface)
            => patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);
        public bool IsReadOnly => !owner.IsEnabled || owner.Column is null;
        public double LargeChange => 32;
        public double SmallChange => 8;
        public double Maximum => owner.Column?.MaxWidth ?? 0;
        public double Minimum => owner.Column?.MinWidth ?? 0;
        public double Value => owner.Column?.Width.Value ?? 0;
        public void SetValue(double value)
        {
            owner.SetWidth(value);
            owner.ResizeCompleted?.Invoke(owner, EventArgs.Empty);
        }
    }
}

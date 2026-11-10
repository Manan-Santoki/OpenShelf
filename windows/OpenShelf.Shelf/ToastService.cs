using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using OpenShelf.Core;

namespace OpenShelf.Shelf;

/// <summary>Small click-through confirmation card. Used on the WPF UI thread.</summary>
public sealed class ToastService : IToastService
{
    const double Pad = 14;
    ToastWindow? _current;

    public void Show(string message, ScreenPoint near)
    {
        try
        {
            _current?.Dismiss();
            var w = new ToastWindow(message);
            _current = w;
            w.Closed += (_, _) => { if (ReferenceEquals(_current, w)) _current = null; };
            w.ShowNear(near);
        }
        catch (Exception ex) { Log.Error("Toast", ex); }
    }

    sealed class ToastWindow : Window
    {
        readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(1600) };
        IntPtr _hwnd;
        bool _dismissed;

        public ToastWindow(string message)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI");
            Focusable = false;
            IsHitTestVisible = false;
            Opacity = 0;

            Content = new Border
            {
                Margin = new Thickness(Pad),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 8, 14, 8),
                Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x26, 0x26, 0x29)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x29, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 14, ShadowDepth = 4, Direction = 270, Opacity = 0.4, Color = Colors.Black
                },
                Child = new TextBlock
                {
                    Text = message, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                },
            };

            SourceInitialized += (_, _) =>
            {
                try
                {
                    _hwnd = new WindowInteropHelper(this).Handle;
                    Native.AddExStyle(_hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE |
                                             Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED);
                }
                catch (Exception ex) { Log.Error("Toast SourceInitialized", ex); }
            };
            _hold.Tick += (_, _) => { _hold.Stop(); FadeOutAndClose(); };
        }

        public void ShowNear(ScreenPoint near)
        {
            Show();
            try
            {
                UpdateLayout();
                var (scale, work) = Native.MonitorAt(near);
                int w = (int)Math.Round(ActualWidth * scale);
                int h = (int)Math.Round(ActualHeight * scale);
                int x = near.X + (int)Math.Round((14 - Pad) * scale);
                int y = near.Y + (int)Math.Round((18 - Pad) * scale);
                (x, y) = Native.Clamp(x, y, w, h, work);
                Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Native.BringToTop(_hwnd);
            }
            catch (Exception ex) { Log.Error("Toast placement", ex); }

            if (SystemParameters.ClientAreaAnimation)
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            }
            else
            {
                Opacity = 1;
            }
            _hold.Start();
        }

        void FadeOutAndClose()
        {
            if (_dismissed) return;
            if (!SystemParameters.ClientAreaAnimation) { SafeClose(); return; }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => SafeClose();
            BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>Closes immediately (a newer toast replaces this one).</summary>
        public void Dismiss()
        {
            _hold.Stop();
            SafeClose();
        }

        void SafeClose()
        {
            if (_dismissed) return;
            _dismissed = true;
            try { _hold.Stop(); BeginAnimation(OpacityProperty, null); Close(); }
            catch (Exception ex) { Log.Error("Toast close", ex); }
        }
    }
}

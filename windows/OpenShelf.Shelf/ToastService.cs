using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Shelf;

/// <summary>Small click-through confirmation card. Used on the WPF UI thread.</summary>
public sealed class ToastService : IToastService
{
    const double Pad = GlassCard.Pad;
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
        readonly string _message;
        readonly Grid _host = new();
        readonly TranslateTransform _rise = new(0, 6);
        IntPtr _hwnd;
        bool _dismissed;

        public ToastWindow(string message)
        {
            _message = message;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Theme.TextFont;
            Focusable = false;
            IsHitTestVisible = false;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

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
            double scale = 1;
            try
            {
                var text = new TextBlock
                {
                    Text = _message, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.White(0.95),
                    Margin = new Thickness(14, 8, 14, 8),
                };
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double cardW = Math.Ceiling(text.DesiredSize.Width) + 2;
                double cardH = Math.Ceiling(text.DesiredSize.Height) + 2;

                var (s, work) = Native.MonitorAt(near);
                scale = s;
                int winW = (int)Math.Round((cardW + 2 * Pad) * scale);
                int winH = (int)Math.Round((cardH + 2 * Pad) * scale);
                int x = near.X + (int)Math.Round((14 - Pad) * scale);
                int y = near.Y + (int)Math.Round((18 - Pad) * scale);
                (x, y) = Native.Clamp(x, y, winW, winH, work);

                BuildContent(text, cardW, cardH, x, y, scale);

                Show();
                Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, winW, winH, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Native.BringToTop(_hwnd);
            }
            catch (Exception ex)
            {
                Log.Error("Toast placement", ex);
                if (!IsVisible) { try { Show(); } catch (Exception) { } }
            }

            Motion.Animate(_host, OpacityProperty, 1, Motion.Enabled ? 160 : 0, Motion.EaseOut, from: 0);
            Motion.Animate(_rise, TranslateTransform.YProperty, 0, 260, Motion.Spring, from: 6);
            _hold.Start();
        }

        void BuildContent(TextBlock text, double cardW, double cardH, int winX, int winY, double scale)
        {
            _host.Opacity = 0;
            _host.RenderTransform = _rise;
            if (!Motion.Enabled) _rise.Y = 0;

            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(14, 8, 14, 8);
            GlassCard.Fill(_host, text, cardW, cardH, winX, winY, scale, hitTestable: false);
            Content = _host;
        }

        void FadeOutAndClose()
        {
            if (_dismissed) return;
            if (!Motion.Enabled) { SafeClose(); return; }
            Motion.Animate(_host, OpacityProperty, 0, 180, Motion.EaseOut, completed: SafeClose);
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
            try { _hold.Stop(); _host.BeginAnimation(OpacityProperty, null); Close(); }
            catch (Exception ex) { Log.Error("Toast close", ex); }
        }
    }
}

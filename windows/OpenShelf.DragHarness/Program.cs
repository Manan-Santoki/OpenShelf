using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenShelf.Core;

namespace OpenShelf.DragHarness;

internal sealed class Options
{
    public int WindowX = 200, WindowY = 200;
    public int WheelX = 900, WheelY = 500;
    public string Keys = "shift";
    public int HoldMs = 40, SettleMs = 500;
    public int Wedge = 0, Wedges = 6;
    public string Expect = "none";
    public int WaitMs = 4000, TimeoutMs = 20000;
    public bool Keep;
    public string Press = "";
    public bool Shake;
    public int PauseMs = 0;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--window-x": o.WindowX = int.Parse(Next()); break;
                case "--window-y": o.WindowY = int.Parse(Next()); break;
                case "--wheel-x": o.WheelX = int.Parse(Next()); break;
                case "--wheel-y": o.WheelY = int.Parse(Next()); break;
                case "--keys": o.Keys = Next(); break;
                case "--hold-ms": o.HoldMs = int.Parse(Next()); break;
                case "--settle-ms": o.SettleMs = int.Parse(Next()); break;
                case "--wedge": o.Wedge = int.Parse(Next()); break;
                case "--wedges": o.Wedges = int.Parse(Next()); break;
                case "--expect": o.Expect = Next(); break;
                case "--wait-ms": o.WaitMs = int.Parse(Next()); break;
                case "--timeout-ms": o.TimeoutMs = int.Parse(Next()); break;
                case "--keep": o.Keep = true; break;
                case "--press": o.Press = Next(); break;
                case "--shake": o.Shake = true; break;
                case "--pause-ms": o.PauseMs = int.Parse(Next()); break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return o;
    }
}

internal static class Native
{
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint KEYEVENTF_KEYUP = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hwnd, System.Text.StringBuilder sb, int max);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}

internal sealed class Harness
{
    private readonly Options _o;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _logLock = new();
    private readonly HashSet<ushort> _keysDown = [];
    private volatile bool _mouseDown;
    private int _x, _y;
    private Form? _form;
    private DragDropEffects? _effect;
    private readonly ManualResetEventSlim _formShown = new();
    private readonly ManualResetEventSlim _dragReturned = new();
    private readonly ManualResetEventSlim _dragStarted = new();
    private string _folder = "";
    private string _png = "";

    public Harness(Options o) => _o = o;

    public void Log(string message)
    {
        lock (_logLock) Console.WriteLine($"[{_clock.ElapsedMilliseconds,6} ms] {message}");
    }

    // ---- input ------------------------------------------------------------------------------

    private static void Send(Native.INPUT input) =>
        Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>());

    private void MoveTo(int x, int y)
    {
        int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
        int vw = Math.Max(2, Native.GetSystemMetrics(78)), vh = Math.Max(2, Native.GetSystemMetrics(79));
        var nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        var input = new Native.INPUT { type = Native.INPUT_MOUSE };
        input.u.mi = new Native.MOUSEINPUT
        {
            dx = nx, dy = ny,
            dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
        };
        Send(input);
        _x = x; _y = y;
    }

    private static void MouseButton(bool down)
    {
        var input = new Native.INPUT { type = Native.INPUT_MOUSE };
        input.u.mi = new Native.MOUSEINPUT { dwFlags = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP };
        Send(input);
    }

    private void LeftDown() { _mouseDown = true; MouseButton(true); }
    private void LeftUp() { MouseButton(false); _mouseDown = false; }

    private void Key(ushort vk, bool down)
    {
        var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        input.u.ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : Native.KEYEVENTF_KEYUP };
        if (down) _keysDown.Add(vk);
        Send(input);
        if (!down) _keysDown.Remove(vk);
    }

    private void MoveSteps(int toX, int toY)
    {
        var fromX = _x; var fromY = _y;
        var dist = Math.Sqrt((toX - fromX) * (double)(toX - fromX) + (toY - fromY) * (double)(toY - fromY));
        var steps = Math.Max(10, (int)Math.Ceiling(dist / 6));
        for (var i = 1; i <= steps; i++)
        {
            MoveTo(fromX + (int)Math.Round((toX - fromX) * (double)i / steps),
                   fromY + (int)Math.Round((toY - fromY) * (double)i / steps));
            Thread.Sleep(15);
        }
    }

    /// <summary>Waits while nudging the pointer by 1 px every 30 ms so OLE re-evaluates the drop target.</summary>
    private void Jiggle(int ms)
    {
        var baseX = _x; var baseY = _y;
        var sw = Stopwatch.StartNew();
        var flip = false;
        while (sw.ElapsedMilliseconds < ms)
        {
            flip = !flip;
            MoveTo(baseX + (flip ? 1 : 0), baseY);
            Thread.Sleep(30);
        }
        MoveTo(baseX, baseY);
    }

    private static ushort KeyCode(string name) => name.Trim().ToLowerInvariant() switch
    {
        "shift" => 0x10,
        "ctrl" or "control" => 0x11,
        "alt" => 0x12,
        "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
        "return" or "enter" => 0x0D, "esc" => 0x1B, "tab" => 0x09,
        var d when d.Length == 1 && char.IsDigit(d[0]) => (ushort)d[0],
        _ => throw new ArgumentException($"unknown key '{name}'"),
    };

    private void Tap(string spec)
    {
        // "alt+1" holds the modifiers named before the last part.
        var parts = spec.Split('+', StringSplitOptions.TrimEntries);
        var mods = parts[..^1].Select(KeyCode).ToArray();
        foreach (var m in mods) Key(m, true);
        var vk = KeyCode(parts[^1]);
        Key(vk, true);
        Thread.Sleep(60);
        Key(vk, false);
        foreach (var m in mods.Reverse()) Key(m, false);
    }

    private void ShakePointer()
    {
        Log("shake");
        var x0 = _x; var y0 = _y;
        for (var r = 0; r < 5; r++)
        {
            var target = x0 + (r % 2 == 0 ? 80 : -80);
            var from = _x;
            for (var i = 1; i <= 8; i++) { MoveTo(from + (target - from) * i / 8, y0); Thread.Sleep(8); }
        }
        MoveTo(x0, y0);
    }

    /// <summary>Releases anything this process is holding down.</summary>
    public void ReleaseAll()
    {
        foreach (var vk in _keysDown.ToArray()) { try { Key(vk, false); } catch { } }
        if (_mouseDown) { try { LeftUp(); } catch { } }
    }

    // ---- window -----------------------------------------------------------------------------

    private void RunWindow()
    {
        Application.EnableVisualStyles();
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            TopMost = true,
            ShowInTaskbar = false,
            BackColor = Color.SteelBlue,
            Text = "OpenShelf drag harness",
        };
        form.AutoScaleMode = AutoScaleMode.None;
        form.Bounds = new Rectangle(_o.WindowX, _o.WindowY, 220, 160);
        form.Paint += (_, e) =>
        {
            using var f = new Font("Segoe UI", 10);
            e.Graphics.DrawString("Harness Photo.png\n(drag source)", f, Brushes.White, 12, 12);
        };
        form.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _effect is not null) return;
            Log("MouseDown in window; calling DoDragDrop");
            _dragStarted.Set();
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { _png });
            try
            {
                _effect = form.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                Log($"DoDragDrop threw: {ex.Message}");
                _effect = DragDropEffects.None;
            }
            Log($"DoDragDrop returned {_effect}");
            _dragReturned.Set();
        };
        form.Shown += (_, _) => _formShown.Set();
        _form = form;
        Application.Run(form);
    }

    // ---- clipboard / folder checks ----------------------------------------------------------

    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }

    private string? ClipboardText() => OnSta(() => { try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { return null; } });

    private string[]? ClipboardFiles() => OnSta(() =>
    {
        try { return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : null; }
        catch { return null; }
    });

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private string? NewFileWithExtension(string ext) =>
        Directory.EnumerateFileSystemEntries(_folder)
            .Where(p => !SamePath(p, _png))
            .FirstOrDefault(p =>
            {
                var n = Path.GetFileName(p);
                return n.StartsWith("Harness Photo", StringComparison.OrdinalIgnoreCase)
                    && n.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase);
            });

    // ---- main flow --------------------------------------------------------------------------

    public int Run()
    {
        var failure = (string?)null;
        Thread? windowThread = null;
        try
        {
            Log($"options: window=({_o.WindowX},{_o.WindowY}) wheel=({_o.WheelX},{_o.WheelY}) keys={_o.Keys} wedge={_o.Wedge}/{_o.Wedges} expect={_o.Expect}");
            _folder = Path.Combine(Path.GetTempPath(), "openshelf-harness-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_folder);
            _png = Path.Combine(_folder, "Harness Photo.png");
            using (var bmp = new Bitmap(400, 300))
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 400, 300), Color.OrangeRed, Color.RoyalBlue, 45f))
            {
                g.FillRectangle(brush, 0, 0, 400, 300);
                bmp.Save(_png, ImageFormat.Png);
            }
            Log($"temp folder: {_folder}");
            Log($"created {_png} ({new FileInfo(_png).Length} bytes)");

            if (_o.Expect.StartsWith("clipboard"))
            {
                OnSta(() => { try { Clipboard.Clear(); } catch { } return 0; });
                Log("clipboard cleared");
            }

            windowThread = new Thread(RunWindow) { IsBackground = true, Name = "window" };
            windowThread.SetApartmentState(ApartmentState.STA);
            windowThread.Start();
            if (!_formShown.Wait(5000)) throw new Exception("window did not show");
            Rectangle bounds = default;
            _form!.Invoke(() => bounds = _form.Bounds);
            var centre = new System.Drawing.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            Log($"window bounds {bounds} centre ({centre.X},{centre.Y})");
            Thread.Sleep(600);

            var hmon = Native.MonitorFromPoint(new Native.POINT { X = _o.WheelX, Y = _o.WheelY }, 2);
            Native.GetDpiForMonitor(hmon, 0, out var dpiX, out _);
            var scale = (dpiX == 0 ? 96 : dpiX) / 96.0;
            Log($"DPI at wheel point: {dpiX} (scale {scale:0.##})");

            Exception? inputError = null;
            var input = new Thread(() =>
            {
                try { DriveInput(centre, scale); }
                catch (Exception ex) { inputError = ex; }
            }) { Name = "input" };
            input.Start();
            input.Join();
            if (inputError is not null) throw inputError;

            if (!_dragReturned.Wait(3000)) Log("DoDragDrop had not returned 3 s after release");
            Log($"drag effect: {(_effect?.ToString() ?? "(none recorded)")}");

            failure = WaitForOutcome();
        }
        catch (Exception ex)
        {
            failure = "exception: " + ex.Message;
            Log(ex.ToString());
        }
        finally
        {
            ReleaseAll();
            try { _form?.Invoke(() => _form.Close()); } catch { }
            windowThread?.Join(2000);
        }

        if (failure is null)
        {
            Log("outcome as expected");
            if (!_o.Keep)
            {
                try { Directory.Delete(_folder, true); Log("temp folder deleted"); } catch (Exception ex) { Log($"could not delete temp folder: {ex.Message}"); }
            }
            else Log($"temp folder kept: {_folder}");
            Console.WriteLine("RESULT: PASS");
            return 0;
        }
        Log($"temp folder kept: {_folder}");
        Console.WriteLine($"RESULT: FAIL: {failure}");
        return 1;
    }

    private void DriveInput(System.Drawing.Point centre, double scale)
    {
        var keys = _o.Keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => !k.Equals("none", StringComparison.OrdinalIgnoreCase)).Select(KeyCode).ToArray();

        Log($"move to window centre ({centre.X},{centre.Y})");
        MoveTo(centre.X, centre.Y);
        Thread.Sleep(150);
        {
            var h = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = centre.X, Y = centre.Y }), 2);
            var title = new System.Text.StringBuilder(128); var cls = new System.Text.StringBuilder(128);
            Native.GetWindowText(h, title, 128); Native.GetClassName(h, cls, 128);
            Log($"window under pointer: 0x{h:X} class '{cls}' title '{title}'");
        }
        Log("left button down");
        LeftDown();
        if (!_dragStarted.Wait(1500)) throw new Exception("window never received MouseDown (drag did not start)");
        Thread.Sleep(100);

        Log($"move to wheel point ({_o.WheelX},{_o.WheelY})");
        MoveSteps(_o.WheelX, _o.WheelY);
        Thread.Sleep(150);

        if (_o.Shake) ShakePointer();
        if (keys.Length > 0)
        {
            Log($"tap keys [{_o.Keys}] hold {_o.HoldMs} ms");
            foreach (var k in keys) Key(k, true);
            Thread.Sleep(_o.HoldMs);
            foreach (var k in keys.Reverse()) Key(k, false);
        }

        Log($"settle {_o.SettleMs} ms (jiggling)");
        Jiggle(_o.SettleMs);

        foreach (var spec in _o.Press.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Log($"press {spec}");
            Tap(spec);
            Jiggle(300);
        }
        if (_o.PauseMs > 0) { Log($"pause {_o.PauseMs} ms"); Jiggle(_o.PauseMs); }

        if (_o.Wedge >= 0)
        {
            var geometry = new WheelGeometry(Math.Max(1, _o.Wedges));
            var angle = geometry.CenterAngle(_o.Wedge);
            var radius = 93 * scale;
            var tx = _o.WheelX + (int)Math.Round(radius * Math.Sin(angle));
            var ty = _o.WheelY - (int)Math.Round(radius * Math.Cos(angle));
            Log($"move to wedge {_o.Wedge}/{_o.Wedges} centre ({tx},{ty}) radius {radius:0.#} px");
            MoveSteps(tx, ty);
        }
        else
        {
            Log("releasing at the wheel point (hub)");
        }
        Log("pause 250 ms (jiggling)");
        Jiggle(250);
        Native.GetCursorPos(out var p);
        Log($"left button up at ({_x},{_y}); actual cursor ({p.X},{p.Y})");
        LeftUp();
    }

    private string? WaitForOutcome()
    {
        var expect = _o.Expect;
        Log($"waiting up to {_o.WaitMs} ms for: {expect}");
        var sw = Stopwatch.StartNew();
        if (expect == "none")
        {
            Thread.Sleep(Math.Min(_o.WaitMs, 1500));
            var extra = Directory.EnumerateFileSystemEntries(_folder).Where(p => !SamePath(p, _png)).ToArray();
            if (extra.Length == 0) { Log("folder unchanged"); return null; }
            return "expected nothing but found: " + string.Join(", ", extra.Select(Path.GetFileName));
        }
        while (sw.ElapsedMilliseconds < _o.WaitMs)
        {
            if (expect.StartsWith("file:"))
            {
                var ext = expect["file:".Length..].TrimStart('.');
                if (NewFileWithExtension(ext) is { } f)
                {
                    Log($"found {Path.GetFileName(f)} ({new FileInfo(f).Length} bytes) after {sw.ElapsedMilliseconds} ms");
                    return null;
                }
            }
            else if (expect == "clipboard-text")
            {
                var text = ClipboardText();
                if (text is not null && SamePath(text.Trim('"'), _png) && text.Trim('"').Length > 0)
                {
                    Log($"clipboard text matches: {text}");
                    return null;
                }
            }
            else if (expect == "clipboard-files")
            {
                var files = ClipboardFiles();
                if (files is not null && files.Any(f => SamePath(f, _png)))
                {
                    Log($"clipboard file list contains the PNG ({files.Length} entries)");
                    return null;
                }
            }
            else return $"unknown --expect '{expect}'";
            Thread.Sleep(100);
        }
        var seen = Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName);
        Log($"folder contents: {string.Join(", ", seen)}");
        if (expect == "clipboard-text") Log($"clipboard text: {ClipboardText() ?? "(none)"}");
        return $"timed out after {_o.WaitMs} ms waiting for {expect} (drag effect {_effect?.ToString() ?? "unknown"})";
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Physical pixels everywhere: Per-Monitor-V2.
        Native.SetProcessDpiAwarenessContext(-4);

        if (args.Length > 0 && args[0] == "qa") return Qa.Run(args.Skip(1).ToArray());

        Options options;
        try { options = Options.Parse(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("options: --window-x --window-y --wheel-x --wheel-y --keys shift,ctrl,alt --hold-ms --settle-ms --wedge --wedges --expect file:<ext>|clipboard-text|clipboard-files|none --wait-ms --timeout-ms --keep");
            return 2;
        }

        var harness = new Harness(options);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            harness.Log($"HARD TIMEOUT after {options.TimeoutMs} ms; releasing input and aborting");
            harness.ReleaseAll();
            Console.WriteLine("RESULT: FAIL: hard timeout");
            Environment.Exit(2);
        }, null, options.TimeoutMs, Timeout.Infinite);

        return harness.Run();
    }
}

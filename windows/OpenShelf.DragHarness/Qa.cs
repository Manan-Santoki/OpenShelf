using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenShelf.DragHarness;

/// <summary>
/// General input scripting for manual QA: <c>DragHarness.exe qa "move 900 500; down; moveto 1000 600 20 10; key shift; up; shot out.png 0 0 800 600"</c>
/// (or <c>qa @script.txt</c>). Commands are separated by ';' or newlines. All coordinates are physical pixels.
/// move X Y | moveto X Y [steps] [ms-per-step] | click [X Y] | dclick [X Y] | rclick [X Y] | down [l|r] | up [l|r]
/// key NAME | keydown NAME | keyup NAME | chord ctrl+alt+space | text STRING | wait MS
/// drag X1 Y1 X2 Y2 [steps] [ms] [tapKey@fractionOfPath, e.g. shift@0.5]
/// shot FILE [X Y W H]  (no rect = whole virtual desktop) | windows [procname] | pos | monitors | resources PROCNAME | echo TEXT
/// </summary>
internal static class Qa
{
    private const uint MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10;
    private const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;

    private static readonly HashSet<ushort> KeysDown = [];
    private static bool _left, _right;
    private static int _x, _y;

    private delegate bool EnumProc(nint hwnd, nint lp);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, nint lp);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint h);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint h, int idx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern short VkKeyScan(char c);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonEnum cb, nint d);
    private delegate bool MonEnum(nint mon, nint hdc, ref RECT r, nint d);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint proc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.POINT p);
    [DllImport("user32.dll")] private static extern nint GetDC(nint h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint d, int x, int y, int w, int h, nint s, int sx, int sy, uint rop);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private static readonly Dictionary<string, ushort> Vk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shift"] = 0x10, ["ctrl"] = 0x11, ["alt"] = 0x12, ["win"] = 0x5B, ["enter"] = 0x0D, ["esc"] = 0x1B, ["tab"] = 0x09,
        ["space"] = 0x20, ["back"] = 0x08, ["delete"] = 0x2E, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["home"] = 0x24, ["end"] = 0x23, ["pgup"] = 0x21, ["pgdn"] = 0x22, ["f2"] = 0x71, ["f5"] = 0x74, ["f4"] = 0x73,
    };
    private static readonly HashSet<ushort> Extended = [0x25, 0x26, 0x27, 0x28, 0x2E, 0x24, 0x23, 0x21, 0x22];

    private static ushort Code(string name)
    {
        if (Vk.TryGetValue(name, out var v)) return v;
        if (name.Length == 1) return (ushort)(VkKeyScan(name[0]) & 0xFF);
        throw new ArgumentException("unknown key " + name);
    }

    public static int Run(string[] args)
    {
        var script = string.Join(" ", args);
        if (script.StartsWith('@')) script = File.ReadAllText(script[1..]);
        using var wd = new System.Threading.Timer(_ => { ReleaseAll(); Console.WriteLine("QA HARD TIMEOUT"); Environment.Exit(3); }, null, 120000, Timeout.Infinite);
        try
        {
            foreach (var raw in script.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                Exec(line);
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("QA ERROR: " + ex.Message); return 1; }
        finally { ReleaseAll(); }
    }

    private static void ReleaseAll()
    {
        if (_left) Button(MOUSEEVENTF_LEFTUP);
        if (_right) Button(MOUSEEVENTF_RIGHTUP);
        _left = _right = false;
        foreach (var k in KeysDown.ToArray()) Key(k, false);
    }

    private static void Exec(string line)
    {
        var raw = line.Split(' ', 2);
        var cmd = raw[0].ToLowerInvariant();
        var rest = raw.Length > 1 ? raw[1] : "";
        var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int I(int i, int d = 0) => i < a.Length ? int.Parse(a[i]) : d;
        switch (cmd)
        {
            case "move": MoveTo(I(0), I(1)); break;
            case "moveto": Glide(I(0), I(1), I(2, 15), I(3, 8), null); break;
            case "click": if (a.Length >= 2) { MoveTo(I(0), I(1)); Thread.Sleep(60); } Click(false); break;
            case "dclick": if (a.Length >= 2) { MoveTo(I(0), I(1)); Thread.Sleep(60); } Click(false); Thread.Sleep(80); Click(false); break;
            case "rclick": if (a.Length >= 2) { MoveTo(I(0), I(1)); Thread.Sleep(60); } Click(true); break;
            case "down": if (a.Length > 0 && a[0] == "r") { Button(MOUSEEVENTF_RIGHTDOWN); _right = true; } else { Button(MOUSEEVENTF_LEFTDOWN); _left = true; } break;
            case "up": if (a.Length > 0 && a[0] == "r") { Button(MOUSEEVENTF_RIGHTUP); _right = false; } else { Button(MOUSEEVENTF_LEFTUP); _left = false; } break;
            case "key": { var k = Code(a[0]); Key(k, true); Thread.Sleep(30); Key(k, false); Thread.Sleep(40); break; }
            case "keydown": Key(Code(a[0]), true); break;
            case "keyup": Key(Code(a[0]), false); break;
            case "chord":
            {
                var parts = a[0].Split('+').Select(Code).ToArray();
                foreach (var k in parts) { Key(k, true); Thread.Sleep(20); }
                Thread.Sleep(80);
                foreach (var k in parts.Reverse()) { Key(k, false); Thread.Sleep(20); }
                break;
            }
            case "text":
                foreach (var c in rest)
                {
                    var vs = VkKeyScan(c); var k = (ushort)(vs & 0xFF); var sh = (vs & 0x100) != 0;
                    if (sh) Key(0x10, true);
                    Key(k, true); Key(k, false);
                    if (sh) Key(0x10, false);
                    Thread.Sleep(30);
                }
                break;
            case "wait": Thread.Sleep(I(0, 500)); break;
            case "drag":
            {
                MoveTo(I(0), I(1)); Thread.Sleep(120);
                Button(MOUSEEVENTF_LEFTDOWN); _left = true; Thread.Sleep(120);
                string? tap = a.Length > 6 ? a[6] : null;
                Glide(I(2), I(3), I(4, 30), I(5, 10), tap);
                Thread.Sleep(150);
                Button(MOUSEEVENTF_LEFTUP); _left = false; Thread.Sleep(100);
                break;
            }
            case "shot": Shot(a[0], a.Length >= 5 ? new Rectangle(I(1), I(2), I(3), I(4)) : null); break;
            case "windows": Windows(a.Length > 0 ? a[0] : "OpenShelf"); break;
            case "pos": { GetCursorPos(out var p); Console.WriteLine($"cursor {p.X},{p.Y}"); break; }
            case "monitors": EnumDisplayMonitors(0, 0, (nint m, nint h, ref RECT r, nint d) => { Console.WriteLine($"monitor {r.L},{r.T} {r.R - r.L}x{r.B - r.T}"); return true; }, 0); break;
            case "resources":
            {
                var p = Process.GetProcessesByName(a[0]).FirstOrDefault();
                if (p == null) { Console.WriteLine("no process"); break; }
                p.Refresh();
                Console.WriteLine($"{p.ProcessName} pid={p.Id} handles={p.HandleCount} ws={p.WorkingSet64 / 1048576}MB priv={p.PrivateMemorySize64 / 1048576}MB threads={p.Threads.Count} gdi={GetGuiResources(p.Handle, 0)} user={GetGuiResources(p.Handle, 1)} cpu={p.TotalProcessorTime.TotalSeconds:F2}s");
                break;
            }
            case "guard":
            {
                // guard X Y proc[,proc]: abort the whole script unless the window under (X,Y) belongs to one of the processes.
                var pt = new Native.POINT { X = I(0), Y = I(1) };
                var root = Native.GetAncestor(Native.WindowFromPoint(pt), 2);
                GetWindowThreadProcessId(root, out var gpid);
                string name; try { name = Process.GetProcessById((int)gpid).ProcessName; } catch { name = "?"; }
                if (!a[2].Split(',').Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"GUARD FAILED at {pt.X},{pt.Y}: window belongs to '{name}', wanted {a[2]}");
                if (a.Length > 3)
                {
                    // guard X Y proc title words...: the window's title must also contain the given text (case-insensitive).
                    var tb = new StringBuilder(256); GetWindowText(root, tb, 256);
                    var want = string.Join(" ", a.Skip(3));
                    if (!tb.ToString().Contains(want, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"GUARD FAILED at {pt.X},{pt.Y}: window title '{tb}' lacks '{want}'");
                }
                break;
            }
            case "fg":
            {
                var h = GetForegroundWindow(); GetWindowThreadProcessId(h, out var fpid);
                var t = new StringBuilder(128); GetWindowText(h, t, 128);
                string n; try { n = Process.GetProcessById((int)fpid).ProcessName; } catch { n = "?"; }
                Console.WriteLine($"foreground 0x{h:X} {n} '{t}'");
                break;
            }
            case "raise":
            {
                // raise PROC title words...: brings a top-level window of that process whose title contains the text to the front.
                var want = string.Join(" ", a.Skip(1)); nint found = 0;
                EnumWindows((h, _) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    var tb = new StringBuilder(256); GetWindowText(h, tb, 256);
                    if (!tb.ToString().Contains(want, StringComparison.OrdinalIgnoreCase)) return true;
                    GetWindowThreadProcessId(h, out var rp);
                    string n; try { n = Process.GetProcessById((int)rp).ProcessName; } catch { return true; }
                    if (!n.Equals(a[0], StringComparison.OrdinalIgnoreCase)) return true;
                    found = h; return false;
                }, 0);
                if (found == 0) throw new InvalidOperationException("raise: no window " + rest);
                var fgw = GetForegroundWindow(); var fgt = GetWindowThreadProcessId(fgw, out _); var me = GetCurrentThreadId();
                AttachThreadInput(me, fgt, true);
                ShowWindow(found, 9); BringWindowToTop(found); SetForegroundWindow(found);
                AttachThreadInput(me, fgt, false);
                Thread.Sleep(300);
                break;
            }
            case "echo": Console.WriteLine(rest); break;
            default: throw new ArgumentException("unknown command " + line);
        }
    }

    private static void Glide(int x, int y, int steps, int ms, string? tap)
    {
        GetCursorPos(out var p);
        int sx = p.X, sy = p.Y;
        string? tapKey = null; double at = 2;
        if (tap != null) { var t = tap.Split('@'); tapKey = t[0]; at = double.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture); }
        var tapped = false;
        for (var i = 1; i <= steps; i++)
        {
            double f = (double)i / steps;
            MoveTo((int)Math.Round(sx + (x - sx) * f), (int)Math.Round(sy + (y - sy) * f));
            Thread.Sleep(ms);
            if (!tapped && tapKey != null && f >= at)
            {
                tapped = true;
                var k = Code(tapKey); Key(k, true); Thread.Sleep(40); Key(k, false); Thread.Sleep(60);
            }
        }
    }

    private static void Click(bool right)
    {
        Button(right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN); Thread.Sleep(40);
        Button(right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP); Thread.Sleep(80);
    }

    private static void Send(Native.INPUT i) => Native.SendInput(1, [i], Marshal.SizeOf<Native.INPUT>());

    private static void MoveTo(int x, int y)
    {
        int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
        int vw = Math.Max(2, Native.GetSystemMetrics(78)), vh = Math.Max(2, Native.GetSystemMetrics(79));
        var i = new Native.INPUT { type = Native.INPUT_MOUSE };
        i.u.mi = new Native.MOUSEINPUT
        {
            dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)), dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1)),
            dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
        };
        Send(i); _x = x; _y = y;
    }

    private static void Button(uint flag)
    {
        var i = new Native.INPUT { type = Native.INPUT_MOUSE };
        i.u.mi = new Native.MOUSEINPUT { dwFlags = flag };
        Send(i);
    }

    private static void Key(ushort vk, bool down)
    {
        var i = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        i.u.ki = new Native.KEYBDINPUT
        {
            wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0),
            dwFlags = (down ? 0u : KEYEVENTF_KEYUP) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0u),
        };
        Send(i);
        if (down) KeysDown.Add(vk); else KeysDown.Remove(vk);
    }

    private static void Shot(string file, Rectangle? rect)
    {
        var r = rect ?? new Rectangle(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var dst = g.GetHdc(); var src = GetDC(0);
            BitBlt(dst, 0, 0, r.Width, r.Height, src, r.X, r.Y, 0x00CC0020u | 0x40000000u);
            ReleaseDC(0, src); g.ReleaseHdc(dst);
        }
        bmp.Save(file, ImageFormat.Png);
        Console.WriteLine($"shot {file} {r.X},{r.Y} {r.Width}x{r.Height}");
    }

    private static void Windows(string proc)
    {
        var pids = Process.GetProcessesByName(proc).Select(p => (uint)p.Id).ToHashSet();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
            GetWindowRect(h, out var r);
            var t = new StringBuilder(128); GetWindowText(h, t, 128);
            var c = new StringBuilder(128); GetClassName(h, c, 128);
            var ex = GetWindowLong(h, -20);
            Console.WriteLine($"win 0x{h:X} pid={pid} class={c} title='{t}' rect={r.L},{r.T} {r.R - r.L}x{r.B - r.T} topmost={(ex & 8) != 0} noactivate={(ex & 0x08000000) != 0} layered={(ex & 0x80000) != 0}");
            return true;
        }, 0);
    }
}

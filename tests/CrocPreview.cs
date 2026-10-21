using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Records the real CrocDesktop window on a real desktop while a scripted
// telemetry timeline is written to scene.json, the same file the Python
// bridge writes. Also samples the window process's CPU time per phase.
// Usage: CrocPreview.exe <CrocDesktop.exe> <project root> <runtime dir> <frame dir> [scale] [seconds]
static class CrocPreview {
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    // CAPTUREBLT includes layered (per-pixel alpha) windows in the copy.
    static void CaptureScreen(Bitmap target, Point origin) {
        IntPtr screen = GetDC(IntPtr.Zero);
        try { using (var g = Graphics.FromImage(target)) { IntPtr dc = g.GetHdc(); try { BitBlt(dc, 0, 0, target.Width, target.Height, screen, origin.X, origin.Y, 0x00CC0020 | 0x40000000); } finally { g.ReleaseHdc(dc); } } }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }
    class Phase { public string Name; public double Start, End; }
    static readonly Phase[] Phases = {
        new Phase{Name="rest",Start=0.5,End=3}, new Phase{Name="tools",Start=3.5,End=5},
        new Phase{Name="tier1",Start=5.5,End=8}, new Phase{Name="tier3",Start=8.5,End=11},
        new Phase{Name="tier5",Start=11.5,End=17}, new Phase{Name="rest_after",Start=20,End=24}};

    static string Scene(double t) {
        string stage = t < 3 ? "idle" : t < 5 ? "tools" : t < 17 ? "generating" : t < 19 ? "tools" : "idle";
        double rate = t < 5 ? 0 : t < 8 ? 10 : t < 11 ? 40 : 140;
        double quota = t < 15 ? 54 : 38;
        bool emitting = stage == "generating" && rate > 0;
        int tier = rate <= 0 ? 0 : 1 + (rate >= 15 ? 1 : 0) + (rate >= 30 ? 1 : 0) + (rate >= 60 ? 1 : 0) + (rate >= 100 ? 1 : 0);
        int lanes = new[]{0,1,1,2,2,3}[tier];
        double seconds = rate <= 0 ? 0 : Math.Max(.55, 4.4 / (1 + rate / 40));
        double spacing = rate <= 0 ? 70 : Math.Max(25, 70 - 9 * Math.Log(1 + rate / 10, 2));
        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        var c = CultureInfo.InvariantCulture;
        var b = new StringBuilder("{");
        Action<string, string> raw = (k, v) => b.Append('"').Append(k).Append("\":").Append(v).Append(',');
        raw("working", stage != "idle" ? "true" : "false");
        raw("emitting", emitting ? "true" : "false");
        raw("idle", stage == "idle" ? "true" : "false");
        raw("lanes", lanes.ToString(c));
        raw("speed_tier", tier.ToString(c));
        raw("seconds", seconds.ToString("0.###", c));
        raw("spacing", spacing.ToString("0.###", c));
        raw("rate", emitting || stage == "tools" && rate > 0 ? rate.ToString("0.#", c) : "null");
        raw("model", "\"gpt-5.5-codex\"");
        raw("cache_percent", "82.5");
        raw("run", "\"preview-run\"");
        raw("selected", "\"preview-thread-0001\"");
        raw("following", "\"\\u56fa\\u5b9a\\u5bf9\\u8bdd\"");
        raw("status", "\"preview\"");
        raw("quota_status", "\"recorded\"");
        raw("quota_remaining", quota.ToString(c));
        raw("quota_used", (100 - quota).ToString(c));
        raw("quota_resets_at", (now + 2 * 3600 + 17 * 60).ToString("0", c));
        raw("quota_recorded_at", ((now - 30) * 1000).ToString("0", c));
        raw("updated", now.ToString("0.###", c));
        b.Append("\"threads\":[]}");
        return b.ToString();
    }

    static void WriteScene(string runtime, double t) {
        string target = Path.Combine(runtime, "scene.json"), temp = target + ".tmp";
        for (int attempt = 0; attempt < 5; attempt++) {
            try {
                File.WriteAllText(temp, Scene(t), new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
                return;
            } catch (IOException) { Thread.Sleep(3); } catch (UnauthorizedAccessException) { Thread.Sleep(3); }
        }
    }

    class Wallpaper : Form {
        public Wallpaper() {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds; ShowInTaskbar = false; DoubleBuffered = true;
        }
        protected override void OnPaint(PaintEventArgs e) {
            using (var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(214, 226, 240), Color.FromArgb(236, 228, 246), 35f))
                e.Graphics.FillRectangle(brush, ClientRectangle);
            // A pale editor-like card, so transparency and edges read against content.
            using (var card = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                e.Graphics.FillRectangle(card, ClientSize.Width / 10, ClientSize.Height / 12, ClientSize.Width * 8 / 10, ClientSize.Height / 3);
        }
    }

    [STAThread]
    static int Main(string[] args) {
        string exe = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]), runtime = Path.GetFullPath(args[2]), frames = Path.GetFullPath(args[3]);
        double scale = args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 2;
        double duration = args.Length > 5 ? double.Parse(args[5], CultureInfo.InvariantCulture) : 24;
        Directory.CreateDirectory(runtime); Directory.CreateDirectory(frames);
        var screen = Screen.PrimaryScreen.Bounds;
        int width = (int)(420 * scale), height = (int)(244 * scale);
        int x = screen.Left + Math.Max(0, (screen.Width - width) / 2), y = screen.Top + Math.Max(0, (screen.Height - height) / 2);
        File.WriteAllText(Path.Combine(runtime, "layout.json"), string.Format(CultureInfo.InvariantCulture, "{{\"x\":{0},\"y\":{1},\"scale\":{2}}}", x, y, scale));
        WriteScene(runtime, 0);

        var wallpaper = new Wallpaper();
        var report = new StringBuilder();
        int exitCode = 0;
        var worker = new Thread(() => {
            Process process = null;
            try {
                process = Process.Start(new ProcessStartInfo(exe, "\"" + root + "\" \"" + runtime + "\"") { UseShellExecute = false });
                Thread.Sleep(2500); // Window creation, sprite loading and first frame.
                var region = new Rectangle(x - 16, y - 16, width + 32, height + 32);
                region.Intersect(screen);
                var queue = new BlockingCollection<Tuple<int, Bitmap>>(64);
                var saver = new Thread(() => { foreach (var item in queue.GetConsumingEnumerable()) { item.Item2.Save(Path.Combine(frames, item.Item1.ToString("D4") + ".png"), ImageFormat.Png); item.Item2.Dispose(); } });
                saver.IsBackground = true; saver.Start();
                var cpu = new Dictionary<string, double[]>();
                var clock = Stopwatch.StartNew();
                int frame = 0; const double fps = 25;
                double nextScene = 0;
                while (clock.Elapsed.TotalSeconds < duration) {
                    double t = clock.Elapsed.TotalSeconds;
                    if (process.HasExited) throw new Exception("CrocDesktop exited with " + process.ExitCode);
                    if (t >= nextScene) { WriteScene(runtime, t); nextScene = t + .1; }
                    foreach (var p in Phases) {
                        process.Refresh();
                        if (!cpu.ContainsKey(p.Name) && t >= p.Start) cpu[p.Name] = new[]{ t, process.TotalProcessorTime.TotalSeconds, 0, 0 };
                        else if (cpu.ContainsKey(p.Name) && cpu[p.Name][2] == 0 && t >= p.End) { cpu[p.Name][2] = t; cpu[p.Name][3] = process.TotalProcessorTime.TotalSeconds; }
                    }
                    if (t >= frame / fps) {
                        var shot = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
                        CaptureScreen(shot, region.Location);
                        queue.Add(Tuple.Create(frame++, shot));
                    }
                    Thread.Sleep(2);
                }
                queue.CompleteAdding(); saver.Join();
                process.Refresh();
                report.Append("{\"frames\":").Append(frame).Append(",\"fps\":").Append(fps.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"logical_cpus\":").Append(Environment.ProcessorCount)
                      .Append(",\"peak_working_set_mb\":").Append((process.PeakWorkingSet64 / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture))
                      .Append(",\"cpu_percent_of_one_core\":{");
                bool first = true;
                foreach (var p in Phases) {
                    double[] s; if (!cpu.TryGetValue(p.Name, out s) || s[2] == 0) continue;
                    report.Append(first ? "" : ",").Append('"').Append(p.Name).Append("\":")
                          .Append((100 * (s[3] - s[1]) / (s[2] - s[0])).ToString("0.0", CultureInfo.InvariantCulture));
                    first = false;
                }
                report.Append("}}");
            } catch (Exception error) {
                report.Clear().Append("{\"error\":\"").Append(error.ToString().Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", "").Replace("\n", "\\n")).Append("\"}");
                exitCode = 1;
            } finally {
                if (process != null && !process.HasExited) { process.Kill(); process.WaitForExit(5000); }
                wallpaper.BeginInvoke(new Action(wallpaper.Close));
            }
        });
        worker.IsBackground = true;
        wallpaper.Shown += (s, e) => worker.Start();
        Application.Run(wallpaper);
        File.WriteAllText(Path.Combine(frames, "metrics.json"), report.ToString());
        Console.WriteLine(report.ToString());
        return exitCode;
    }
}

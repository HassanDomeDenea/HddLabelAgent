using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace HddLabelAgent;

internal static class Program
{
    public const string AppName = "HDD Label Agent";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\HddLabelAgent.SingleInstance", out var isFirst);
        var store = SettingsStore.Load();
        if (!isFirst)
        {
            // Already in the tray: starting it again just brings its settings up.
            OpenUrl($"http://127.0.0.1:{store.Current.Port}/");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);

        var ui = new Ui();
        var printing = new PrintService();
        var server = new ApiServer(store, new OriginGate(store, ui), printing);
        string? startError = null;
        try
        {
            server.Start();
            Log.Write($"started on 127.0.0.1:{server.Port}");
        }
        catch (Exception ex)
        {
            Log.Write("could not start the HTTP endpoint: " + ex);
            startError = $"Port {store.Current.Port} is in use by another program, so the web app cannot reach the label printer.\n\n" +
                         $"Change \"port\" in {SettingsStore.FilePath} and start the agent again.";
        }

        using var tray = new TrayContext(store, printing, server, startError);
        Application.Run(tray);
        server.Stop();
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("could not open " + url + ": " + ex.Message);
        }
    }
}

/// <summary>The icon near the clock: the only window the agent has.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string AppName = Program.AppName;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "HddLabelAgent";

    private readonly SettingsStore _store;
    private readonly PrintService _printing;
    private readonly ApiServer _server;
    private readonly NotifyIcon _icon;

    public TrayContext(SettingsStore store, PrintService printing, ApiServer server, string? startError)
    {
        _store = store;
        _printing = printing;
        _server = server;

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        BuildMenu(menu);

        _icon = new NotifyIcon
        {
            Icon = DrawIcon(),
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenSettings();

        if (startError != null)
            MessageBox.Show(startError, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (!IsAutoStart())
            _icon.ShowBalloonTip(4000, AppName, "طابعة الملصقات جاهزة. انقر مرتين على الأيقونة للإعدادات.\nReady. Double-click the icon for settings.", ToolTipIcon.Info);
    }

    private void BuildMenu(ContextMenuStrip menu)
    {
        var settings = _store.Current;
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem($"{AppName} {ApiServer.Version}  ·  127.0.0.1:{_server.Port}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("الإعدادات  ·  Settings…", null, (_, _) => OpenSettings()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("طباعة ملصق تجريبي  ·  Print test label", null, async (_, _) => await TestPrint()));

        var profiles = new ToolStripMenuItem("الورق  ·  Paper profile");
        foreach (var profile in settings.Profiles)
        {
            var id = profile.Id;
            profiles.DropDownItems.Add(new ToolStripMenuItem(profile.Name, null, (_, _) => _store.Update(s => s.ActiveProfileId = id))
            {
                Checked = id == settings.ActiveProfileId,
            });
        }

        menu.Items.Add(profiles);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("التشغيل مع ويندوز  ·  Start with Windows", null, (_, _) => SetAutoStart(!IsAutoStart()))
        {
            Checked = IsAutoStart(),
        });
        menu.Items.Add(new ToolStripMenuItem("مجلد السجل  ·  Open log folder", null, (_, _) => Program.OpenUrl(SettingsStore.Dir)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("خروج  ·  Exit", null, (_, _) => ExitThread()));
    }

    private void OpenSettings() => Program.OpenUrl($"http://127.0.0.1:{_server.Port}/");

    private async Task TestPrint()
    {
        try
        {
            var result = await _printing.PrintAsync(_store.Current.ActiveProfile(), [ApiServer.Sample]);
            _icon.ShowBalloonTip(3000, AppName, "Sent to " + result.Printer, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            if (ex is not LabelException) Log.Write("test print failed: " + ex);
            MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool IsAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string command &&
               command.Contains(Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetAutoStart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, false);
    }

    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var label = new GraphicsPath();
            var r = new Rectangle(1, 4, 29, 23);
            const int c = 8;
            label.AddArc(r.Left, r.Top, c, c, 180, 90);
            label.AddArc(r.Right - c, r.Top, c, c, 270, 90);
            label.AddArc(r.Right - c, r.Bottom - c, c, c, 0, 90);
            label.AddArc(r.Left, r.Bottom - c, c, c, 90, 90);
            label.CloseFigure();
            g.FillPath(Brushes.White, label);
            using var edge = new Pen(Color.FromArgb(60, 60, 60), 1.4f);
            g.DrawPath(edge, label);

            g.SmoothingMode = SmoothingMode.None;
            int[] bars = [6, 1, 8, 2, 11, 1, 13, 3, 17, 1, 19, 2, 22, 1, 24, 2];
            for (var i = 0; i < bars.Length; i += 2) g.FillRectangle(Brushes.Black, bars[i], 9, bars[i + 1], 13);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}

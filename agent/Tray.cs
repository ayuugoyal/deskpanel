using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace DeskPanel.Agent;

/// <summary>Tray icon: shows how many phones are connected, the start-with-Windows switch and Exit.</summary>
sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem status;
    readonly System.Windows.Forms.Timer timer;
    readonly Action onExit;

    public TrayContext(Hub hub, Action onExit)
    {
        this.onExit = onExit;

        status = new ToolStripMenuItem("No phone connected") { Enabled = false };
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = AutoStart.IsEnabled, CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => AutoStart.Set(autostart.Checked);
        var openLog = new ToolStripMenuItem("Open log", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); } catch { }
        });
        var forget = new ToolStripMenuItem("Forget paired phones", null, (_, _) =>
        {
            if (MessageBox.Show("Forget all paired phones? They will have to be approved again.", "DeskPanel",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Pairing.ForgetAll();
                hub.DisconnectAll();
            }
        });
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("DeskPanel") { Enabled = false },
            status,
            new ToolStripSeparator(),
            autostart,
            forget,
            openLog,
            exit,
        });

        icon = new NotifyIcon { Icon = MakeIcon(), Text = "DeskPanel", ContextMenuStrip = menu, Visible = true };

        timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            int n = hub.ClientCount;
            var text = n switch { 0 => "No phone connected", 1 => "1 phone connected", _ => $"{n} phones connected" };
            status.Text = text;
            icon.Text = "DeskPanel – " + text;
        };
        timer.Start();
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        icon.Visible = false;
        icon.Dispose();
        onExit();
        base.ExitThreadCore();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(79, 140, 255));
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(1, 4, r, r, 180, 90);
            path.AddArc(31 - r, 4, r, r, 270, 90);
            path.AddArc(31 - r, 28 - r, r, r, 0, 90);
            path.AddArc(1, 28 - r, r, r, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            g.FillRectangle(Brushes.White, 7, 17, 4, 6);
            g.FillRectangle(Brushes.White, 12, 11, 4, 12);
            g.FillRectangle(Brushes.White, 17, 14, 4, 9);
            g.FillRectangle(Brushes.White, 22, 9, 4, 14);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}

static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "DeskPanel";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(Name, false);
        }
        catch (Exception e) { Log.Error("autostart", e); }
    }
}

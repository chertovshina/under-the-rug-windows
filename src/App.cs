using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace RugWindows
{
    public sealed class RugApp : Application
    {
        public Preferences Preferences { get; private set; }
        public DesktopService Desktop { get; private set; }
        public WallpaperService Wallpaper { get; private set; }
        public StartupService AutoStart { get; private set; }
        public RugWindow Rug { get; private set; }
        public string ResourceDirectory { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources"); } }
        public string TexturePath { get { return Preferences.Texture; } }
        public IEnumerable<string> RugPaths
        {
            get
            {
                var builtins = Directory.GetFiles(Path.Combine(ResourceDirectory, "rugs"), "*.jpg").OrderBy(x => x, StringComparer.Ordinal).ToList();
                string dir = Path.Combine(RugWindows.Preferences.DataDirectory, "rugs");
                if (Directory.Exists(dir)) builtins.AddRange(Directory.GetFiles(dir).Where(x => new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(x).ToLowerInvariant())).OrderBy(x => x));
                return builtins;
            }
        }
        SettingsWindow settings;
        Forms.NotifyIcon tray;
        DispatcherTimer scan;
        HwndSource messageHost;
        bool settingsRequested;
        bool exiting;
        static uint showMessage;
        static Mutex instance;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint processId);

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--diagnose") return Diagnose(args.Length > 1 ? args[1] : "diagnostics.txt");
                if (args.Length > 1 && args[0] == "--render-demo") return RenderDemo(args[1]);
                string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RugWindows.exe");
                if (args.Length == 1 && args[0] == "--enable-startup") { new StartupService(executable).SetEnabled(true); return 0; }
                if (args.Length == 1 && args[0] == "--disable-startup") { new StartupService(executable).SetEnabled(false); return 0; }
                bool smoke = args.Length > 1 && args[0] == "--smoke-test";
                bool startupLaunch = args.Any(a => String.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));
                showMessage = RegisterWindowMessage("UnderTheRugWindows.ShowSettings.1");
                bool created; instance = new Mutex(true, "Local\\UnderTheRugWindows.1", out created);
                if (!created)
                {
                    if (startupLaunch) { instance.Dispose(); return 0; }
                    foreach (var process in Process.GetProcessesByName("RugWindows"))
                    {
                        using (process) try { if (process.Id != Process.GetCurrentProcess().Id && string.Equals(process.MainModule.FileName, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase)) AllowSetForegroundWindow((uint)process.Id); } catch { }
                    }
                    PostMessage(new IntPtr(0xffff), showMessage, IntPtr.Zero, IntPtr.Zero); instance.Dispose(); return 0;
                }
                var app = new RugApp();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { Log.Write(e.Exception.ToString()); e.Handled = true; MessageBox.Show(e.Exception.Message, "Ковер", MessageBoxButton.OK, MessageBoxImage.Warning); };
                app.Startup += delegate
                {
                    try { app.Start(smoke ? args[1] : null, startupLaunch); }
                    catch (Exception e) { Log.Write(e.ToString()); MessageBox.Show(e.Message, "Ковер: не удалось запустить", MessageBoxButton.OK, MessageBoxImage.Error); app.Shutdown(1); }
                };
                app.Exit += delegate { app.Cleanup(); };
                int exitCode = app.Run(); instance.ReleaseMutex(); instance.Dispose(); return exitCode;
            }
            catch (Exception e) { Log.Write(e.ToString()); MessageBox.Show(e.Message, "Ковер: не удалось запустить", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        }
        void Start(string smokeReport, bool startupLaunch)
        {
            Preferences = RugWindows.Preferences.Load(); Desktop = new DesktopService(); Wallpaper = new WallpaperService(Preferences);
            AutoStart = new StartupService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RugWindows.exe"));
            if (!startupLaunch && AutoStart.Enabled)
            {
                try { if (!String.Equals(AutoStart.RegisteredCommand, StartupService.BuildCommand(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RugWindows.exe")), StringComparison.OrdinalIgnoreCase)) AutoStart.SetEnabled(true); }
                catch (Exception e) { Log.Write("Update startup path: " + e.Message); }
            }
            settingsRequested = !startupLaunch;
            // Run entries can execute before Explorer finishes creating the desktop.
            // A hidden top-level message window also lets a later manual launch open settings.
            var messages = new HwndSourceParameters("UnderTheRugWindows.MessageHost")
            {
                Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080
            };
            messageHost = new HwndSource(messages); messageHost.AddHook(SettingsHook);
            try { Wallpaper.RepairCurrentFloor(); }
            catch (Exception e) { Log.Write("Wallpaper migration: " + e.Message); }
            if (string.IsNullOrEmpty(Preferences.Texture) || !File.Exists(Preferences.Texture)) Preferences.Texture = RugPaths.First();
            try { RugScene.LoadImage(Preferences.Texture, 1600); }
            catch (Exception e) { Log.Write("Texture fallback: " + e.Message); Preferences.Texture = RugPaths.First(); }
            tray = new Forms.NotifyIcon { Text = "Ковер — настройки", Visible = true };
            try
            {
                using (var image = new System.Drawing.Bitmap(Path.Combine(ResourceDirectory, "icon_1024.png")))
                using (var small = new System.Drawing.Bitmap(image, 32, 32))
                {
                    IntPtr handle = small.GetHicon(); using (var icon = System.Drawing.Icon.FromHandle(handle)) tray.Icon = (System.Drawing.Icon)icon.Clone(); DestroyIcon(handle);
                }
            }
            catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) ShowSettings(); };
            tray.ContextMenuStrip = Menu();
            scan = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; scan.Tick += delegate { Rescan(); }; scan.Start(); Rescan();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
            Log.Write("Started" + (startupLaunch ? " from Windows startup" : "") + ". Renderer tier " + (RenderCapability.Tier >> 16) + "; waiting for desktop=" + (Rug == null));
            if (smokeReport != null)
            {
                bool initiallyHidden = settings == null || !settings.IsVisible;
                bool quietDuplicateKeptHidden = false;
                var reopen = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                reopen.Tick += delegate
                {
                    reopen.Stop(); if (settings != null) settings.Hide();
                    using (var second = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RugWindows.exe"), startupLaunch ? "--startup" : "") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })) { }
                }; reopen.Start();
                if (startupLaunch)
                {
                    var manual = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    manual.Tick += delegate
                    {
                        manual.Stop(); quietDuplicateKeptHidden = settings == null || !settings.IsVisible;
                        using (var second = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RugWindows.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })) { }
                    }; manual.Start();
                }
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
                timer.Tick += delegate
                {
                    timer.Stop();
                    try
                    {
                        bool finite = Rug.Scene.Cloth.Positions.All(p => !double.IsNaN(p.X) && !double.IsInfinity(p.X) && !double.IsNaN(p.Y) && !double.IsNaN(p.Z));
                        bool attached = DesktopWindow.IsAttached(Rug.Handle), visible = IsWindowVisible(Rug.Handle);
                        string folder = Path.GetDirectoryName(Path.GetFullPath(smokeReport)); Directory.CreateDirectory(folder);
                        SaveVisual(settings, Path.Combine(folder, "settings-preview.png"));
                        SaveVisual(Rug.Scene, Path.Combine(folder, "desktop-rug-preview.png"));
                        File.WriteAllText(smokeReport, "Attached=" + attached + "\nVisible=" + visible + "\nFinite=" + finite + "\nStartupLaunch=" + startupLaunch + "\nSettingsInitiallyHidden=" + initiallyHidden + "\nStartupDuplicateKeptHidden=" + quietDuplicateKeptHidden + "\nSettingsReopenedBySecondLaunch=" + settings.IsVisible + "\nIcons=" + Desktop.Scan().Count + "\nDpi=" + Rug.Dpi + "\nSleeping=" + Rug.Sleeping + "\nMaxSpeed=" + Rug.Scene.Cloth.MaxSpeed + "\nMaxFrameMotion=" + Rug.Scene.Cloth.MaxFrameMotion + "\nAverageFrameMotion=" + Rug.Scene.Cloth.AverageFrameMotion + "\nVertices=" + Rug.Scene.Cloth.Count + "\n", Encoding.UTF8);
                    }
                    catch (Exception e) { File.WriteAllText(smokeReport, "FAIL " + e); }
                    Quit();
                }; timer.Start();
            }
        }
        void DisplaysChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (exiting) return;
                if (Rug == null || !IsWindow(Rug.Handle)) { Rescan(); return; }
                try
                {
                    // System display changes preserve the saved pose and never open an error dialog during logon.
                    var oldRug = Rug; Rug = null; oldRug.Close(); Rescan();
                }
                catch (Exception error) { Log.Write("Display refresh: " + error.Message); }
            }));
        }
        void CreateRug(Forms.Screen screen)
        {
            var candidate = new RugWindow(this, screen);
            try
            {
                candidate.Show();
                if (!DesktopWindow.IsAttached(candidate.Handle)) throw new InvalidOperationException("Не удалось разместить ковер на рабочем столе Windows.");
                Rug = candidate;
            }
            catch { try { candidate.Close(); } catch { } throw; }
        }
        public void ChangeMonitor(Forms.Screen screen)
        {
            if (Rug != null) Rug.Close(); Preferences.Monitor = screen.DeviceName; Preferences.CenterX = .5; Preferences.CenterY = .5; Preferences.Angle = 0;
            Preferences.Save(); CreateRug(screen); Rescan();
        }
        void Rescan()
        {
            if (exiting) return;
            try
            {
                if (Desktop.DesktopHost == IntPtr.Zero) return;
                if (Rug == null || !IsWindow(Rug.Handle)) CreateRug(Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == Preferences.Monitor) ?? Forms.Screen.PrimaryScreen);
                else if (!DesktopWindow.IsAttached(Rug.Handle)) DesktopWindow.Attach(Rug.Handle, Rug.Screen.Bounds);
                Rug.SetIcons(Desktop.Scan()); if (settings != null) settings.RefreshStatus();
                if (settingsRequested) { settingsRequested = false; ShowSettings(); }
            }
            catch (Exception e) { Log.Write("Desktop refresh: " + e.Message); }
        }
        public void ShowSettings()
        {
            if (Rug == null || !IsWindow(Rug.Handle)) { settingsRequested = true; return; }
            if (settings == null)
            {
                settings = new SettingsWindow(this); MainWindow = settings;
            }
            settings.Show(); if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal; settings.Activate(); settings.RefreshStatus();
        }
        IntPtr SettingsHook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        { if ((uint)msg == showMessage) { ShowSettings(); handled = true; } return IntPtr.Zero; }
        Forms.ContextMenuStrip Menu()
        {
            var menu = new Forms.ContextMenuStrip();
            Add(menu, "Настройки…", ShowSettings); menu.Items.Add(new Forms.ToolStripSeparator());
            Add(menu, "Замести значки под ковер", Sweep); Add(menu, "Разложить значки обратно", Restore);
            Add(menu, "Накрыть значки ковром", Cover); menu.Items.Add(new Forms.ToolStripSeparator());
            Add(menu, "Уронить заново", delegate { Rug.Reset(true, 110); }); Add(menu, "Положить в центр", delegate { Rug.CenterRug(); });
            Add(menu, "Своя картинка…", ChooseImage); menu.Items.Add(new Forms.ToolStripSeparator()); Add(menu, "Выйти", Quit);
            menu.Opening += delegate
            {
                bool ready = Rug != null && IsWindow(Rug.Handle);
                foreach (int index in new[] { 2, 4, 6, 7, 8 }) menu.Items[index].Enabled = ready;
                menu.Items[3].Enabled = ready && Desktop.HasSavedLayout;
            }; return menu;
        }
        void Add(Forms.ContextMenuStrip menu, string text, Action action) { menu.Items.Add(text, null, delegate { TryAction(action); }); }
        public void ShowQuickMenu() { var menu = Menu(); menu.Closed += delegate { Dispatcher.BeginInvoke(new Action(menu.Dispose)); }; menu.Show(Forms.Cursor.Position); }
        public void TryAction(Action action) { try { action(); } catch (Exception e) { Log.Write(e.ToString()); MessageBox.Show(settings, e.Message, "Ковер", MessageBoxButton.OK, MessageBoxImage.Information); } }
        public void Sweep()
        {
            var center = Rug.Scene.Cloth.Center;
            Desktop.Sweep(new System.Drawing.Point(Rug.Screen.Bounds.X + (int)(center.X * Rug.Dpi), Rug.Screen.Bounds.Y + (int)(center.Y * Rug.Dpi)));
            Rug.SetIcons(Desktop.Scan()); Rug.Reset(true, 130); if (settings != null) settings.RefreshStatus();
        }
        public void Restore() { Desktop.Restore(); Rescan(); }
        public void Cover() { Rug.CoverIcons(Desktop.Scan()); }
        public void SelectTexture(string path)
        {
            string previous = Preferences.Texture; Preferences.Texture = path;
            try { Rug.Reset(true, 95); Preferences.Save(); } catch { Preferences.Texture = previous; throw; }
        }
        public void ChooseImage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Изображение ковра с видом сверху", Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp", CheckFileExists = true };
            if (dialog.ShowDialog(settings) != true) return;
            RugScene.LoadImage(dialog.FileName, 1600);
            var dir = Path.Combine(RugWindows.Preferences.DataDirectory, "rugs"); Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(dialog.FileName), ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            string target = Path.Combine(dir, name + ext); int suffix = 2;
            while (File.Exists(target)) target = Path.Combine(dir, name + " (" + suffix++ + ")" + ext);
            File.Copy(dialog.FileName, target); SelectTexture(target); if (settings != null) settings.RefreshRugs();
        }
        public static string TextureName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            switch (name) { case "1 persian": return "Персидский"; case "2 neuro": return "Нейроковер"; case "3 soviet": return "Советский"; case "4 pixel": return "Пиксельный"; default: return name; }
        }
        public void Quit()
        {
            if (exiting) return; exiting = true; if (scan != null) scan.Stop();
            try { if (Rug != null) Rug.Close(); } catch (Exception e) { Log.Write("Close rug: " + e.Message); }
            try { if (settings != null) settings.CloseForExit(); }
            finally { Shutdown(); }
        }
        void Cleanup()
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
            if (scan != null) scan.Stop(); if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (Desktop != null) Desktop.Dispose();
            if (messageHost != null) messageHost.Dispose();
        }
        static int Diagnose(string file)
        {
            using (var desktop = new DesktopService())
            {
                var icons = desktop.Scan();
                File.WriteAllText(file, "OS=" + Environment.OSVersion + "\n64bit=" + Environment.Is64BitProcess + "\nDesktopHost=" + desktop.DesktopHost + "\nIcons=" + icons.Count + "\nAutoArrange=" + desktop.AutoArrange + "\nAlignToGrid=" + desktop.AlignToGrid + "\nSavedLayout=" + desktop.HasSavedLayout + "\nStatus=" + desktop.Status + "\n", Encoding.UTF8);
                return desktop.DesktopHost == IntPtr.Zero ? 1 : 0;
            }
        }
        static int RenderDemo(string file)
        {
            var app = new Application(); var scene = new RugScene(1100, 760);
            scene.SetRug(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rugs", "1 persian.jpg"), .62, new Point(550, 380), -.08, 0);
            for (int i = 0; i < 45; i++) scene.Cloth.Step(1.0 / 30);
            Point corner = scene.Cloth.Project(scene.Cloth.Positions[0]); scene.Cloth.BeginGrab(0, corner, 155);
            for (int i = 0; i < 35; i++) { scene.Cloth.MoveGrab(new Point(corner.X + i * 4, corner.Y + i * 3)); scene.Cloth.Step(1.0 / 30); }
            scene.UpdateMesh(); scene.Measure(new Size(1100, 760)); scene.Arrange(new Rect(0, 0, 1100, 760)); scene.UpdateLayout();
            SaveVisual(scene, file); app.Shutdown(); return 0;
        }
        static void SaveVisual(FrameworkElement visual, string file)
        {
            visual.UpdateLayout(); int w = Math.Max(1, (int)visual.ActualWidth), h = Math.Max(1, (int)visual.ActualHeight);
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }
    }
}

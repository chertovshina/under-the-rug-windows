using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace RugWindows
{
    public sealed class SettingsWindow : Window
    {
        readonly RugApp app;
        readonly TextBlock status;
        readonly Button restoreIcons, restoreWallpaper;
        readonly WrapPanel rugs;
        readonly CheckBox autoStart;
        bool closing;
        public SettingsWindow(RugApp application)
        {
            app = application; Title = "Ковер для Windows"; Width = 735; Height = 850; MinWidth = 610; MinHeight = 590;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = SystemColors.WindowBrush; Foreground = SystemColors.WindowTextBrush;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 22) }; scroll.Content = panel; Content = scroll;
            panel.Children.Add(new TextBlock { Text = "Под ковром", FontSize = 30, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "Чуть меньше порядка. Чуть больше уюта.", FontSize = 14, Margin = new Thickness(0, 5, 0, 12), Foreground = SystemColors.GrayTextBrush });
            panel.Children.Add(new TextBlock { Text = "Потяни ковер за край, чтобы заглянуть под него. Колесо мыши поворачивает ковер, правый клик открывает меню. Закрой настройки — ковер останется на рабочем столе. Открыть их снова можно из значка в трее.", TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
            Heading(panel, "Запуск");
            autoStart = new CheckBox { Content = "Запускать вместе с Windows", IsChecked = app.AutoStart.Enabled, Margin = new Thickness(0, 0, 0, 7) };
            autoStart.Click += delegate { app.TryAction(delegate { app.AutoStart.SetEnabled(autoStart.IsChecked == true); }); autoStart.IsChecked = app.AutoStart.Enabled; };
            panel.Children.Add(autoStart);
            panel.Children.Add(new TextBlock { Text = "После входа в Windows ковер появится с сохраненными настройками. Окно настроек открываться не будет. Папку программы оставь на месте.", Foreground = SystemColors.GrayTextBrush, TextWrapping = TextWrapping.Wrap, LineHeight = 18 });
            Heading(panel, "Выбери ковер");
            rugs = new WrapPanel(); panel.Children.Add(rugs); RefreshRugs();
            panel.Children.Add(Button("＋  Своя картинка…", app.ChooseImage));
            Heading(panel, "Размер ковра");
            var sizeRow = new DockPanel();
            var sizeLabel = new TextBlock { Text = Math.Round(app.Preferences.Scale * 100) + "%", Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(sizeLabel, Dock.Right); sizeRow.Children.Add(sizeLabel);
            var size = new Slider { Minimum = .25, Maximum = .75, Value = app.Preferences.Scale, TickFrequency = .01, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            debounce.Tick += delegate { debounce.Stop(); app.TryAction(delegate { app.Preferences.Scale = size.Value; app.Rug.Reset(true, 50); app.Preferences.Save(); }); };
            size.ValueChanged += delegate { sizeLabel.Text = Math.Round(size.Value * 100) + "%"; debounce.Stop(); debounce.Start(); };
            Closing += delegate { debounce.Stop(); };
            sizeRow.Children.Add(size); panel.Children.Add(sizeRow);
            Heading(panel, "Пол на рабочем столе");
            panel.Children.Add(new TextBlock { Text = "Выбор пола заменит обои Windows. Прежние обои можно вернуть кнопкой ниже.", Foreground = SystemColors.GrayTextBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) });
            var floors = new WrapPanel(); panel.Children.Add(floors);
            string[] names = { "Елочка", "Мозаика", "Дубовые доски" };
            var files = Directory.GetFiles(Path.Combine(app.ResourceDirectory, "floors"), "*.jpg"); Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i]; floors.Children.Add(Tile(file, names[Math.Min(i, names.Length - 1)], false, delegate { app.Wallpaper.Apply(file); RefreshStatus(); }));
            }
            restoreWallpaper = Button("Вернуть прежние обои", delegate { app.Wallpaper.Restore(); RefreshStatus(); }); panel.Children.Add(restoreWallpaper);
            Heading(panel, "Значки рабочего стола");
            var icons = new WrapPanel();
            icons.Children.Add(Button("Замести под ковер", app.Sweep));
            restoreIcons = Button("Разложить обратно", app.Restore); icons.Children.Add(restoreIcons);
            icons.Children.Add(Button("Накрыть ковром", app.Cover)); panel.Children.Add(icons);
            status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(0, 5, 0, 0), LineHeight = 19 }; panel.Children.Add(status);
            Heading(panel, "Положение");
            var position = new WrapPanel(); position.Children.Add(Button("Уронить заново", delegate { app.Rug.Reset(true, 110); })); position.Children.Add(Button("Положить в центр", delegate { app.Rug.CenterRug(); })); panel.Children.Add(position);
            if (Forms.Screen.AllScreens.Length > 1)
            {
                var monitor = new ComboBox { Margin = new Thickness(0, 9, 0, 0), MaxWidth = 450, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 300 };
                foreach (var screen in Forms.Screen.AllScreens) monitor.Items.Add(new ComboBoxItem { Content = screen.DeviceName + (screen.Primary ? " — основной экран" : "") + " (" + screen.Bounds.Width + " × " + screen.Bounds.Height + ")", Tag = screen });
                foreach (ComboBoxItem item in monitor.Items) if (((Forms.Screen)item.Tag).DeviceName == app.Rug.Screen.DeviceName) monitor.SelectedItem = item;
                monitor.SelectionChanged += delegate { var item = monitor.SelectedItem as ComboBoxItem; if (item != null) app.TryAction(delegate { app.ChangeMonitor((Forms.Screen)item.Tag); }); };
                panel.Children.Add(monitor);
            }
            var footer = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
            var quit = Button("Выйти из Ковра", app.Quit); DockPanel.SetDock(quit, Dock.Right); footer.Children.Add(quit);
            footer.Children.Add(new TextBlock { Text = "WINDOWS  ·  1.1.0\nПо мотивам Rug / siliconbag · MIT", FontSize = 11, Foreground = SystemColors.GrayTextBrush, VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(footer);
            Activated += delegate { RefreshStatus(); };
            Closing += delegate(object sender, CancelEventArgs e) { if (!closing) { e.Cancel = true; Hide(); } };
            RefreshStatus();
        }
        static void Heading(Panel parent, string text) { parent.Children.Add(new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 9) }); }
        Button Button(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 5), HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += delegate { app.TryAction(action); }; return button;
        }
        Button Tile(string file, string label, bool selected, Action action)
        {
            var column = new StackPanel();
            column.Children.Add(new Image { Source = RugScene.LoadImage(file, 260), Width = 125, Height = 72, Stretch = Stretch.UniformToFill, ClipToBounds = true });
            column.Children.Add(new TextBlock { Text = label, Margin = new Thickness(2, 6, 2, 0), TextAlignment = TextAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 125 });
            var button = new Button { Content = column, Padding = new Thickness(4), Margin = new Thickness(0, 0, 8, 7), ToolTip = label, BorderThickness = new Thickness(selected ? 2 : 1) };
            if (selected) button.BorderBrush = SystemColors.HighlightBrush;
            button.Click += delegate { app.TryAction(action); }; return button;
        }
        public void RefreshRugs()
        {
            rugs.Children.Clear();
            foreach (string path in app.RugPaths)
            {
                string file = path;
                try { rugs.Children.Add(Tile(file, RugApp.TextureName(file), string.Equals(file, app.TexturePath, StringComparison.OrdinalIgnoreCase), delegate { app.SelectTexture(file); RefreshRugs(); })); }
                catch (Exception e) { Log.Write("Skipping unreadable rug thumbnail: " + e.Message); }
            }
        }
        public void RefreshStatus()
        {
            if (status == null) return;
            autoStart.IsChecked = app.AutoStart.Enabled;
            restoreIcons.IsEnabled = app.Desktop.HasSavedLayout; restoreWallpaper.IsEnabled = app.Preferences.WallpaperSaved;
            status.Text = app.Desktop.Status + (app.Desktop.AutoArrange || app.Desktop.AlignToGrid ? "\nЧтобы собрать и вернуть значки, отключи в меню рабочего стола «Вид → Упорядочить значки автоматически» и «Вид → Выровнять значки по сетке»." : "\nСохраняются только координаты значков. Файлы остаются на своих местах.");
        }
        public void CloseForExit() { closing = true; Close(); }
    }
}

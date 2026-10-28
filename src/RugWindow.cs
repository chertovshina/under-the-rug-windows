using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace RugWindows
{
    public sealed class RugScene : Grid
    {
        public Cloth Cloth { get; private set; }
        readonly Viewport3D viewport = new Viewport3D();
        readonly GeometryModel3D rugModel = new GeometryModel3D();
        readonly System.Windows.Shapes.Path shadow = new System.Windows.Shapes.Path();
        MeshGeometry3D mesh;
        readonly double sceneWidth, sceneHeight;
        readonly List<Bump> bumps = new List<Bump>();
        const double FieldCell = 8;
        readonly int fieldColumns, fieldRows;
        readonly double[] field;
        public RugScene(double width, double height)
        {
            sceneWidth = width; sceneHeight = height; Width = width; Height = height;
            fieldColumns = (int)Math.Ceiling(width / FieldCell) + 1; fieldRows = (int)Math.Ceiling(height / FieldCell) + 1;
            field = new double[fieldColumns * fieldRows];
            Background = Brushes.Transparent; ClipToBounds = true;
            shadow.Fill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)); shadow.Effect = new BlurEffect { Radius = 11 }; shadow.IsHitTestVisible = false;
            Children.Add(shadow); Children.Add(viewport);
            viewport.IsHitTestVisible = false;
            double distance = Math.Max(width, height) * 1.15;
            viewport.Camera = new PerspectiveCamera(new Point3D(width / 2, -height / 2, distance), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 2 * Math.Atan(width / (2 * distance)) * 180 / Math.PI) { NearPlaneDistance = 1, FarPlaneDistance = distance * 3 };
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(165, 165, 165)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(115, 110, 100), new Vector3D(.35, -.5, -1)));
            lights.Freeze(); viewport.Children.Add(new ModelVisual3D { Content = lights });
            viewport.Children.Add(new ModelVisual3D { Content = rugModel });
        }
        public void SetRug(string path, double scale, Point center, double angle, double drop)
        {
            var image = LoadImage(path, 1600);
            double aspect = Math.Max(.25, Math.Min(3, (double)image.PixelHeight / image.PixelWidth));
            double width = Math.Min(sceneWidth * scale, sceneHeight * .90 / aspect);
            Cloth = new Cloth(width, width * aspect, 32, sceneWidth, sceneHeight);
            Cloth.Ground = Ground;
            Cloth.Layout(center, angle, drop);
            mesh = new MeshGeometry3D();
            var indices = new Int32Collection(Cloth.Indices); indices.Freeze();
            var uvs = new PointCollection(Cloth.UVs); uvs.Freeze();
            mesh.TriangleIndices = indices; mesh.TextureCoordinates = uvs;
            var brush = new ImageBrush(image) { ViewportUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill }; brush.Freeze();
            var front = new DiffuseMaterial(brush); front.Freeze();
            var backBrush = new ImageBrush(image) { Opacity = .58 }; backBrush.Freeze();
            var back = new MaterialGroup(); back.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(109, 91, 72)))); back.Children.Add(new DiffuseMaterial(backBrush)); back.Freeze();
            rugModel.Material = front; rugModel.BackMaterial = back; rugModel.Geometry = mesh;
            UpdateMesh();
        }
        public static BitmapImage LoadImage(string path, int pixels)
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(System.IO.Path.GetFullPath(path)); image.DecodePixelWidth = pixels;
            image.EndInit(); image.Freeze(); return image;
        }
        sealed class Bump { public double X, Y, Radius; }
        public bool SetIcons(List<DesktopIcon> icons, System.Drawing.Rectangle bounds, double dpi)
        {
            var next = new List<Bump>();
            foreach (var icon in icons)
            {
                double x = (icon.X - bounds.Left + icon.Width / 2.0) / dpi, y = (icon.Y - bounds.Top + Math.Min(icon.Width,icon.Height) / 2.0) / dpi;
                if (x >= -50 && x < sceneWidth + 50 && y >= -50 && y < sceneHeight + 50) next.Add(new Bump { X = x, Y = y, Radius = Math.Max(20, icon.Width * .43 / dpi) });
            }
            bool changed = next.Count != bumps.Count;
            if (!changed) for (int i = 0; i < next.Count; i++) if (Math.Abs(next[i].X - bumps[i].X) > .5 || Math.Abs(next[i].Y - bumps[i].Y) > .5 || Math.Abs(next[i].Radius - bumps[i].Radius) > .5) { changed = true; break; }
            if (changed) { bumps.Clear(); bumps.AddRange(next); RebuildField(); }
            return changed;
        }
        void RebuildField()
        {
            Array.Clear(field, 0, field.Length);
            foreach (var b in bumps)
            {
                int left = Math.Max(0, (int)Math.Floor((b.X - b.Radius * 2.3) / FieldCell));
                int right = Math.Min(fieldColumns - 1, (int)Math.Ceiling((b.X + b.Radius * 2.3) / FieldCell));
                int top = Math.Max(0, (int)Math.Floor((b.Y - b.Radius * 2.3) / FieldCell));
                int bottom = Math.Min(fieldRows - 1, (int)Math.Ceiling((b.Y + b.Radius * 2.3) / FieldCell));
                for (int j = top; j <= bottom; j++) for (int i = left; i <= right; i++)
                {
                    double dx = (i * FieldCell - b.X) / b.Radius, dy = (j * FieldCell - b.Y) / b.Radius;
                    double z = 23 * Math.Exp(-(dx * dx + dy * dy) * 1.25);
                    int index = j * fieldColumns + i; field[index] = Math.Max(field[index], z);
                }
            }
        }
        double Ground(double x, double y)
        {
            if (x < 0 || y < 0 || x >= sceneWidth || y >= sceneHeight) return 1;
            double gx = x / FieldCell, gy = y / FieldCell; int i = (int)gx, j = (int)gy;
            double u = gx - i, v = gy - j; int k = j * fieldColumns + i;
            return 1 + (field[k] * (1 - u) + field[k + 1] * u) * (1 - v) + (field[k + fieldColumns] * (1 - u) + field[k + fieldColumns + 1] * u) * v;
        }
        public void UpdateMesh()
        {
            var points = new Point3DCollection(Cloth.Count);
            foreach (var p in Cloth.Positions) points.Add(new Point3D(p.X, -p.Y, p.Z));
            mesh.Positions = points;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                int nx = Cloth.Columns, ny = Cloth.Rows;
                var outline = new List<Point>();
                for (int x = 0; x < nx; x++) outline.Add(ShadowPoint(x));
                for (int y = 1; y < ny; y++) outline.Add(ShadowPoint(y * nx + nx - 1));
                for (int x = nx - 2; x >= 0; x--) outline.Add(ShadowPoint((ny - 1) * nx + x));
                for (int y = ny - 2; y > 0; y--) outline.Add(ShadowPoint(y * nx));
                ctx.BeginFigure(outline[0], true, true); ctx.PolyLineTo(outline.Skip(1).ToList(), true, false);
            }
            geometry.Freeze(); shadow.Data = geometry;
        }
        Point ShadowPoint(int id) { var p = Cloth.Positions[id]; return new Point(p.X + p.Z * .32 + 3, p.Y + p.Z * .40 + 5); }
    }

    public sealed class RugWindow : Window
    {
        readonly RugApp owner;
        public RugScene Scene { get; private set; }
        public Forms.Screen Screen { get; private set; }
        public IntPtr Handle { get; private set; }
        public double Dpi { get; private set; }
        readonly DispatcherTimer animation;
        readonly DispatcherTimer hover;
        int calm;
        bool dragging, wasTransparent;
        public bool Sleeping { get { return !animation.IsEnabled; } }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
        public RugWindow(RugApp app, Forms.Screen screen)
        {
            owner = app; Screen = screen;
            Title = "Ковер — рабочий стол"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
            Left = screen.Bounds.Left; Top = screen.Bounds.Top; Width = screen.Bounds.Width; Height = screen.Bounds.Height;
            animation = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
            animation.Tick += Animate;
            hover = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
            hover.Tick += PollHover;
            SourceInitialized += Initialize;
            PreviewMouseLeftButtonDown += Down; PreviewMouseLeftButtonUp += Up; PreviewMouseMove += Move;
            PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) { if (Scene.Cloth.Pick(e.GetPosition(Scene)) < 0) return; Scene.Cloth.Rotate(e.Delta / 120.0 * .10); Wake(); e.Handled = true; };
            PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) { if (Scene.Cloth.Pick(e.GetPosition(Scene)) >= 0) { EndDrag(); owner.ShowQuickMenu(); e.Handled = true; } };
            LostMouseCapture += delegate { EndDrag(); };
            Closed += delegate { animation.Stop(); hover.Stop(); if (Scene != null) SavePose(); };
        }
        void Initialize(object sender, EventArgs e)
        {
            Handle = new WindowInteropHelper(this).Handle;
            uint dpi = 96; try { dpi = GetDpiForWindow(Handle); } catch (EntryPointNotFoundException) { }
            Dpi = Math.Max(1, dpi / 96.0);
            Width = Screen.Bounds.Width / Dpi; Height = Screen.Bounds.Height / Dpi;
            Scene = new RugScene(Width, Height); Content = Scene;
            new WindowInteropHelper(this).EnsureHandle();
            HwndSource.FromHwnd(Handle).AddHook(Hook);
            SetWindowLongPtr(Handle, -20, new IntPtr(GetWindowLongPtr(Handle, -20).ToInt64() | 0x08000000L | 0x80L));
            DesktopWindow.Attach(Handle, Screen.Bounds);
            // Cross-process parenting can change DPI awareness; use the final HWND DPI.
            uint attachedDpi = dpi; try { attachedDpi = GetDpiForWindow(Handle); } catch (EntryPointNotFoundException) { }
            if (attachedDpi != dpi)
            {
                Dpi = Math.Max(1, attachedDpi / 96.0); Width = Screen.Bounds.Width / Dpi; Height = Screen.Bounds.Height / Dpi;
                Scene = new RugScene(Width, Height); Content = Scene;
            }
            Reset(false, 75);
            Loaded += delegate { DesktopWindow.Attach(Handle, Screen.Bounds); };
            hover.Start(); Wake();
        }
        IntPtr Hook(IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (message == 0x21) { handled = true; return new IntPtr(3); }
            return IntPtr.Zero;
        }
        void Down(object sender, MouseButtonEventArgs e)
        {
            var point = e.GetPosition(Scene); int id = Scene.Cloth.Pick(point); if (id < 0) return;
            dragging = true; Scene.Cloth.BeginGrab(id, point, 125); Mouse.Capture(this); Wake(); e.Handled = true;
        }
        void Move(object sender, MouseEventArgs e) { if (dragging) { Scene.Cloth.MoveGrab(e.GetPosition(Scene)); Wake(); e.Handled = true; } }
        void Up(object sender, MouseButtonEventArgs e) { if (dragging) { EndDrag(); e.Handled = true; } }
        void EndDrag()
        {
            if (!dragging) return; dragging = false; Scene.Cloth.EndGrab(); if (Mouse.Captured == this) Mouse.Capture(null); Wake();
        }
        void PollHover(object sender, EventArgs e)
        {
            if (dragging) { if (GetAsyncKeyState(0x1B) < 0 || GetAsyncKeyState(1) >= 0) EndDrag(); return; }
            NativePoint point; if (!GetCursorPos(out point) || Scene == null) return;
            bool busy = wasTransparent && (GetAsyncKeyState(1) < 0 || GetAsyncKeyState(2) < 0);
            bool hit = !busy && Scene.Cloth.Pick(new Point((point.X - Screen.Bounds.X) / Dpi, (point.Y - Screen.Bounds.Y) / Dpi)) >= 0;
            bool transparent = !hit;
            if (transparent != wasTransparent)
            {
                long style = GetWindowLongPtr(Handle, -20).ToInt64();
                SetWindowLongPtr(Handle, -20, new IntPtr(transparent ? style | 0x20L : style & ~0x20L)); wasTransparent = transparent;
            }
            Cursor = hit ? Cursors.Hand : Cursors.Arrow;
        }
        void Animate(object sender, EventArgs e)
        {
            Scene.Cloth.Step(1.0 / 30); Scene.UpdateMesh();
            // Dense contacts can retain tiny substep jitter after the visible cloth settles.
            bool quiet = Scene.Cloth.MaxSpeed < 4 || (Scene.Cloth.MaxFrameMotion < .5 && Scene.Cloth.AverageFrameMotion < .04);
            if (!dragging && !Scene.Cloth.IsGrabbing && quiet) calm++; else calm = 0;
            if (calm > 45) { animation.Stop(); SavePose(); }
        }
        public void Wake() { calm = 0; animation.Start(); }
        public void SavePose()
        {
            if (Scene == null || Scene.Cloth == null) return;
            owner.Preferences.CenterX = Scene.Cloth.Center.X / Scene.Width; owner.Preferences.CenterY = Scene.Cloth.Center.Y / Scene.Height;
            owner.Preferences.Angle = Scene.Cloth.Angle;
            try { owner.Preferences.Save(); } catch (Exception e) { Log.Write("Could not save rug position: " + e.Message); }
        }
        public void Reset(bool keep, double drop)
        {
            var p = owner.Preferences;
            Point center = keep && Scene.Cloth != null ? Scene.Cloth.Center : new Point(Scene.Width * p.CenterX, Scene.Height * p.CenterY);
            double angle = keep && Scene.Cloth != null ? Scene.Cloth.Angle : p.Angle;
            Scene.SetRug(owner.TexturePath, p.Scale, center, angle, drop); Wake();
        }
        public void CenterRug() { Scene.Cloth.Layout(new Point(Scene.Width / 2, Scene.Height / 2), 0, 70); Wake(); }
        public void SetIcons(List<DesktopIcon> icons) { if (Scene.SetIcons(icons, Screen.Bounds, Dpi)) Wake(); }
        public void CoverIcons(List<DesktopIcon> icons)
        {
            var onScreen = icons.Where(i => Screen.Bounds.Contains(i.X + i.Width / 2, i.Y + i.Height / 2)).ToList();
            if (onScreen.Count == 0) { Reset(true, 100); return; }
            double x = (onScreen.Min(i => i.X) + onScreen.Max(i => i.X + i.Width)) / 2.0 - Screen.Bounds.X;
            double y = (onScreen.Min(i => i.Y) + onScreen.Max(i => i.Y + i.Height)) / 2.0 - Screen.Bounds.Y;
            x = Math.Max(Scene.Cloth.Width / 2, Math.Min(Scene.Width - Scene.Cloth.Width / 2, x / Dpi));
            y = Math.Max(Scene.Cloth.Height / 2, Math.Min(Scene.Height - Scene.Cloth.Height / 2, y / Dpi));
            Scene.Cloth.Layout(new Point(x, y), 0, 100); Wake();
        }
    }
}

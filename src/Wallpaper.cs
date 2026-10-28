using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace RugWindows
{
    public sealed class WallpaperService
    {
        readonly object gate = new object();
        readonly Preferences prefs;
        readonly IWallpaperBackend backend;
        readonly string dataDirectory;
        readonly string cacheDirectory;
        readonly Action savePreferences;
        static readonly string PublishedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes", "UnderTheRugWindows");

        public WallpaperService(Preferences preferences)
            : this(preferences, new WindowsWallpaperBackend(PublishedDirectory), Preferences.DataDirectory, preferences.Save, PublishedDirectory) { }

        // Isolated tests inject a backend and a settings writer; no native desktop calls are made.
        internal WallpaperService(Preferences preferences, IWallpaperBackend nativeBackend, string directory, Action save, string publishedDirectory = null)
        {
            if (preferences == null || nativeBackend == null || save == null) throw new ArgumentNullException();
            prefs = preferences; backend = nativeBackend; dataDirectory = Path.GetFullPath(directory); savePreferences = save;
            cacheDirectory = Path.GetFullPath(publishedDirectory ?? directory);
        }

        public void Apply(string source)
        {
            lock (gate)
            {
                // Fully decode and publish a durable, opaque BMP before touching Windows or its settings.
                string target = WallpaperImages.Normalize(source, Path.Combine(cacheDirectory, "floors"), "floor-v2-");
                WallpaperState current = backend.Read();
                SaveOriginal(current);
                ApplyWithRecovery(target, "10", "0", current, "Не удалось установить пол. Прежние обои и копия настроек сохранены.");
            }
        }

        public bool RepairCurrentFloor()
        {
            lock (gate)
            {
                // Never turn an unrelated wallpaper into a floor on startup, or invent an original snapshot.
                if (!prefs.WallpaperSaved) return false;
                WallpaperState current = backend.Read();
                string legacy = LegacyFloorPath(current);
                if (legacy == null) return false;
                string target = WallpaperImages.Normalize(legacy, Path.Combine(cacheDirectory, "floors"), "floor-v2-");
                if (!WallpaperImages.SamePath(LegacyFloorPath(backend.Read()), legacy)) return false;
                SaveOriginal(current);
                ApplyWithRecovery(target, "10", "0", current, "Не удалось исправить фон пола. Резервная копия прежних обоев сохранена.");
                Log.Write("Migrated legacy floor wallpaper to verified 24-bit BMP.");
                return true;
            }
        }

        public void Restore()
        {
            lock (gate)
            {
                if (!prefs.WallpaperSaved) return;
                string oldBackup = prefs.PreviousWallpaperBackup;
                if (PublishExistingBackup())
                {
                    try { savePreferences(); }
                    catch { prefs.PreviousWallpaperBackup = oldBackup; throw; }
                }
                string target = RestoreImage(); // Decode before any native mutation; failed restore keeps the snapshot.
                WallpaperState current = backend.Read();
                ApplyWithRecovery(target, prefs.PreviousStyle ?? "10", prefs.PreviousTile ?? "0", current,
                    "Не удалось восстановить обои. Резервная копия и настройки сохранены; повторите восстановление.");
                string previous = prefs.PreviousWallpaper, backup = prefs.PreviousWallpaperBackup, hash = prefs.PreviousWallpaperHash;
                prefs.WallpaperSaved = false; prefs.PreviousWallpaper = null; prefs.PreviousWallpaperBackup = null; prefs.PreviousWallpaperHash = null;
                try { savePreferences(); }
                catch
                {
                    prefs.WallpaperSaved = true; prefs.PreviousWallpaper = previous; prefs.PreviousWallpaperBackup = backup; prefs.PreviousWallpaperHash = hash;
                    throw;
                }
            }
        }

        bool IsLegacyFloor(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            try
            {
                string extension = Path.GetExtension(path);
                return (String.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) || String.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)) &&
                    WallpaperImages.SamePath(Path.GetDirectoryName(Path.GetFullPath(path)), Path.Combine(dataDirectory, "floors"));
            }
            catch (Exception ex) { if (!(ex is ArgumentException) && !(ex is NotSupportedException)) throw; return false; }
        }

        string LegacyFloorPath(WallpaperState state)
        {
            if (IsLegacyFloor(state.Path)) return state.Path;
            // Failed SPI JPEG decoding can leave the configured path while the modern API reports no visible image.
            if (String.IsNullOrEmpty(state.Path) && IsLegacyFloor(state.LegacyPath)) return state.LegacyPath;
            return null;
        }

        void SaveOriginal(WallpaperState current)
        {
            bool hadSnapshot = prefs.WallpaperSaved;
            string oldPath = prefs.PreviousWallpaper, oldStyle = prefs.PreviousStyle, oldTile = prefs.PreviousTile;
            string oldBackup = prefs.PreviousWallpaperBackup, oldHash = prefs.PreviousWallpaperHash;
            if (!hadSnapshot)
            {
                prefs.PreviousWallpaper = current.Path ?? ""; prefs.PreviousStyle = current.Style; prefs.PreviousTile = current.Tile;
            }
            try
            {
                string backupDirectory = Path.Combine(cacheDirectory, "wallpaper-backups");
                PublishExistingBackup();
                if (String.IsNullOrEmpty(prefs.PreviousWallpaperBackup) && !String.IsNullOrEmpty(prefs.PreviousWallpaper))
                {
                    try
                    {
                        // Use the INITIAL saved path even while migrating an already selected legacy floor.
                        prefs.PreviousWallpaperBackup = WallpaperImages.Normalize(prefs.PreviousWallpaper, backupDirectory, "original-v1-");
                        prefs.PreviousWallpaperHash = WallpaperImages.Hash(prefs.PreviousWallpaper);
                    }
                    catch (Exception ex)
                    {
                        if (!WallpaperImages.IsImageError(ex)) throw;
                        Log.Write("Original wallpaper file could not be copied; retaining its original path: " + ex.Message);
                    }
                }
                prefs.WallpaperSaved = true;
                if (!hadSnapshot || oldBackup != prefs.PreviousWallpaperBackup || oldHash != prefs.PreviousWallpaperHash) savePreferences();
            }
            catch
            {
                prefs.WallpaperSaved = hadSnapshot; prefs.PreviousWallpaper = oldPath; prefs.PreviousStyle = oldStyle; prefs.PreviousTile = oldTile;
                prefs.PreviousWallpaperBackup = oldBackup; prefs.PreviousWallpaperHash = oldHash;
                throw;
            }
        }

        bool PublishExistingBackup()
        {
            string folder = Path.Combine(cacheDirectory, "wallpaper-backups");
            if (String.IsNullOrEmpty(prefs.PreviousWallpaperBackup) || !WallpaperImages.Valid(prefs.PreviousWallpaperBackup)) return false;
            if (WallpaperImages.SamePath(Path.GetDirectoryName(Path.GetFullPath(prefs.PreviousWallpaperBackup)), folder)) return false;
            // The shell can reject images from the old app-data cache even when this process can read them.
            // Migrate captured pixels, retaining the initial original path, hash and display snapshot unchanged.
            prefs.PreviousWallpaperBackup = WallpaperImages.Normalize(prefs.PreviousWallpaperBackup, folder, "original-v1-");
            return true;
        }

        string RestoreImage()
        {
            if (String.IsNullOrEmpty(prefs.PreviousWallpaper)) return "";
            if (WallpaperImages.Valid(prefs.PreviousWallpaper) &&
                (String.IsNullOrEmpty(prefs.PreviousWallpaperHash) || String.Equals(prefs.PreviousWallpaperHash, WallpaperImages.Hash(prefs.PreviousWallpaper), StringComparison.Ordinal)))
                return prefs.PreviousWallpaper;
            if (WallpaperImages.Valid(prefs.PreviousWallpaperBackup)) return prefs.PreviousWallpaperBackup;
            throw new InvalidOperationException("Исходное изображение обоев недоступно. Резервные настройки сохранены; верните файл и повторите восстановление.");
        }

        void ApplyWithRecovery(string path, string style, string tile, WallpaperState current, string message)
        {
            try { backend.Set(path, style, tile); }
            catch (Exception ex)
            {
                // A native operation can fail after changing part of the state. Keep the original durable snapshot either way.
                try { if (String.IsNullOrEmpty(current.Path) || WallpaperImages.Valid(current.Path)) backend.Set(current.Path ?? "", current.Style, current.Tile); }
                catch (Exception recovery) { Log.Write("Wallpaper rollback failed: " + recovery.Message); }
                throw new InvalidOperationException(message + " " + ex.Message, ex);
            }
        }
    }

    internal sealed class WallpaperState
    {
        internal string Path, LegacyPath, Style, Tile;
    }

    internal interface IWallpaperBackend
    {
        WallpaperState Read();
        void Set(string path, string style, string tile);
    }

    internal static class WallpaperImages
    {
        internal static bool SamePath(string first, string second)
        {
            if (String.IsNullOrEmpty(first) || String.IsNullOrEmpty(second)) return String.IsNullOrEmpty(first) && String.IsNullOrEmpty(second);
            return String.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        internal static string Hash(string path)
        {
            using (FileStream input = File.OpenRead(path))
            using (SHA256 algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        internal static string Normalize(string source, string folder, string prefix)
        {
            if (String.IsNullOrWhiteSpace(source)) throw new InvalidDataException("Не выбран файл изображения.");
            string temporary = null;
            try
            {
                using (FileStream input = new FileStream(Path.GetFullPath(source), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length == 0 || input.Length > 128L * 1024 * 1024) throw new InvalidDataException("Файл изображения пуст или слишком велик.");
                    using (Image original = Image.FromStream(input, true, true))
                    {
                        CheckSize(original);
                        using (Bitmap bitmap = new Bitmap(original.Width, original.Height, PixelFormat.Format24bppRgb))
                        {
                            using (Graphics graphics = Graphics.FromImage(bitmap))
                            {
                                graphics.Clear(Color.White);
                                graphics.DrawImageUnscaled(original, 0, 0); // Forces decoding, removes alpha and embedded-format ambiguity.
                            }
                            Directory.CreateDirectory(folder);
                            temporary = Path.Combine(folder, ".wallpaper-" + Guid.NewGuid().ToString("N") + ".tmp");
                            using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                            { bitmap.Save(output, ImageFormat.Bmp); output.Flush(true); }
                        }
                    }
                }
                string hash = Hash(temporary), target = Path.Combine(folder, prefix + hash + ".bmp");
                if (File.Exists(target))
                {
                    if (String.Equals(Hash(target), hash, StringComparison.Ordinal)) return target;
                    target = Path.Combine(folder, prefix + hash + "-" + Guid.NewGuid().ToString("N") + ".bmp");
                }
                File.Move(temporary, target); // Same-volume atomic publish; existing or active files are never overwritten.
                return target;
            }
            catch (Exception ex)
            {
                if (!IsImageError(ex)) throw;
                throw new InvalidDataException("Не удалось прочитать изображение обоев. Выберите исправный JPEG, PNG или BMP. " + ex.Message, ex);
            }
            finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static bool Valid(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            try
            {
                using (FileStream input = File.OpenRead(path))
                using (Image image = Image.FromStream(input, true, true)) { CheckSize(image); using (Bitmap decoded = new Bitmap(image)) { decoded.GetPixel(0, 0); } }
                return true;
            }
            catch (Exception ex) { if (!IsImageError(ex)) throw; return false; }
        }

        static void CheckSize(Image image)
        {
            if (image.Width <= 0 || image.Height <= 0 || image.Width > 32768 || image.Height > 32768 || (long)image.Width * image.Height > 100000000)
                throw new InvalidDataException("Изображение обоев слишком велико.");
        }
        internal static bool IsImageError(Exception ex)
        { return ex is ArgumentException || ex is IOException || ex is ExternalException || ex is OutOfMemoryException || ex is UnauthorizedAccessException || ex is NotSupportedException; }
    }

    internal sealed class WindowsWallpaperBackend : IWallpaperBackend
    {
        readonly string directory;
        internal WindowsWallpaperBackend(string dataDirectory) { directory = dataDirectory; }

        public WallpaperState Read()
        {
            string legacyPath = ReadLegacyPath(), path = legacyPath;
            IDesktopWallpaper wallpaper = CreateDesktopWallpaper();
            if (wallpaper != null)
            {
                try
                {
                    string current;
                    int result = wallpaper.GetWallpaper(null, out current);
                    Check(result);
                    if (result == 0) path = current ?? ""; // S_FALSE means per-monitor wallpapers; preserve legacy primary-path behavior.
                }
                finally { Marshal.FinalReleaseComObject(wallpaper); }
            }
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop"))
            {
                if (key == null) throw new InvalidOperationException("Не удалось прочитать настройки рабочего стола Windows.");
                return new WallpaperState { Path = path, LegacyPath = legacyPath, Style = Convert.ToString(key.GetValue("WallpaperStyle", "10")), Tile = Convert.ToString(key.GetValue("TileWallpaper", "0")) };
            }
        }

        public void Set(string path, string style, string tile)
        {
            IDesktopWallpaper wallpaper = CreateDesktopWallpaper();
            if (wallpaper == null)
            {
                string legacyTarget = String.IsNullOrEmpty(path) ? "" : WallpaperImages.Normalize(path, Path.Combine(directory, "floors"), "compatible-v2-");
                SetRegistryStyle(style, tile);
                if (!SystemParametersInfo(20, 0, legacyTarget, 3)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не разрешила сменить обои.");
                if (!WallpaperImages.SamePath(ReadLegacyPath(), legacyTarget)) throw new InvalidOperationException("Windows не подтвердила выбранные обои.");
                return;
            }
            try
            {
                Check(wallpaper.SetPosition(Position(style, tile)));
                Check(wallpaper.SetWallpaper(null, path ?? ""));
                if (!String.IsNullOrEmpty(path)) Check(wallpaper.Enable(true));
                string actual;
                int result = wallpaper.GetWallpaper(null, out actual);
                Check(result);
                if (result != 0 || !WallpaperImages.SamePath(actual, path)) throw new InvalidOperationException("Windows не подтвердила выбранное изображение обоев.");
                SetRegistryStyle(style, tile);
            }
            finally { Marshal.FinalReleaseComObject(wallpaper); }
        }

        internal static bool IsComUnavailable(Exception ex)
        {
            COMException com = ex as COMException;
            return com != null && (com.ErrorCode == unchecked((int)0x80040154) || com.ErrorCode == unchecked((int)0x80040111) || com.ErrorCode == unchecked((int)0x80004002));
        }

        static IDesktopWallpaper CreateDesktopWallpaper()
        {
            object instance = null;
            try
            {
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")));
                return (IDesktopWallpaper)instance;
            }
            catch (Exception ex)
            {
                if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
                if (!IsComUnavailable(ex) && !(ex is InvalidCastException)) throw;
                Log.Write("IDesktopWallpaper unavailable; using verified BMP with SystemParametersInfo.");
                return null;
            }
        }

        static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        static int Position(string style, string tile)
        {
            if (tile == "1") return 1;
            switch (style) { case "0": return 0; case "2": return 2; case "6": return 3; case "22": return 5; default: return 4; }
        }
        static void SetRegistryStyle(string style, string tile)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop", true))
            {
                if (key == null) throw new InvalidOperationException("Windows не разрешила изменить настройки обоев.");
                key.SetValue("WallpaperStyle", style ?? "10"); key.SetValue("TileWallpaper", tile ?? "0");
            }
        }
        static string ReadLegacyPath()
        {
            StringBuilder path = new StringBuilder(32768);
            if (!SystemParametersInfo(0x73, (uint)path.Capacity, path, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось прочитать текущие обои Windows.");
            return path.ToString();
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SystemParametersInfo(uint action, uint parameter, StringBuilder value, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SystemParametersInfo(uint action, uint parameter, string value, uint flags);

        // Exact vtable order and GUID from Microsoft's ShObjIdl_core.h, including unused slots.
        // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/ShObjIdl_core.h
        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDesktopWallpaper
        {
            [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] string path);
            [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string monitor);
            [PreserveSig] int GetMonitorDevicePathCount(out uint count);
            [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor, out MonitorRectangle bounds);
            [PreserveSig] int SetBackgroundColor(uint color);
            [PreserveSig] int GetBackgroundColor(out uint color);
            [PreserveSig] int SetPosition(int position);
            [PreserveSig] int GetPosition(out int position);
            [PreserveSig] int SetSlideshow(IntPtr items);
            [PreserveSig] int GetSlideshow(out IntPtr items);
            [PreserveSig] int SetSlideshowOptions(uint options, uint milliseconds);
            [PreserveSig] int GetSlideshowOptions(out uint options, out uint milliseconds);
            [PreserveSig] int AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitor, int direction);
            [PreserveSig] int GetStatus(out uint state);
            [PreserveSig] int Enable([MarshalAs(UnmanagedType.Bool)] bool enabled);
        }
        [StructLayout(LayoutKind.Sequential)] struct MonitorRectangle { internal int Left, Top, Right, Bottom; }
    }
}

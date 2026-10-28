using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RugWindows
{
    public sealed class DesktopIcon
    {
        public string Name;
        public int X, Y, Width, Height;
    }

    /// <summary>
    /// Reads Explorer's desktop list view; changes positions only after a durable backup exists.
    /// Explorer's window hierarchy is an implementation detail, so missing/changed hosts fail closed.
    /// No file-system desktop items are renamed, opened, moved, or deleted by this service.
    /// </summary>
    public sealed class DesktopService : IDisposable
    {
        private readonly object gate = new object();
        private readonly string layoutPath;
        private readonly Func<DesktopHandles> desktopProvider;
        private bool disposed;
        private string status = "Ожидание рабочего стола";

        public DesktopService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnderTheRugWindows", "desktop-layout.json")) { }

        internal DesktopService(string savedLayoutPath) : this(savedLayoutPath, null) { }
        // Isolated test fixtures supply their own list view; the production constructor always finds Explorer.
        internal DesktopService(string savedLayoutPath, Func<DesktopHandles> provider)
        { layoutPath = Path.GetFullPath(savedLayoutPath); desktopProvider = provider; }

        public bool HasSavedLayout { get { return File.Exists(layoutPath); } }
        public string Status { get { return status; } }
        public IntPtr DesktopHost { get { DesktopHandles h; return TryFindDesktop(out h) ? h.Host : IntPtr.Zero; } }
        public bool AutoArrange
        {
            get
            {
                DesktopHandles h;
                return TryFindDesktop(out h) && (Native.Style(h.List) & 0x0100L) != 0;
            }
        }
        public bool AlignToGrid
        {
            get
            {
                DesktopHandles h;
                long extended;
                return TryFindDesktop(out h) && Native.TryExtendedStyle(h.List, out extended) && (extended & 0x00080000L) != 0;
            }
        }

        public List<DesktopIcon> Scan()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                try
                {
                    DesktopHandles h = RequireDesktop(false);
                    if (!Native.IsWindowVisible(h.List))
                    {
                        status = "Значки рабочего стола скрыты: Вид → Отображать значки рабочего стола";
                        return new List<DesktopIcon>();
                    }
                    using (RemoteDesktop session = new RemoteDesktop(h))
                    {
                        List<IndexedIcon> items = session.ReadAll();
                        List<DesktopIcon> result = new List<DesktopIcon>(items.Count);
                        foreach (IndexedIcon item in items) result.Add(item.Visual);
                        status = (AutoArrange ? "Включено автоматическое упорядочивание; " : "") +
                            (AlignToGrid ? "включено выравнивание по сетке; " : "") +
                            "значков: " + result.Count;
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    status = Friendly(ex);
                    return new List<DesktopIcon>();
                }
            }
        }

        public void Sweep(Point screenCenter)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                try
                {
                    DesktopHandles h = RequireDesktop(true);
                    if (!Native.IsWindowVisible(h.List))
                        throw new InvalidOperationException("Включите «Вид → Отображать значки рабочего стола» в меню рабочего стола.");
                    using (RemoteDesktop session = new RemoteDesktop(h))
                    {
                        List<IndexedIcon> items = session.ReadAll();
                        if (items.Count == 0) { status = "На рабочем столе нет значков"; return; }
                        LayoutSnapshot original;
                        if (File.Exists(layoutPath)) original = LoadSnapshot();
                        else
                        {
                            original = MakeSnapshot(items);
                            SaveNewSnapshot(original); // Must complete, including Flush(true), before the first move.
                        }
                        Dictionary<string, SavedIcon> homes = IndexSnapshot(original);
                        int moved = 0, skipped = 0;
                        session.ValidateInventory(items);
                        foreach (IndexedIcon item in items)
                        {
                            if (!homes.ContainsKey(Identity(item.Visual.Name, item.Occurrence))) { skipped++; continue; }
                            double angle = moved * 2.399963229728653;
                            double radius = Math.Min(150, 12 * Math.Sqrt(moved));
                            int cx = screenCenter.X + (int)Math.Round(Math.Cos(angle) * radius);
                            int cy = screenCenter.Y + (int)Math.Round(Math.Sin(angle) * radius * 0.8);
                            Point target = new Point(cx - item.Visual.Width / 2 - (item.Visual.X - item.Position.X),
                                cy - item.Visual.Height / 2 - (item.Visual.Y - item.Position.Y));
                            session.SetPosition(item, target);
                            moved++;
                        }
                        status = "Заметено под ковёр: " + moved + (skipped > 0 ?
                            ". Новые значки оставлены на местах; сначала восстановите исходную раскладку." : "");
                    }
                }
                catch (Exception ex) { status = Friendly(ex); throw new InvalidOperationException(status, ex); }
            }
        }

        public void Restore()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (!File.Exists(layoutPath)) { status = "Сохранённой раскладки нет"; return; }
                try
                {
                    LayoutSnapshot original = LoadSnapshot();
                    DesktopHandles h = RequireDesktop(true);
                    using (RemoteDesktop session = new RemoteDesktop(h))
                    {
                        List<IndexedIcon> items = session.ReadAll();
                        Dictionary<string, IndexedIcon> current = new Dictionary<string, IndexedIcon>(StringComparer.Ordinal);
                        foreach (IndexedIcon item in items) current.Add(Identity(item.Visual.Name, item.Occurrence), item);
                        session.ValidateInventory(items);
                        int restored = 0, missing = 0, unavailable = 0;
                        foreach (SavedIcon saved in original.Icons)
                        {
                            IndexedIcon item;
                            if (!current.TryGetValue(Identity(saved.Name, saved.Occurrence), out item)) { missing++; continue; }
                            Point target = new Point(saved.X, saved.Y);
                            if (!Native.VirtualBounds.Contains(target)) { unavailable++; continue; }
                            session.SetPosition(item, target);
                            restored++;
                        }
                        if (missing > 0 || unavailable > 0)
                            throw new InvalidOperationException("Восстановлено: " + restored + ". Не найдены значки: " + missing +
                                "; недоступен прежний монитор: " + unavailable + ". Резервная раскладка сохранена: повторите восстановление после возвращения значков или монитора.");
                        // Any error above leaves the complete original snapshot available for another attempt.
                        File.Delete(layoutPath);
                        status = "Исходные позиции значков восстановлены: " + restored;
                    }
                }
                catch (Exception ex) { status = Friendly(ex); throw new InvalidOperationException(status, ex); }
            }
        }

        private DesktopHandles RequireDesktop(bool moving)
        {
            DesktopHandles h;
            if (!TryFindDesktop(out h))
                throw new InvalidOperationException("Рабочий стол Проводника недоступен. Дождитесь запуска Проводника и повторите попытку.");
            if (moving)
            {
                long extended;
                if (!Native.TryExtendedStyle(h.List, out extended))
                    throw new InvalidOperationException("Проводник не отвечает. Дождитесь его восстановления и повторите попытку.");
                if ((Native.Style(h.List) & 0x0100L) != 0 || (extended & 0x00080000L) != 0)
                    throw new InvalidOperationException("Проводник сам выравнивает значки. Щёлкните правой кнопкой по рабочему столу → Вид и отключите обе настройки: «Упорядочить значки автоматически» и «Выровнять значки по сетке». Затем повторите попытку. Сохранённая раскладка не удаляется.");
            }
            return h;
        }

        private bool TryFindDesktop(out DesktopHandles handles)
        {
            if (desktopProvider == null) return DesktopHandles.TryFind(out handles);
            handles = desktopProvider();
            return handles != null && Native.IsWindow(handles.Host) && Native.IsWindow(handles.List);
        }

        private static LayoutSnapshot MakeSnapshot(List<IndexedIcon> items)
        {
            LayoutSnapshot snapshot = new LayoutSnapshot();
            snapshot.Version = 1;
            snapshot.SavedUtc = DateTime.UtcNow.ToString("o");
            snapshot.Icons = new List<SavedIcon>();
            foreach (IndexedIcon item in items)
                snapshot.Icons.Add(new SavedIcon { Name = item.Visual.Name, Occurrence = item.Occurrence,
                    X = item.Position.X, Y = item.Position.Y });
            return snapshot;
        }

        private void SaveNewSnapshot(LayoutSnapshot snapshot)
        {
            string folder = Path.GetDirectoryName(layoutPath);
            Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, "desktop-layout." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    new DataContractJsonSerializer(typeof(LayoutSnapshot)).WriteObject(stream, snapshot);
                    stream.Flush(true);
                }
                // Same-volume rename is atomic. File.Move refuses to replace any earlier snapshot.
                File.Move(temporary, layoutPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private LayoutSnapshot LoadSnapshot()
        {
            LayoutSnapshot snapshot;
            using (FileStream stream = new FileStream(layoutPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Файл резервной раскладки слишком велик; исходный файл сохранён.");
                snapshot = new DataContractJsonSerializer(typeof(LayoutSnapshot)).ReadObject(stream) as LayoutSnapshot;
            }
            if (snapshot == null || snapshot.Version != 1 || snapshot.Icons == null || snapshot.Icons.Count > 10000)
                throw new InvalidDataException("Не удалось прочитать резервную раскладку. Файл сохранён: " + layoutPath);
            IndexSnapshot(snapshot); // Validate all entries before moving any icon.
            return snapshot;
        }

        private static Dictionary<string, SavedIcon> IndexSnapshot(LayoutSnapshot snapshot)
        {
            Dictionary<string, SavedIcon> result = new Dictionary<string, SavedIcon>(StringComparer.Ordinal);
            foreach (SavedIcon icon in snapshot.Icons)
            {
                if (icon == null || String.IsNullOrEmpty(icon.Name) || icon.Name.Length > 2047 || icon.Occurrence < 0 ||
                    icon.Occurrence > 9999 || Math.Abs((long)icon.X) > 1000000 || Math.Abs((long)icon.Y) > 1000000)
                    throw new InvalidDataException("Резервная раскладка содержит недопустимые данные; файл сохранён.");
                string key = Identity(icon.Name, icon.Occurrence);
                if (result.ContainsKey(key)) throw new InvalidDataException("В резервной раскладке повторяется идентификатор значка; файл сохранён.");
                result.Add(key, icon);
            }
            return result;
        }

        private static string Identity(string name, int occurrence) { return occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + name; }
        private static string Friendly(Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                return "Нет доступа к Проводнику или резервной раскладке. Запустите программу от того же пользователя, что и Проводник. " + ex.Message;
            if (ex is SerializationException) return "Не удалось прочитать резервную раскладку. Исходный файл сохранён. " + ex.Message;
            return ex.Message;
        }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException("DesktopService"); }
        public void Dispose() { lock (gate) { disposed = true; } }

        [DataContract]
        private sealed class LayoutSnapshot
        {
            [DataMember(Order = 0)] public int Version;
            [DataMember(Order = 1)] public string SavedUtc;
            [DataMember(Order = 2)] public List<SavedIcon> Icons;
        }
        [DataContract]
        private sealed class SavedIcon
        {
            [DataMember(Order = 0)] public string Name;
            [DataMember(Order = 1)] public int Occurrence;
            [DataMember(Order = 2)] public int X;
            [DataMember(Order = 3)] public int Y;
        }
    }

    /// <summary>A child of Explorer's desktop, so the rug never becomes a topmost application overlay.</summary>
    public static class DesktopWindow
    {
        public static bool IsAlive(IntPtr hwnd) { return hwnd != IntPtr.Zero && Native.IsWindow(hwnd); }
        public static bool IsAttached(IntPtr hwnd)
        {
            DesktopHandles h;
            return IsAlive(hwnd) && DesktopHandles.TryFind(out h) && Native.GetParent(hwnd) == h.Host &&
                (Native.Style(hwnd) & 0x40000000L) != 0;
        }

        public static void Attach(IntPtr hwnd, Rectangle physicalBounds)
        {
            if (!IsAlive(hwnd)) throw new InvalidOperationException("Окно ковра недоступно; создайте его заново после перезапуска Проводника.");
            Native.ShowWindow(hwnd, 0);
            try
            {
                if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
                    throw new ArgumentOutOfRangeException("physicalBounds");
                DesktopHandles h;
                if (!DesktopHandles.TryFind(out h))
                    throw new InvalidOperationException("Не удалось найти рабочий стол Проводника. Ковёр скрыт; повторите попытку после запуска Проводника.");
                long style = (Native.Style(hwnd) & ~0x80000000L) | 0x40000000L;
                Native.SetStyle(hwnd, -16, style);
                long extended = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
                Native.SetStyle(hwnd, -20, (extended | 0x08000000L | 0x00000080L) & ~0x00040000L);
                Native.SetLastError(0);
                IntPtr previous = Native.SetParent(hwnd, h.Host);
                if (previous == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Проводник не принял окно ковра");
                if (Native.GetParent(hwnd) != h.Host) throw new InvalidOperationException("Окно ковра не прикреплено к рабочему столу; оно скрыто.");
                Native.Point point = new Native.Point(physicalBounds.X, physicalBounds.Y);
                if (!Native.ScreenToClient(h.Host, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!Native.SetWindowPos(hwnd, IntPtr.Zero, point.X, point.Y, physicalBounds.Width, physicalBounds.Height,
                    0x0010 | 0x0020 | 0x0040)) throw new Win32Exception(Marshal.GetLastWin32Error());
                Native.Rect actual;
                if (!Native.GetWindowRect(hwnd, out actual) || Math.Abs(actual.Left - physicalBounds.Left) > 2 ||
                    Math.Abs(actual.Top - physicalBounds.Top) > 2 || Math.Abs((actual.Right - actual.Left) - physicalBounds.Width) > 2 ||
                    Math.Abs((actual.Bottom - actual.Top) - physicalBounds.Height) > 2)
                    throw new InvalidOperationException("Проводник изменил масштаб окна ковра. Проверьте настройки масштаба экранов и перезапустите программу.");
            }
            catch { Native.ShowWindow(hwnd, 0); throw; }
        }
    }

    internal sealed class IndexedIcon
    {
        internal int Index, Occurrence;
        internal Point Position;
        internal DesktopIcon Visual;
    }

    internal sealed class DesktopHandles
    {
        internal IntPtr Host, List;
        internal static bool TryFind(out DesktopHandles found)
        {
            DesktopHandles result = null;
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman != IntPtr.Zero) result = FromTop(progman);
            if (result == null)
                Native.EnumWindows(delegate(IntPtr top, IntPtr unused)
                {
                    result = FromTop(top);
                    return result == null;
                }, IntPtr.Zero);
            found = result;
            return result != null;
        }
        private static DesktopHandles FromTop(IntPtr top)
        {
            StringBuilder className = new StringBuilder(128);
            if (Native.GetClassName(top, className, className.Capacity) == 0 ||
                (className.ToString() != "Progman" && className.ToString() != "WorkerW")) return null;
            IntPtr view = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return null;
            IntPtr list = Native.FindWindowEx(view, IntPtr.Zero, "SysListView32", "FolderView");
            if (list == IntPtr.Zero) list = Native.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            uint pid, shellPid;
            if (list == IntPtr.Zero || Native.GetWindowThreadProcessId(list, out pid) == 0 ||
                Native.GetParent(list) != view) return null;
            IntPtr shell = Native.GetShellWindow();
            if (shell == IntPtr.Zero || Native.GetWindowThreadProcessId(shell, out shellPid) == 0 || shellPid != pid) return null;
            return new DesktopHandles { Host = view, List = list };
        }
    }

    internal sealed class RemoteDesktop : IDisposable
    {
        private readonly DesktopHandles handles;
        private IntPtr process, memory;
        private uint processId;
        private bool unsafeToFree;
        private Point clientOrigin, viewOrigin;
        private const int TextOffset = 512, TextCapacity = 2048;

        internal RemoteDesktop(DesktopHandles desktop)
        {
            handles = desktop;
            try
            {
                if (IntPtr.Size != 8) throw new InvalidOperationException("Для доступа к значкам нужен 64-разрядный выпуск программы.");
                Native.GetWindowThreadProcessId(handles.List, out processId);
                if (processId == 0) throw new InvalidOperationException("Проводник перезапустился; повторите попытку.");
                // Probe without a pointer before allocating. A hung Explorer cannot leak buffers on every scan.
                Send(0, IntPtr.Zero, IntPtr.Zero);
                process = Native.OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x1000, false, processId);
                if (process == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, "Не удалось прочитать значки Проводника (ошибка " + error + "): " + new Win32Exception(error).Message);
                }
                bool wow64;
                if (!Native.IsWow64Process(process, out wow64) || wow64)
                    throw new InvalidOperationException("Архитектура Проводника не поддерживается 64-разрядным выпуском программы.");
                memory = Native.VirtualAllocEx(process, IntPtr.Zero, new UIntPtr(8192), 0x1000 | 0x2000, 0x04);
                if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось выделить буфер для чтения значков");
                Native.Point origin = new Native.Point(0, 0);
                if (!Native.ClientToScreen(handles.List, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error());
                clientOrigin = new Point(origin.X, origin.Y);
                Write(0, new byte[8]);
                if (Send(0x1000 + 41, IntPtr.Zero, At(0)).ToInt64() != 0)
                {
                    byte[] bytes = Read(0, 8);
                    viewOrigin = new Point(BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4));
                }
            }
            catch { Dispose(); throw; }
        }

        internal List<IndexedIcon> ReadAll()
        {
            int count = Count();
            List<IndexedIcon> result = new List<IndexedIcon>(count);
            Dictionary<string, int> occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string name = Name(i);
                if (String.IsNullOrEmpty(name)) throw new InvalidOperationException("Проводник не вернул имя значка. Дождитесь обновления рабочего стола и повторите попытку.");
                Point position = Position(i);
                Write(32, StructBytes(new Native.Rect { Left = 1 })); // LVIR_ICON: visual bump excludes the label.
                if (Send(0x1000 + 14, new IntPtr(i), At(32)).ToInt64() == 0)
                    throw new InvalidOperationException("Не удалось получить границы значка; повторите попытку.");
                byte[] rectangle = Read(32, 16);
                int left = BitConverter.ToInt32(rectangle, 0), top = BitConverter.ToInt32(rectangle, 4);
                int width = BitConverter.ToInt32(rectangle, 8) - left, height = BitConverter.ToInt32(rectangle, 12) - top;
                if (width <= 0 || width > 1024 || height <= 0 || height > 1024) { width = 48; height = 48; }
                int occurrence;
                occurrences.TryGetValue(name, out occurrence);
                occurrences[name] = occurrence + 1;
                result.Add(new IndexedIcon { Index = i, Occurrence = occurrence, Position = position,
                    Visual = new DesktopIcon { Name = name, X = clientOrigin.X + left, Y = clientOrigin.Y + top, Width = width, Height = height } });
            }
            ValidateInventory(result);
            return result;
        }

        internal void ValidateInventory(List<IndexedIcon> items)
        {
            if (Count() != items.Count) throw Changed();
            foreach (IndexedIcon item in items) if (!String.Equals(Name(item.Index), item.Visual.Name, StringComparison.Ordinal)) throw Changed();
        }
        private static InvalidOperationException Changed()
        {
            return new InvalidOperationException("Состав значков изменился во время операции. Резервная раскладка сохранена; повторите попытку.");
        }

        internal void SetPosition(IndexedIcon item, Point screen)
        {
            long extended = Send(0x1000 + 55, IntPtr.Zero, IntPtr.Zero).ToInt64();
            if ((Native.Style(handles.List) & 0x0100L) != 0 || (extended & 0x00080000L) != 0)
                throw new InvalidOperationException("Проводник включил выравнивание значков. Отключите «Вид → Упорядочить значки автоматически» и «Вид → Выровнять значки по сетке», затем повторите попытку. Резервная раскладка сохранена.");
            if (!String.Equals(Name(item.Index), item.Visual.Name, StringComparison.Ordinal)) throw Changed();
            Native.Point point = new Native.Point(screen.X - clientOrigin.X + viewOrigin.X, screen.Y - clientOrigin.Y + viewOrigin.Y);
            Write(0, StructBytes(point));
            Send(0x1000 + 49, new IntPtr(item.Index), At(0)); // LVM_SETITEMPOSITION32 has no result.
            Point actual = Position(item.Index);
            if (Math.Abs(actual.X - screen.X) > 2 || Math.Abs(actual.Y - screen.Y) > 2)
                throw new InvalidOperationException("Проводник не принял позицию значка. Отключите автоматическое упорядочивание; исходная раскладка сохранена.");
        }

        private int Count()
        {
            long value = Send(0x1000 + 4, IntPtr.Zero, IntPtr.Zero).ToInt64();
            if (value < 0 || value > 10000) throw new InvalidOperationException("Неподдерживаемое количество значков рабочего стола.");
            return (int)value;
        }
        private string Name(int index)
        {
            Native.LvItem item = new Native.LvItem { Mask = 1, Item = index, Text = At(TextOffset), TextMax = TextCapacity };
            Write(128, StructBytes(item));
            Write(TextOffset, new byte[2]);
            long length = Send(0x1000 + 115, new IntPtr(index), At(128)).ToInt64();
            if (length < 0 || length >= TextCapacity - 1) throw new InvalidOperationException("Проводник вернул некорректное имя значка.");
            return Encoding.Unicode.GetString(Read(TextOffset, checked((int)length * 2)));
        }
        private Point Position(int index)
        {
            Write(0, new byte[8]);
            if (Send(0x1000 + 16, new IntPtr(index), At(0)).ToInt64() == 0)
                throw new InvalidOperationException("Не удалось получить позицию значка. Проводник мог перезапуститься.");
            byte[] bytes = Read(0, 8);
            return new Point(BitConverter.ToInt32(bytes, 0) - viewOrigin.X + clientOrigin.X,
                BitConverter.ToInt32(bytes, 4) - viewOrigin.Y + clientOrigin.Y);
        }
        private IntPtr Send(uint message, IntPtr wParam, IntPtr lParam)
        {
            uint pid;
            Native.GetWindowThreadProcessId(handles.List, out pid);
            if (!Native.IsWindow(handles.List) || pid != processId) throw new InvalidOperationException("Проводник перезапустился; повторите попытку.");
            UIntPtr result;
            Native.SetLastError(0);
            if (Native.SendMessageTimeout(handles.List, message, wParam, lParam, 0x0001 | 0x0002 | 0x0020, 1000, out result) == IntPtr.Zero)
            {
                // A timed-out cross-process message may still use its pointer. Keep that tiny allocation
                // until Explorer exits instead of risking use-after-free in the shell's process.
                unsafeToFree = true;
                throw new InvalidOperationException("Проводник не отвечает. Дождитесь его восстановления и повторите попытку; резервная раскладка сохранена.");
            }
            return new IntPtr(unchecked((long)result.ToUInt64()));
        }
        private IntPtr At(int offset) { return new IntPtr(memory.ToInt64() + offset); }
        private void Write(int offset, byte[] bytes)
        {
            UIntPtr written;
            if (!Native.WriteProcessMemory(process, At(offset), bytes, new UIntPtr((uint)bytes.Length), out written) ||
                written.ToUInt64() != (ulong)bytes.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось записать временный буфер значков");
        }
        private byte[] Read(int offset, int length)
        {
            byte[] bytes = new byte[length];
            if (length == 0) return bytes;
            UIntPtr read;
            if (!Native.ReadProcessMemory(process, At(offset), bytes, new UIntPtr((uint)length), out read) || read.ToUInt64() != (ulong)length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось прочитать временный буфер значков");
            return bytes;
        }
        private static byte[] StructBytes<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            byte[] bytes = new byte[size];
            IntPtr local = Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(value, local, false); Marshal.Copy(local, bytes, 0, size); return bytes; }
            finally { Marshal.FreeHGlobal(local); }
        }
        public void Dispose()
        {
            if (memory != IntPtr.Zero && !unsafeToFree) Native.VirtualFreeEx(process, memory, UIntPtr.Zero, 0x8000);
            memory = IntPtr.Zero;
            if (process != IntPtr.Zero) Native.CloseHandle(process);
            process = IntPtr.Zero;
        }
    }

    internal static class Native
    {
        // Microsoft API references:
        // https://learn.microsoft.com/windows/win32/controls/lvm-getitemtext
        // https://learn.microsoft.com/windows/win32/controls/lvm-getitemposition
        // https://learn.microsoft.com/windows/win32/controls/lvm-setitemposition32
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw
        // Messages >= WM_USER need explicit cross-process marshalling; every pointer below is remote.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setparent
        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct Point
        {
            internal int X, Y;
            internal Point(int x, int y) { X = x; Y = y; }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct LvItem
        {
            internal uint Mask;
            internal int Item, SubItem;
            internal uint State, StateMask;
            internal IntPtr Text;
            internal int TextMax, Image;
            internal IntPtr Param;
            internal int Indent, GroupId;
            internal uint Columns;
            internal IntPtr ColumnPointers, ColumnFormats;
            internal int Group;
        }
        internal static Rectangle VirtualBounds
        {
            get { return new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79)); }
        }
        internal static long Style(IntPtr window) { return GetWindowLongPtr(window, -16).ToInt64(); }
        internal static bool TryExtendedStyle(IntPtr window, out long extended)
        {
            UIntPtr result;
            bool success = SendMessageTimeout(window, 0x1000 + 55, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0002 | 0x0020, 1000, out result) != IntPtr.Zero;
            extended = unchecked((long)result.ToUInt64());
            return success;
        }
        internal static void SetStyle(IntPtr window, int index, long value)
        {
            SetLastError(0);
            if (SetWindowLongPtr(window, index, new IntPtr(value)) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder className, int capacity);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool ScreenToClient(IntPtr hwnd, ref Point point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rectangle);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] internal static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsWow64Process(IntPtr process, out bool wow64);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] internal static extern void SetLastError(uint error);
    }
}

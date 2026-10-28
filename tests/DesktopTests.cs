// Compile with src/Desktop.cs, /platform:x64, System.Drawing, System.Runtime.Serialization and System.Windows.Forms.
// --native uses only an invisible list view in a dedicated fixture process, never the user's desktop.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RugWindows;
using Forms = System.Windows.Forms;

internal static class DesktopSafetyTests
{
    private static int checks;
    private static void Assert(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static object Call(string name, object target, params object[] args)
    {
        try
        {
            return typeof(DesktopService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Invoke(target, args);
        }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--native-fixture") return RunFixture();
        if (args.Length < 1 || args.Length > 2) { Console.Error.WriteLine("Pass a writable test directory and optional --native."); return 2; }
        string folder = Path.Combine(Path.GetFullPath(args[0]), "desktop-safety-" + Guid.NewGuid().ToString("N"));
        string layout = Path.Combine(folder, "desktop-layout.json");
        Directory.CreateDirectory(folder);
        try
        {
            Assert(IntPtr.Size == 8, "Tests and desktop service must run x64.");
            Assert(Marshal.SizeOf(typeof(Native.LvItem)) == 88, "LVITEMW x64 layout mismatch.");
            Assert(Marshal.OffsetOf(typeof(Native.LvItem), "Text").ToInt32() == 24, "LVITEMW pszText offset mismatch.");
            Assert(Marshal.OffsetOf(typeof(Native.LvItem), "ColumnPointers").ToInt32() == 64, "LVITEMW puColumns offset mismatch.");
            List<IndexedIcon> icons = new List<IndexedIcon>();
            icons.Add(new IndexedIcon { Index = 2, Occurrence = 0, Position = new Point(-1850, 37), Visual = new DesktopIcon { Name = "Одинаковое имя 😺" } });
            icons.Add(new IndexedIcon { Index = 7, Occurrence = 1, Position = new Point(110, 241), Visual = new DesktopIcon { Name = "Одинаковое имя 😺" } });
            icons.Add(new IndexedIcon { Index = 8, Occurrence = 0, Position = new Point(999, -300), Visual = new DesktopIcon { Name = "quoted \"item\" \\ slash" } });
            object snapshot = Call("MakeSnapshot", null, icons);
            using (DesktopService service = new DesktopService(layout))
            {
                Assert(!service.HasSavedLayout, "Fresh test must have no saved layout.");
                Call("SaveNewSnapshot", service, snapshot);
                Assert(service.HasSavedLayout, "Successful save must publish the snapshot.");
                byte[] original = File.ReadAllBytes(layout);
                object loaded = Call("LoadSnapshot", service);
                IDictionary identities = (IDictionary)Call("IndexSnapshot", null, loaded);
                Assert(identities.Count == 3, "Unicode and duplicate display names must survive JSON round-trip.");
                Assert(identities.Contains("0:Одинаковое имя 😺") && identities.Contains("1:Одинаковое имя 😺"), "Duplicate occurrences must have independent identities.");
                object savedIcon = identities["0:Одинаковое имя 😺"];
                Assert((int)savedIcon.GetType().GetField("X").GetValue(savedIcon) == -1850, "Negative monitor coordinates must survive persistence.");
                bool refused = false;
                try { Call("SaveNewSnapshot", service, snapshot); } catch (IOException) { refused = true; }
                Assert(refused, "Repeated sweep must refuse to overwrite the original snapshot.");
                Assert(Convert.ToBase64String(original) == Convert.ToBase64String(File.ReadAllBytes(layout)), "Original snapshot must remain byte-for-byte intact.");
                Assert(Directory.GetFiles(folder, "*.tmp").Length == 0, "Failed save must clean its uncommitted temporary file.");
                File.WriteAllText(layout, "{ this is invalid JSON");
                bool corruptRefused = false;
                try { service.Restore(); } catch (InvalidOperationException) { corruptRefused = true; }
                Assert(corruptRefused && File.Exists(layout), "Invalid snapshot must abort restore and preserve the original file.");
                service.Dispose();
                bool disposedRefused = false;
                try { service.Scan(); } catch (ObjectDisposedException) { disposedRefused = true; }
                Assert(disposedRefused, "Disposed service must reject operations.");
            }
            Assert(!DesktopWindow.IsAlive(IntPtr.Zero) && !DesktopWindow.IsAttached(IntPtr.Zero), "Invalid desktop window handles must fail closed.");
            if (args.Length > 1 && args[1] == "--native") CheckNativeFixture(folder);
            Console.WriteLine("Desktop safety checks passed: " + checks + ". No user desktop icons were moved.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        finally
        {
            if (File.Exists(layout)) File.Delete(layout);
            string fixtureLayout = Path.Combine(folder, "fixture-layout.json");
            if (File.Exists(fixtureLayout)) File.Delete(fixtureLayout);
            // Only the unique directory created above; no recursive deletion or external application data.
            foreach (string temporary in Directory.GetFiles(folder, "*.tmp")) File.Delete(temporary);
            Directory.Delete(folder, false);
        }
    }

    private const string DuplicateName = "Повторяющееся имя 😺";
    private const string UniqueName = "Уникальное имя \\ \"тест\"";
    private const string NewName = "Новый значок";

    private static void CheckNativeFixture(string folder)
    {
        ProcessStartInfo start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--native-fixture");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardInput = true;
        string layout = Path.Combine(folder, "fixture-layout.json");
        using (Process fixture = Process.Start(start))
        {
            try
            {
                string[] ready = ReadLine(fixture).Split('|');
                Assert(ready.Length == 3 && ready[0] == "READY", "Native fixture must start its own list view.");
                DesktopHandles handles = new DesktopHandles { Host = new IntPtr(Int64.Parse(ready[1])), List = new IntPtr(Int64.Parse(ready[2])) };
                Command(fixture, "snap:on"); // Desktop's default "Align icons to grid" must not prevent explicit positions.
                using (RemoteDesktop session = new RemoteDesktop(handles))
                {
                    List<IndexedIcon> items = session.ReadAll();
                    Assert(items.Count == 3 && items[0].Visual.Name == DuplicateName, "Cross-process read must preserve Unicode labels.");
                    Assert(items[2].Occurrence == 1, "Native duplicate display names must retain occurrence identities.");
                    Point original = items[0].Position;
                    Command(fixture, "snap:off");
                    try { session.SetPosition(items[0], new Point(70000, 40000)); }
                    catch (InvalidOperationException ex)
                    {
                        Point rejected = session.ReadAll()[0].Position;
                        throw new Exception("32-bit fixture move rejected: expected (70000,40000), actual " + rejected + ".", ex);
                    }
                    List<IndexedIcon> displaced = session.ReadAll();
                    Assert(displaced[0].Position == new Point(70000, 40000), "LVM_SETITEMPOSITION32 must support coordinates beyond 16 bits.");
                    session.SetPosition(displaced[0], original);
                    Assert(session.ReadAll()[0].Position == original, "Native position round-trip must restore the fixture icon.");
                    Command(fixture, "snap:on");
                }
                using (DesktopService service = new DesktopService(layout, delegate { return handles; }))
                {
                    bool gridRefused = false;
                    try { service.Sweep(new Point(350, 250)); } catch (InvalidOperationException) { gridRefused = true; }
                    Assert(service.AlignToGrid && gridRefused && !File.Exists(layout), "Align-to-grid must refuse sweep before any backup or movement.");
                    Command(fixture, "snap:off");
                    Command(fixture, "auto:on");
                    bool arrangeRefused = false;
                    try { service.Sweep(new Point(350, 250)); } catch (InvalidOperationException) { arrangeRefused = true; }
                    Assert(arrangeRefused && !File.Exists(layout), "Auto-arrange must refuse sweep before publishing any snapshot.");
                    Command(fixture, "auto:off");
                    List<IndexedIcon> original;
                    using (RemoteDesktop session = new RemoteDesktop(handles)) original = session.ReadAll();
                    service.Sweep(new Point(350, 250));
                    Assert(File.Exists(layout), "Native sweep must publish a durable original layout.");
                    byte[] saved = File.ReadAllBytes(layout);
                    Command(fixture, "new");
                    Point newPosition = FindPosition(handles, NewName, 0);
                    service.Sweep(new Point(390, 290));
                    Assert(Convert.ToBase64String(saved) == Convert.ToBase64String(File.ReadAllBytes(layout)), "Repeated native sweep must preserve the first snapshot unchanged.");
                    Point actualNew = FindPosition(handles, NewName, 0);
                    Assert(actualNew == newPosition, "Icons absent from the original snapshot must remain in place on repeated sweep.");
                    Command(fixture, "reorder");
                    service.Restore();
                    Assert(!service.HasSavedLayout, "Complete fixture restore must retire the original snapshot.");
                    foreach (IndexedIcon icon in original)
                        Assert(FindPosition(handles, icon.Visual.Name, icon.Occurrence) == icon.Position, "Restore after native item reordering must use name+occurrence, not the original indices.");

                    service.Sweep(new Point(350, 250));
                    byte[] partial = File.ReadAllBytes(layout);
                    Command(fixture, "snap:on");
                    bool restoreGridRefused = false;
                    try { service.Restore(); } catch (InvalidOperationException) { restoreGridRefused = true; }
                    Assert(restoreGridRefused && service.HasSavedLayout, "Enabling grid must refuse restore and preserve recovery data.");
                    Command(fixture, "snap:off");
                    Command(fixture, "remove");
                    bool partialRefused = false;
                    try { service.Restore(); } catch (InvalidOperationException) { partialRefused = true; }
                    Assert(partialRefused && service.HasSavedLayout, "Missing fixture icons must retain the recovery snapshot.");
                    Assert(Convert.ToBase64String(partial) == Convert.ToBase64String(File.ReadAllBytes(layout)), "Partial restoration must preserve the complete original snapshot.");
                    Command(fixture, "return");
                    service.Restore();
                    Assert(!service.HasSavedLayout, "A later restore must complete after a missing fixture icon returns.");
                }
                Command(fixture, "close");
                Assert(fixture.WaitForExit(5000) && fixture.ExitCode == 0, "Native fixture must exit cleanly.");
            }
            finally
            {
                // Kill only the disposable child process started by this test if it stopped responding.
                if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(5000); }
            }
        }
    }

    private static Point FindPosition(DesktopHandles handles, string name, int occurrence)
    {
        using (RemoteDesktop session = new RemoteDesktop(handles))
            foreach (IndexedIcon item in session.ReadAll())
                if (item.Visual.Name == name && item.Occurrence == occurrence) return item.Position;
        throw new Exception("Fixture item not found.");
    }

    private static string ReadLine(Process process)
    {
        Task<string> read = Task.Factory.StartNew(delegate { return process.StandardOutput.ReadLine(); });
        if (!read.Wait(5000)) throw new Exception("Native fixture did not respond within five seconds.");
        if (read.Result == null) throw new Exception("Native fixture exited before responding.");
        return read.Result;
    }

    private static void Command(Process process, string command)
    {
        process.StandardInput.WriteLine(command); process.StandardInput.Flush();
        Assert(ReadLine(process) == "OK " + command, "Native fixture command must complete: " + command);
    }

    private static void AddItem(IntPtr list, string name, Point position)
    {
        IntPtr text = Marshal.StringToHGlobalUni(name);
        IntPtr itemBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.LvItem)));
        IntPtr pointBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Point)));
        try
        {
            int index = FixtureSend(list, 0x1000 + 4, IntPtr.Zero, IntPtr.Zero).ToInt32();
            Native.LvItem item = new Native.LvItem { Mask = 3, Item = index, Text = text, TextMax = name.Length + 1, Image = 0 };
            Marshal.StructureToPtr(item, itemBuffer, false);
            if (FixtureSend(list, 0x1000 + 77, IntPtr.Zero, itemBuffer).ToInt32() != index) throw new Exception("Native fixture insert failed.");
            Marshal.StructureToPtr(new Native.Point(position.X, position.Y), pointBuffer, false);
            FixtureSend(list, 0x1000 + 49, new IntPtr(index), pointBuffer);
        }
        finally { Marshal.FreeHGlobal(text); Marshal.FreeHGlobal(itemBuffer); Marshal.FreeHGlobal(pointBuffer); }
    }

    private static IntPtr FixtureSend(IntPtr list, uint message, IntPtr wp, IntPtr lp)
    { UIntPtr result; Native.SendMessageTimeout(list, message, wp, lp, 0x0002, 1000, out result); return new IntPtr((long)result.ToUInt64()); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extended, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    private static int RunFixture()
    {
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
        using (Forms.Form form = new Forms.Form())
        using (Forms.ImageList images = new Forms.ImageList())
        using (Bitmap image = new Bitmap(32, 32))
        {
            form.ShowInTaskbar = false; form.Opacity = 0; form.StartPosition = Forms.FormStartPosition.Manual;
            form.Bounds = new Rectangle(80, 80, 700, 500);
            images.ImageSize = new Size(32, 32);
            images.Images.Add(image);
            // Create a raw native control: WinForms ListView adds its own item-arrangement behavior.
            // Only the invisible fixture's message loop and image list are supplied by WinForms.
            using (Forms.ListView initializeClass = new Forms.ListView()) { IntPtr ignored = initializeClass.Handle; }
            IntPtr list = IntPtr.Zero;
            form.Load += delegate
            {
                list = CreateWindowEx(0, "SysListView32", "NativeFixture", 0x50002000, 0, 0, 680, 440, form.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (list == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                FixtureSend(list, 0x1000 + 3, IntPtr.Zero, images.Handle);
                AddItem(list, DuplicateName, new Point(40, 35));
                AddItem(list, UniqueName, new Point(170, 80));
                AddItem(list, DuplicateName, new Point(290, 140));
                Console.WriteLine("READY|" + form.Handle.ToInt64() + "|" + list.ToInt64()); Console.Out.Flush();
                Thread commands = new Thread(delegate()
                {
                    string line;
                    while ((line = Console.ReadLine()) != null)
                    {
                        string current = line;
                        form.BeginInvoke(new Action(delegate
                        {
                            if (current == "auto:on") Native.SetStyle(list, -16, Native.Style(list) | 0x0100L);
                            else if (current == "auto:off") Native.SetStyle(list, -16, Native.Style(list) & ~0x0100L);
                            else if (current == "snap:on")
                            {
                                UIntPtr result;
                                Native.SendMessageTimeout(list, 0x1000 + 54, new IntPtr(0x00080000), new IntPtr(0x00080000), 0x0002, 1000, out result);
                            }
                            else if (current == "snap:off")
                            {
                                UIntPtr result;
                                Native.SendMessageTimeout(list, 0x1000 + 54, new IntPtr(0x00080000), IntPtr.Zero, 0x0002, 1000, out result);
                            }
                            else if (current == "new") AddItem(list, NewName, new Point(430, 240));
                            else if (current == "reorder")
                            {
                                FixtureSend(list, 0x1000 + 9, IntPtr.Zero, IntPtr.Zero);
                                AddItem(list, UniqueName, new Point(20, 25));
                                AddItem(list, DuplicateName, new Point(150, 55));
                                AddItem(list, DuplicateName, new Point(300, 105));
                                AddItem(list, NewName, new Point(450, 255));
                            }
                            else if (current == "remove")
                            {
                                FixtureSend(list, 0x1000 + 8, IntPtr.Zero, IntPtr.Zero);
                            }
                            else if (current == "return") AddItem(list, UniqueName, new Point(500, 350));
                            Console.WriteLine("OK " + current); Console.Out.Flush();
                            if (current == "close") form.Close();
                        }));
                        if (current == "close") break;
                    }
                });
                commands.IsBackground = true; commands.Start();
            };
            Forms.Application.Run(form);
        }
        return 0;
    }
}

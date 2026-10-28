// Compile with src/StartupService.cs. This fixture never opens or writes a real registry key.
using System;
using System.Collections.Generic;
using System.IO;
using RugWindows;

internal static class StartupTests
{
    static int checks;
    static void Assert(bool value, string message) { checks++; if (!value) throw new Exception(message); }

    static void Refuses(Action action, string message)
    {
        bool refused = false;
        try { action(); }
        catch (Exception error)
        {
            if (!(error is ArgumentException) && !(error is IOException) && !(error is NotSupportedException)) throw;
            refused = true;
        }
        Assert(refused, message);
    }

    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Pass a writable test directory."); return 2; }
        string parent = Path.GetFullPath(args[0]);
        string root = Path.Combine(parent, "startup-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            QuotingAndValidation();
            RegistrationLifecycle(root);
            WriteFailures(root);
            Console.WriteLine("Startup safety checks passed: " + checks + ". The user's startup registry was not changed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!String.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("startup-safety-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe startup fixture path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    static void QuotingAndValidation()
    {
        Assert(StartupService.BuildCommand(@"C:\Program Files\Ковер\RugWindows.exe") == "\"C:\\Program Files\\Ковер\\RugWindows.exe\" --startup", "Unicode and spaces must be quoted as one executable argument.");
        Assert(StartupService.BuildCommand(@"C:\apps\folder\..\RugWindows.EXE") == "\"C:\\apps\\RugWindows.EXE\" --startup", "The registered command must normalize absolute paths.");
        Assert(StartupService.BuildCommand(@"\\server\share\Ковер\RugWindows.exe") == "\"\\\\server\\share\\Ковер\\RugWindows.exe\" --startup", "Fully qualified UNC paths must preserve quoting.");
        var invalidPaths = new[] { null, "", " ", "RugWindows.exe", @"C:RugWindows.exe", @"\RugWindows.exe", @"C:\apps\RugWindows.cmd", "C:\\apps\\rug\".exe", "C:\\apps\\rug\n.exe", "C:\\apps\\rug\r.exe", "C:\\apps\\rug\0.exe" };
        foreach (string path in invalidPaths) Refuses(delegate { StartupService.BuildCommand(path); }, "Unsafe/ambiguous paths must not become startup commands.");
        string oversized = @"C:\" + new string('a', 246) + ".exe";
        Refuses(delegate { StartupService.BuildCommand(oversized); }, "A Run command longer than 260 characters must be refused.");
        string boundary = @"C:\" + new string('a', 241) + ".exe";
        Assert(StartupService.BuildCommand(boundary).Length == 260, "A Run command at the documented 260-character boundary is allowed.");
    }

    static void RegistrationLifecycle(string root)
    {
        string folder = Path.Combine(root, "Папка с пробелами"); Directory.CreateDirectory(folder);
        string executable = Path.Combine(folder, "RugWindows.exe"); File.WriteAllBytes(executable, new byte[] { 77, 90 });
        var store = new FakeStore(); store.Values["Another app"] = "leave this startup untouched";
        var startup = new StartupService(executable, store);
        Assert(!startup.Enabled && startup.RegisteredCommand == "", "No entry means disabled, without synthesizing a command.");
        startup.SetEnabled(true);
        string command = StartupService.BuildCommand(executable);
        Assert(startup.Enabled && startup.RegisteredCommand == command, "Enabling must register the exact quoted EXE and --startup argument.");
        Assert(store.Writes == 1 && store.Removes == 0, "Enabling writes one app-owned value.");
        startup.SetEnabled(true);
        Assert(store.Writes == 1, "Enabling an identical command is idempotent.");
        Assert(store.Values["Another app"] == "leave this startup untouched", "Enabling must not alter another application's startup.");

        string moved = Path.Combine(root, "Moved RugWindows.exe"); File.Copy(executable, moved);
        var movedStartup = new StartupService(moved, store);
        Assert(movedStartup.Enabled, "An existing app-owned startup entry remains visible after moving the executable.");
        movedStartup.SetEnabled(true);
        Assert(store.Writes == 2 && movedStartup.RegisteredCommand == StartupService.BuildCommand(moved), "Enabling a moved copy updates the registered target to the current location.");
        File.Delete(moved);
        Assert(movedStartup.Enabled, "Querying startup does not require the target to exist.");
        movedStartup.SetEnabled(false);
        Assert(!movedStartup.Enabled && movedStartup.RegisteredCommand == "", "Disabling remains possible after removing the executable.");
        Assert(store.Values.Count == 1 && store.Values["Another app"] == "leave this startup untouched", "Disabling deletes only the app-owned value.");
        movedStartup.SetEnabled(false);
        Assert(!movedStartup.Enabled && store.Values.Count == 1, "Disabling a missing value is harmless and preserves unrelated entries.");

        var missing = new StartupService(Path.Combine(root, "missing.exe"), store);
        int writes = store.Writes, removes = store.Removes;
        Refuses(delegate { missing.SetEnabled(true); }, "An absent EXE must be refused before changing startup.");
        Assert(store.Writes == writes && store.Removes == removes, "A missing target must not mutate the startup store.");
        var invalid = new StartupService("bad\".exe", store);
        Refuses(delegate { invalid.SetEnabled(true); }, "A quoted or nonabsolute target must fail before writing.");
        Assert(store.Writes == writes && store.Removes == removes, "Invalid command data must not mutate the startup store.");
        Assert(StartupService.RunSubKey == @"Software\Microsoft\Windows\CurrentVersion\Run" && StartupService.ValueName == "UnderTheRugWindows", "Production uses the current-user Run entry owned by this application.");
    }

    static void WriteFailures(string root)
    {
        string path = Path.Combine(root, "failure.exe"); File.WriteAllBytes(path, new byte[] { 77, 90 });
        var writeFailure = new FakeStore { IgnoreWrites = true };
        Refuses(delegate { new StartupService(path, writeFailure).SetEnabled(true); }, "A registry write that is not retained must not report success.");
        var removeFailure = new FakeStore { IgnoreRemoves = true }; removeFailure.Values[StartupService.ValueName] = StartupService.BuildCommand(path);
        Refuses(delegate { new StartupService(path, removeFailure).SetEnabled(false); }, "A removal that is not retained must not report success.");
    }

    sealed class FakeStore : IStartupStore
    {
        internal readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        internal int Writes, Removes;
        internal bool IgnoreWrites, IgnoreRemoves;
        public string Read() { string value; return Values.TryGetValue(StartupService.ValueName, out value) ? value : null; }
        public void Write(string command) { Writes++; if (!IgnoreWrites) Values[StartupService.ValueName] = command; }
        public void Remove() { Removes++; if (!IgnoreRemoves) Values.Remove(StartupService.ValueName); }
    }
}

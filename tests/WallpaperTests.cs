// Compile with src/State.cs and src/Wallpaper.cs; this fixture never creates a real native backend.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using RugWindows;

internal static class WallpaperTests
{
    static int checks;
    static string root;
    static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Refuses(Action action, string message)
    {
        bool refused = false;
        try { action(); } catch (Exception ex) { if (!(ex is InvalidDataException) && !(ex is InvalidOperationException) && !(ex is IOException)) throw; refused = true; }
        Assert(refused, message);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Pass a writable test directory."); return 2; }
        string parent = Path.GetFullPath(args[0]);
        root = Path.Combine(parent, "wallpaper-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            InvalidImagesDoNotMutate();
            FormatBackupAndRestart();
            FailedApplyAndRestore();
            LegacyMigrationIsNarrow();
            SeparateShellCacheAndBackupMigration();
            MissingBackupRefusesRestore();
            Assert(WindowsWallpaperBackend.IsComUnavailable(new COMException("Missing class", unchecked((int)0x80040154))), "Class-not-registered may use the BMP fallback.");
            Assert(!WindowsWallpaperBackend.IsComUnavailable(new COMException("Policy denied", unchecked((int)0x80070005))), "Access/policy refusal must never select SPI fallback.");
            Assert(!WindowsWallpaperBackend.IsComUnavailable(new COMException("Unexpected failure", unchecked((int)0x80004005))), "Unknown COM failures must never select SPI fallback.");
            Console.WriteLine("Wallpaper safety checks passed: " + checks + ". Native wallpaper and user settings were not changed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            // Only delete the unique fixture folder directly below the supplied workspace test directory.
            string resolved = Path.GetFullPath(root);
            if (!String.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("wallpaper-safety-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fixture path.");
            Directory.Delete(resolved, true);
        }
    }

    static string Folder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    static string ImageFile(string directory, string name, Color color, ImageFormat format)
    {
        string path = Path.Combine(directory, name);
        using (Bitmap image = new Bitmap(47, 29, PixelFormat.Format32bppArgb))
        { using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(color); image.Save(path, format); }
        return path;
    }
    static void CheckBmp(string path)
    {
        using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
        {
            Assert(reader.ReadUInt16() == 0x4d42, "Native floor must contain an actual BMP header.");
            reader.BaseStream.Position = 28;
            Assert(reader.ReadUInt16() == 24, "Native floor must have 24-bit opaque RGB pixels.");
            Assert(reader.ReadUInt32() == 0, "Native floor must use uncompressed BI_RGB.");
        }
        using (Bitmap bitmap = new Bitmap(path)) { Assert(bitmap.Width == 47 && bitmap.Height == 29, "Normalization must preserve image dimensions."); }
    }
    static Action Save(Preferences preferences, string folder)
    {
        return delegate { using (FileStream file = File.Create(Path.Combine(folder, "settings.json"))) new DataContractJsonSerializer(typeof(Preferences)).WriteObject(file, preferences); };
    }
    static Preferences Reload(string folder)
    { using (FileStream file = File.OpenRead(Path.Combine(folder, "settings.json"))) return (Preferences)new DataContractJsonSerializer(typeof(Preferences)).ReadObject(file); }

    static void InvalidImagesDoNotMutate()
    {
        string folder = Folder("invalid");
        Preferences prefs = new Preferences(); FakeBackend backend = new FakeBackend("", "0", "0"); int saves = 0;
        WallpaperService service = new WallpaperService(prefs, backend, folder, delegate { saves++; });
        string invalid = Path.Combine(folder, "invalid.jpg"); File.WriteAllText(invalid, "not an image");
        string empty = Path.Combine(folder, "empty.jpg"); File.WriteAllBytes(empty, new byte[0]);
        Refuses(delegate { service.Apply(invalid); }, "Corrupt JPEG must fail.");
        Refuses(delegate { service.Apply(empty); }, "Empty JPEG must fail.");
        Refuses(delegate { service.Apply(Path.Combine(folder, "missing.jpg")); }, "Missing image must fail.");
        Assert(backend.Reads == 0 && backend.Sets.Count == 0 && saves == 0, "Invalid image must fail before any native reads, mutations, or settings save.");
        Assert(!prefs.WallpaperSaved && prefs.PreviousWallpaper == null, "Invalid image must not invent an original snapshot.");
        string floor = ImageFile(folder, "valid floor.jpg", Color.SaddleBrown, ImageFormat.Jpeg);
        WallpaperService failingSave = new WallpaperService(prefs, backend, folder, delegate { throw new IOException("Settings disk failure"); });
        Refuses(delegate { failingSave.Apply(floor); }, "A failed durable settings save must abort apply.");
        Assert(backend.Sets.Count == 0 && !prefs.WallpaperSaved, "Settings failure must occur before native mutation and reset tentative snapshot fields.");
    }

    static void FormatBackupAndRestart()
    {
        string folder = Folder("switches");
        string original = ImageFile(folder, "original Кириллица.jpg", Color.DarkRed, ImageFormat.Jpeg);
        string floor = ImageFile(folder, "floor α.jpg", Color.SaddleBrown, ImageFormat.Jpeg);
        string second = ImageFile(folder, "transparent floor.png", Color.Transparent, ImageFormat.Png);
        Preferences prefs = new Preferences(); FakeBackend backend = new FakeBackend(original, "6", "0");
        backend.BeforeSet = delegate { Assert(File.Exists(Path.Combine(folder, "settings.json")) && prefs.WallpaperSaved && File.Exists(prefs.PreviousWallpaperBackup), "Initial snapshot and physical backup must exist before the first native mutation."); };
        WallpaperService service = new WallpaperService(prefs, backend, folder, Save(prefs, folder));
        service.Apply(floor); string firstTarget = backend.Current.Path, backup = prefs.PreviousWallpaperBackup;
        CheckBmp(firstTarget); CheckBmp(backup);
        Assert(prefs.PreviousWallpaper == original && prefs.PreviousStyle == "6", "Initial image and display style must be preserved.");
        string firstHash = WallpaperImages.Hash(firstTarget), backupHash = WallpaperImages.Hash(backup);
        service.Apply(second);
        Assert(backend.Current.Path != firstTarget && File.Exists(firstTarget) && WallpaperImages.Hash(firstTarget) == firstHash, "Selecting another floor must publish a separate file and leave the active previous file intact.");
        Assert(prefs.PreviousWallpaper == original && prefs.PreviousWallpaperBackup == backup && WallpaperImages.Hash(backup) == backupHash, "Repeated selections must retain the very first original and its physical backup.");
        using (Bitmap image = new Bitmap(backend.Current.Path)) { Assert(image.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "Transparent source must normalize to an opaque background."); }
        service.Apply(second);
        Assert(Directory.GetFiles(Path.Combine(folder, "floors"), "*.bmp").Length == 2, "Identical images should reuse an immutable content-addressed BMP.");
        Assert(Directory.GetFiles(Path.Combine(folder, "floors"), "*.tmp").Length == 0, "Normalization must not leave temporary files.");
        Preferences restarted = Reload(folder);
        backend.BeforeSet = null;
        new WallpaperService(restarted, backend, folder, Save(restarted, folder)).Restore();
        Assert(backend.Current.Path == original && backend.Current.Style == "6" && backend.Current.Tile == "0", "Restore after process restart must select the valid original path and original display mode.");
        Assert(!restarted.WallpaperSaved && File.Exists(backup), "Successful restore clears the snapshot only after native success and retains the physical image file.");
        Preferences changedPrefs = new Preferences(); FakeBackend changedBackend = new FakeBackend(original, "2", "0");
        WallpaperService changedService = new WallpaperService(changedPrefs, changedBackend, folder, Save(changedPrefs, folder));
        changedService.Apply(floor); string changedBackup = changedPrefs.PreviousWallpaperBackup;
        ImageFile(folder, Path.GetFileName(original), Color.Blue, ImageFormat.Jpeg);
        changedService.Restore();
        Assert(changedBackend.Current.Path == changedBackup, "An overwritten original path must restore the captured pixels from the physical backup.");
    }

    static void FailedApplyAndRestore()
    {
        string folder = Folder("recovery");
        string original = ImageFile(folder, "original.jpg", Color.DarkGreen, ImageFormat.Jpeg);
        string floor = ImageFile(folder, "floor.jpg", Color.BurlyWood, ImageFormat.Jpeg);
        Preferences prefs = new Preferences(); FakeBackend backend = new FakeBackend(original, "0", "1");
        WallpaperService service = new WallpaperService(prefs, backend, folder, Save(prefs, folder));
        backend.FailAfterChange = 1;
        Refuses(delegate { service.Apply(floor); }, "Partial native apply failure must be reported.");
        Assert(backend.Current.Path == original && prefs.WallpaperSaved && File.Exists(prefs.PreviousWallpaperBackup), "Apply failure must attempt rollback and keep the durable original snapshot.");
        string originalBackup = prefs.PreviousWallpaperBackup;
        service.Apply(floor); string selected = backend.Current.Path;
        backend.FailAfterChange = 1;
        Refuses(delegate { service.Restore(); }, "Partial native restore failure must be reported.");
        Assert(backend.Current.Path == selected && prefs.WallpaperSaved && prefs.PreviousWallpaperBackup == originalBackup, "Restore failure must roll back to the selected floor and preserve the initial backup.");
        File.Delete(original);
        service.Restore();
        Assert(backend.Current.Path == originalBackup && backend.Current.Style == "0" && backend.Current.Tile == "1", "Missing original must restore the durable image and its tile/center settings.");
        Assert(!prefs.WallpaperSaved, "A later successful retry may clear the original snapshot.");
    }

    static void LegacyMigrationIsNarrow()
    {
        string folder = Folder("migration"), floorFolder = Path.Combine(folder, "floors"); Directory.CreateDirectory(floorFolder);
        string original = ImageFile(folder, "original.jpg", Color.DarkSlateBlue, ImageFormat.Jpeg);
        string legacy = ImageFile(floorFolder, "3 oak-planks.jpg", Color.Tan, ImageFormat.Jpeg);
        Preferences prefs = new Preferences { WallpaperSaved = true, PreviousWallpaper = original, PreviousStyle = "6", PreviousTile = "0" };
        FakeBackend backend = new FakeBackend(legacy, "10", "0");
        WallpaperService service = new WallpaperService(prefs, backend, folder, Save(prefs, folder));
        Assert(service.RepairCurrentFloor(), "An active legacy app-owned JPEG must migrate automatically.");
        Assert(prefs.PreviousWallpaper == original && File.Exists(prefs.PreviousWallpaperBackup), "Legacy migration must back up the initial original, never the current floor.");
        CheckBmp(backend.Current.Path);
        Assert(!service.RepairCurrentFloor() && backend.Sets.Count == 1, "An already repaired BMP must not be reapplied on every launch.");
        backend.Current.Path = original;
        Assert(!service.RepairCurrentFloor() && backend.Sets.Count == 1, "Unrelated user wallpaper must not be changed on launch.");
        string nested = Path.Combine(floorFolder, "unrelated"); Directory.CreateDirectory(nested);
        backend.Current.Path = ImageFile(nested, "personal.jpg", Color.Blue, ImageFormat.Jpeg);
        Assert(!service.RepairCurrentFloor() && backend.Sets.Count == 1, "Files outside the direct legacy floor directory must not migrate.");
        Preferences noSnapshot = new Preferences(); backend.Current.Path = legacy;
        Assert(!new WallpaperService(noSnapshot, backend, folder, delegate { }).RepairCurrentFloor(), "Migration must not invent an original snapshot from an already selected floor.");
        backend.Current.Path = ""; backend.Current.LegacyPath = legacy;
        Assert(service.RepairCurrentFloor(), "Regression: COM can report no visible image while SPI still names the app-owned JPEG; this black-background state must migrate.");
        CheckBmp(backend.Current.Path);
        int migratedCalls = backend.Sets.Count;
        backend.Current.Path = ""; backend.Current.LegacyPath = original;
        Assert(!service.RepairCurrentFloor() && backend.Sets.Count == migratedCalls, "COM-empty state with an unrelated legacy path must not change the user's wallpaper.");
        backend.Current.Path = original; backend.Current.LegacyPath = legacy;
        Assert(!service.RepairCurrentFloor() && backend.Sets.Count == migratedCalls, "A visible unrelated COM wallpaper must not be replaced by a stale legacy floor path.");
    }

    static void MissingBackupRefusesRestore()
    {
        string folder = Folder("lost-original");
        Preferences prefs = new Preferences { WallpaperSaved = true, PreviousWallpaper = Path.Combine(folder, "missing.jpg"), PreviousWallpaperBackup = Path.Combine(folder, "bad.bmp") };
        File.WriteAllText(prefs.PreviousWallpaperBackup, "invalid");
        FakeBackend backend = new FakeBackend("", "10", "0");
        WallpaperService service = new WallpaperService(prefs, backend, folder, delegate { throw new Exception("Unexpected save"); });
        Refuses(delegate { service.Restore(); }, "Restore must refuse invalid original and invalid backup.");
        Assert(prefs.WallpaperSaved && backend.Sets.Count == 0 && backend.Reads == 0, "Failed restore validation must preserve the snapshot and avoid all native changes.");
    }

    static void SeparateShellCacheAndBackupMigration()
    {
        string folder = Folder("separate-cache"), local = Path.Combine(folder, "local"), published = Path.Combine(folder, "themes");
        Directory.CreateDirectory(local); Directory.CreateDirectory(Path.Combine(local, "floors"));
        string original = ImageFile(local, "original.jpg", Color.Crimson, ImageFormat.Jpeg);
        string legacy = ImageFile(Path.Combine(local, "floors"), "legacy.jpg", Color.Tan, ImageFormat.Jpeg);
        string oldBackup = WallpaperImages.Normalize(original, Path.Combine(local, "old-backups"), "backup-");
        string originalHash = WallpaperImages.Hash(original), backupHash = WallpaperImages.Hash(oldBackup);
        Preferences prefs = new Preferences { WallpaperSaved = true, PreviousWallpaper = original, PreviousWallpaperBackup = oldBackup,
            PreviousWallpaperHash = originalHash, PreviousStyle = "2", PreviousTile = "0" };
        FakeBackend backend = new FakeBackend("", "10", "0"); backend.Current.LegacyPath = legacy;
        WallpaperService service = new WallpaperService(prefs, backend, local, Save(prefs, folder), published);
        Assert(service.RepairCurrentFloor(), "A broken legacy JPEG must migrate from the local settings directory to the shell-readable theme cache.");
        Assert(WallpaperImages.SamePath(Path.GetDirectoryName(backend.Current.Path), Path.Combine(published, "floors")), "Published floors must use the separate shell-readable directory.");
        Assert(WallpaperImages.SamePath(Path.GetDirectoryName(prefs.PreviousWallpaperBackup), Path.Combine(published, "wallpaper-backups")), "An existing local backup must also migrate to the theme cache.");
        Assert(prefs.PreviousWallpaper == original && prefs.PreviousWallpaperHash == originalHash && prefs.PreviousStyle == "2" && prefs.PreviousTile == "0", "Backup migration must retain the initial path, hash and display snapshot.");
        Assert(File.Exists(oldBackup) && WallpaperImages.Hash(oldBackup) == backupHash && WallpaperImages.Hash(prefs.PreviousWallpaperBackup) == backupHash, "Backup migration must preserve the captured pixels and leave its source intact.");
        File.Delete(original); string migratedBackup = prefs.PreviousWallpaperBackup;
        service.Restore();
        Assert(backend.Current.Path == migratedBackup, "A migrated physical backup must restore even after the original image disappears.");
        CheckBmp(backend.Current.Path);
        Preferences directPrefs = new Preferences { WallpaperSaved = true, PreviousWallpaper = original, PreviousWallpaperBackup = oldBackup,
            PreviousWallpaperHash = originalHash, PreviousStyle = "6", PreviousTile = "0" };
        FakeBackend directBackend = new FakeBackend("", "10", "0");
        string directPublished = Path.Combine(folder, "direct-restore-themes");
        WallpaperService directService = new WallpaperService(directPrefs, directBackend, local, Save(directPrefs, folder), directPublished);
        directService.Restore();
        Assert(WallpaperImages.SamePath(Path.GetDirectoryName(directBackend.Current.Path), Path.Combine(directPublished, "wallpaper-backups")), "Direct restore before any new apply must publish a local backup to the shell-readable cache.");
        Assert(!directPrefs.WallpaperSaved && directBackend.Current.Style == "6", "Direct restore from migrated backup must complete normally.");
    }

    sealed class FakeBackend : IWallpaperBackend
    {
        internal WallpaperState Current;
        internal readonly List<WallpaperState> Sets = new List<WallpaperState>();
        internal int Reads, FailAfterChange;
        internal Action BeforeSet;
        internal FakeBackend(string path, string style, string tile) { Current = new WallpaperState { Path = path, Style = style, Tile = tile }; }
        public WallpaperState Read() { Reads++; return new WallpaperState { Path = Current.Path, LegacyPath = Current.LegacyPath, Style = Current.Style, Tile = Current.Tile }; }
        public void Set(string path, string style, string tile)
        {
            if (BeforeSet != null) BeforeSet();
            Current = new WallpaperState { Path = path, Style = style, Tile = tile }; Sets.Add(Current);
            if (FailAfterChange > 0) { FailAfterChange--; throw new InvalidOperationException("Simulated native failure after applying wallpaper."); }
        }
    }
}

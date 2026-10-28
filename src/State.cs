using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RugWindows
{
    [DataContract]
    public sealed class Preferences
    {
        [DataMember] public string Texture;
        [DataMember] public double Scale = .46;
        [DataMember] public double CenterX = .5;
        [DataMember] public double CenterY = .5;
        [DataMember] public double Angle;
        [DataMember] public string Monitor;
        [DataMember] public string PreviousWallpaper;
        [DataMember] public string PreviousStyle;
        [DataMember] public string PreviousTile;
        [DataMember] public string PreviousWallpaperBackup;
        [DataMember] public string PreviousWallpaperHash;
        [DataMember] public bool WallpaperSaved;

        public static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnderTheRugWindows");
        public static readonly string FileName = Path.Combine(DataDirectory, "settings.json");
        public static Preferences Load()
        {
            try
            {
                using (var stream = File.OpenRead(FileName))
                {
                    var p = (Preferences)new DataContractJsonSerializer(typeof(Preferences)).ReadObject(stream);
                    if (!Finite(p.Scale) || p.Scale < .25 || p.Scale > .75) p.Scale = .46;
                    if (!Finite(p.CenterX) || !Finite(p.CenterY)) { p.CenterX = .5; p.CenterY = .5; }
                    p.CenterX = Math.Max(0, Math.Min(1, p.CenterX)); p.CenterY = Math.Max(0, Math.Min(1, p.CenterY));
                    if (!Finite(p.Angle)) p.Angle = 0;
                    return p;
                }
            }
            catch (Exception e) { if (!(e is FileNotFoundException) && !(e is DirectoryNotFoundException)) Log.Write("Settings: " + e.Message); return new Preferences(); }
        }
        static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
        public void Save()
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = FileName + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(Preferences)).WriteObject(stream, this); stream.Flush(true); }
            if (File.Exists(FileName)) File.Replace(tmp, FileName, null); else File.Move(tmp, FileName);
        }
    }

    public static class Log
    {
        public static void Write(string message)
        {
            try { Directory.CreateDirectory(Preferences.DataDirectory); File.AppendAllText(Path.Combine(Preferences.DataDirectory, "rug.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { }
        }
    }

}

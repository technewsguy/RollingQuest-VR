// Steam's list of non-Steam games: Steam\userdata\<user>\config\shortcuts.vdf (binary KeyValues). Every entry we don't
// touch is kept byte-for-byte. Steam reads the file at startup and rewrites it on exit, so it is only edited while
// Steam is closed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

static class SteamShortcuts
{
    internal class Node
    {
        public byte Type;          // 0 = map, 1 = string, 2 = int32, other = raw bytes kept as-is
        public string Name;
        public string Str;
        public int Int;
        public byte[] Raw;
        public List<Node> Kids = new List<Node>();
        public Node Find(string n) { foreach (var k in Kids) if (string.Equals(k.Name, n, StringComparison.OrdinalIgnoreCase)) return k; return null; }
        public string S(string n) { var k = Find(n); return k == null ? null : k.Str; }
    }

    public static string SteamPath()
    {
        try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; return v == null ? null : v.Replace('/', '\\'); }
        catch { return null; }
    }

    // The signed-in (or most recently used) Steam account's config folder, or null.
    public static string UserConfigDir()
    {
        string steam = SteamPath();
        if (steam == null || !Directory.Exists(Path.Combine(steam, "userdata"))) return null;
        try
        {
            object u = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            if (u is int && (int)u != 0)
            {
                string d = Path.Combine(steam, @"userdata\" + unchecked((uint)(int)u) + @"\config");
                if (Directory.Exists(d)) return d;
            }
        }
        catch { }
        string best = null; DateTime bestTime = DateTime.MinValue;
        foreach (var u in Directory.GetDirectories(Path.Combine(steam, "userdata")))
        {
            uint id;
            if (!uint.TryParse(Path.GetFileName(u), out id) || id == 0) continue;
            string cfg = Path.Combine(u, "config");
            if (!Directory.Exists(cfg)) continue;
            DateTime t = Directory.GetLastWriteTimeUtc(cfg);
            if (t > bestTime) { best = cfg; bestTime = t; }
        }
        return best;
    }

    // All users' shortcut files (for finding the game and for cleanup).
    public static List<string> AllShortcutFiles()
    {
        var list = new List<string>();
        string steam = SteamPath();
        if (steam == null || !Directory.Exists(Path.Combine(steam, "userdata"))) return list;
        foreach (var u in Directory.GetDirectories(Path.Combine(steam, "userdata")))
        {
            string f = Path.Combine(u, @"config\shortcuts.vdf");
            if (File.Exists(f)) list.Add(f);
        }
        return list;
    }

    // ------------------------------------------------------------ file

    public static List<Node> Load(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return new List<Node>();
        using (var r = new BinaryReader(File.OpenRead(path))) return ReadMap(r);
    }

    public static void Save(string path, List<Node> root, string backupSuffix)
    {
        if (File.Exists(path) && !File.Exists(path + backupSuffix)) File.Copy(path, path + backupSuffix);
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) WriteMap(w, root);
        File.WriteAllBytes(path + ".tmp", ms.ToArray());
        File.Copy(path + ".tmp", path, true);
        File.Delete(path + ".tmp");
    }

    public static Node List(List<Node> root)
    {
        if (root.Count == 0) root.Add(new Node { Type = 0, Name = "shortcuts" });
        return root[0];
    }

    public static IEnumerable<Node> Entries(List<Node> root)
    {
        if (root.Count == 0) yield break;
        foreach (var e in root[0].Kids) if (e.Type == 0) yield return e;
    }

    public static Node FindByName(List<Node> root, string name)
    {
        foreach (var e in Entries(root)) if (string.Equals(e.S("AppName") ?? e.S("appname"), name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    public static uint AppId(Node e)
    {
        var id = e.Find("appid");
        return id != null && id.Type == 2 ? unchecked((uint)id.Int) : 0;
    }

    // Adds or updates the entry called name. Returns its 32-bit app id (artwork file names in config\grid use it).
    public static uint Upsert(List<Node> root, string name, string exe, string startDir, string icon, string launchOptions, bool openVR)
    {
        var list = List(root);
        string qexe = "\"" + exe + "\"", qstart = "\"" + startDir + "\"";
        var entry = FindByName(root, name);
        uint appid = Crc32(Encoding.UTF8.GetBytes(qexe + name)) | 0x80000000;
        if (entry == null)
        {
            entry = new Node { Type = 0, Name = list.Kids.Count.ToString() };
            list.Kids.Add(entry);
            Set(entry, 2, "appid", null, unchecked((int)appid));
        }
        else if (AppId(entry) != 0) appid = AppId(entry);   // keep the id: artwork, controller settings and playtime use it
        else Set(entry, 2, "appid", null, unchecked((int)appid));
        Set(entry, 1, "AppName", name, 0);
        Set(entry, 1, "Exe", qexe, 0);
        Set(entry, 1, "StartDir", qstart, 0);
        Set(entry, 1, "icon", icon, 0);
        if (entry.Find("ShortcutPath") == null) Set(entry, 1, "ShortcutPath", "", 0);
        Set(entry, 1, "LaunchOptions", launchOptions ?? "", 0);
        if (entry.Find("IsHidden") == null) Set(entry, 2, "IsHidden", null, 0);
        if (entry.Find("AllowDesktopConfig") == null) Set(entry, 2, "AllowDesktopConfig", null, 1);
        if (entry.Find("AllowOverlay") == null) Set(entry, 2, "AllowOverlay", null, 1);
        Set(entry, 2, "OpenVR", null, openVR ? 1 : 0);
        if (entry.Find("Devkit") == null) Set(entry, 2, "Devkit", null, 0);
        if (entry.Find("DevkitGameID") == null) Set(entry, 1, "DevkitGameID", "", 0);
        if (entry.Find("DevkitOverrideAppID") == null) Set(entry, 2, "DevkitOverrideAppID", null, 0);
        if (entry.Find("LastPlayTime") == null) Set(entry, 2, "LastPlayTime", null, 0);
        if (entry.Find("tags") == null) entry.Kids.Add(new Node { Type = 0, Name = "tags" });
        return appid;
    }

    // Removes the entry called name (the remaining entries are renumbered 0, 1, 2...). Returns its app id, or 0.
    public static uint Remove(List<Node> root, string name)
    {
        var e = FindByName(root, name);
        if (e == null) return 0;
        uint id = AppId(e);
        var list = root[0];
        list.Kids.Remove(e);
        int i = 0;
        foreach (var k in list.Kids) if (k.Type == 0) k.Name = (i++).ToString();
        return id;
    }

    static void Set(Node e, byte type, string name, string s, int i)
    {
        var n = e.Find(name);
        if (n == null) { n = new Node { Type = type, Name = name }; e.Kids.Add(n); }
        n.Type = type; n.Str = s; n.Int = i;
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    static string ReadZ(BinaryReader r)
    {
        var b = new List<byte>();
        for (byte c; (c = r.ReadByte()) != 0;) b.Add(c);
        return Encoding.UTF8.GetString(b.ToArray());
    }

    static void WriteZ(BinaryWriter w, string s) { w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0); }

    static List<Node> ReadMap(BinaryReader r)
    {
        var list = new List<Node>();
        while (true)
        {
            byte t = r.ReadByte();
            if (t == 8) return list;
            var n = new Node { Type = t, Name = ReadZ(r) };
            switch (t)
            {
                case 0: n.Kids = ReadMap(r); break;
                case 1: n.Str = ReadZ(r); break;
                case 2: n.Int = r.ReadInt32(); break;
                case 3: n.Raw = r.ReadBytes(4); break;   // float
                case 7: n.Raw = r.ReadBytes(8); break;   // uint64
                default: throw new InvalidDataException("unknown VDF type " + t);
            }
            list.Add(n);
        }
    }

    static void WriteMap(BinaryWriter w, List<Node> list)
    {
        foreach (var n in list)
        {
            w.Write(n.Type);
            WriteZ(w, n.Name);
            switch (n.Type)
            {
                case 0: WriteMap(w, n.Kids); break;
                case 1: WriteZ(w, n.Str ?? ""); break;
                case 2: w.Write(n.Int); break;
                default: w.Write(n.Raw); break;
            }
        }
        w.Write((byte)8);
    }
}

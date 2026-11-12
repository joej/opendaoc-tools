using CEM.Utils;
using CEM.World;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CEM.Client {
  /// <summary>
  /// Game resource class
  /// </summary>
  internal static class ClientData {
    /// <summary>
    /// Path to the game
    /// </summary>
    public static string GamePath {
      get { return Program.Config.CEM.GamePath; }
    }

    /// <summary>
    /// Zones.dat
    /// </summary>
    public static Stream ZonesDat {
      get { return OpenExactly("zones/zones.mpk/zones.dat"); }
    }

    /// <summary>
    /// Returns all world dirs (zones/, frontiers/, ...)
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<string> AddonPaths(Zone2 zone) {
      yield return "newtowns";
      yield return zone.Region.Addon; // gnihihih
      yield return "";
    }

    /// <summary>
    /// Generates all possible nif paths relative to the game dir
    /// </summary>
    /// <returns></returns>
    private static IEnumerable<string> NIFFolders(Zone2 zone) {
      yield return Path.Combine("zones", "nifs");
      yield return "nifs";
    }

    /// <summary>
    /// Generates all possible nif paths relative to the game dir
    /// </summary>
    /// <returns></returns>
    private static IEnumerable<string> DNIFFolders(Zone2 zone) {
      yield return "dnifs";
      yield return Path.Combine("zones", "dnifs");
      foreach (var f in NIFFolders(zone))
        yield return f;
    }

    #region Awful permutation methods

    private static IEnumerable<string> BuildPathPermutations(IEnumerable<string> a, IEnumerable<string> b) {
      foreach (var elemA in a) {
        foreach (var elemB in b) {
          yield return Path.Combine(elemA, elemB);
        }
      }
    }

    public static IEnumerable<string> BuildPathPermutations(IEnumerable<string> first,
                                                             params IEnumerable<string>[] rest) {
      if (rest.Length == 0) {
        /* only one set */
        return first;
      } else if (rest.Length == 1) {
        return BuildPathPermutations(first, rest[0]);
      } else {
        IEnumerable<string> parts = BuildPathPermutations(first, rest[0]);
        IEnumerable<string>[] remainder = rest.Skip(1).ToArray();
        return BuildPathPermutations(parts, remainder);
      }
    }

    #endregion

    /// <summary>
    /// True if the specified file exists and isn't empty. Works with sub-paths within a nif
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public static bool Exists(string file) {
      Stream fs = null;
      try {
        fs = OpenExactly(file);
        return fs != null;
      } finally {
        if (fs != null)
          fs.Close();
      }
    }

    /// <summary>
    /// Opens the specified file readonly, and *only* this file. Does not search in alternative paths; returns null if not found
    /// </summary>
    public static Stream OpenExactly(string file, params object[] args) {
      if (GamePath == null)
        return null;
      if (args.Length > 0)
        file = string.Format(file, args);

      string fileSystemPath = GamePath;
      string[] components = file.ToLower().Split('\\', '/');
      string nifPath = null; // sub path within a NPK

      // Split the path into pre/post NIF paths
      foreach (var comp in components) {
        if (nifPath != null) {
          nifPath = Path.Combine(nifPath, comp);
        } else {
          fileSystemPath = Path.Combine(fileSystemPath, comp);
          if (new[] { ".npk", ".mpk" }.Any(ext => comp.EndsWith(ext, StringComparison.InvariantCultureIgnoreCase)))
            nifPath = ""; // start parsing the nif subtree now
        }
      }

      // The client's folders and files are mixed case (Nifs, Dnifs, BogHall1.npk) and these paths
      // are lowercased. Always resolve against the real directory listings: a case-sensitive
      // filesystem won't find the lowercased name, and an SMB share mounted on macOS answers
      // wrong-case names inconsistently (zones/DNIFS found, zones/dnifs not).
      fileSystemPath = ResolveCaseInsensitive(fileSystemPath);
      if (fileSystemPath == null || !File.Exists(fileSystemPath)) return null;

      // If this is a nif, look inside to find the file
      if (nifPath != null) {
        TinyMPK mpk;
        try {
          mpk = TinyMPK.FromFile(fileSystemPath);
        }
        catch {
            // The file is probably empty.
            return null;
        }

        MPKFileEntry subfile = mpk.GetFile(nifPath);
        return subfile == null ? null : new MemoryStream(subfile.Data);
      }
      return new FileStream(fileSystemPath, FileMode.Open, FileAccess.Read);
    }

    /// <summary>
    /// Opens the first matching file that actually exists
    /// </summary>
    /// <param name="files"></param>
    /// <returns></returns>
    public static Stream OpenFirst(IEnumerable<string> files) {
      return (from file in files where Exists(file) select OpenExactly(file)).FirstOrDefault();
    }

    /// <summary>
    /// Finds the specified file amongst common addon paths (or returns null if not found)
    /// </summary>
    public static Stream Find(Zone2 zone, string file, params object[] args) {
      if (args.Length > 0) {
        file = string.Format(file, args);
      }
      return OpenFirst(BuildPathPermutations(AddonPaths(zone), new[] { file }));
    }

    /// <summary>
    /// Tries to find the specified csv file
    /// </summary>
    /// <param name="nif"></param>
    /// <returns></returns>
    public static Stream FindCSV(Zone2 zone, string file) {
      return OpenFirst(BuildPathPermutations(AddonPaths(zone), new[] { string.Format("zones/zone{0:D3}/csv{0:D3}.mpk", zone.ID), string.Format("zones/zone{0:D3}/dat{0:D3}.mpk", zone.ID) }, new[] { file }));
    }

    /// <summary>
    /// Opens the specified nif file
    /// </summary>
    /// <param name="nif"></param>
    /// <returns></returns>
    public static Stream FindNIF(Zone2 zone, string nif) {
      var files = new[] { nif, Path.Combine(nif.ToLower().Replace(".nif", ".npk"), nif) };
      return OpenFirst(BuildPathPermutations(AddonPaths(zone), NIFFolders(zone), files));
    }

    /// <summary>
    /// Finds the specified dungeon nif
    /// </summary>
    /// <param name="dnif"></param>
    /// <returns></returns>
    public static Stream FindDNIF(Zone2 zone, string dnif) {
      var files = new[] { dnif, Path.Combine(dnif.ToLower().Replace(".nif", ".npk"), dnif) };
      return OpenFirst(BuildPathPermutations(AddonPaths(zone), DNIFFolders(zone), files));
    }

    /// <summary>
    /// Finds all finds matching the specified pattern.
    /// Note: Does not search subdirectories & mpks at the moment
    /// </summary>
    /// <param name="dir"></param>
    /// <param name="pattern"></param>
    /// <returns></returns>
    public static IEnumerable<string> FindFiles(string dir, string pattern) {
      string full = Path.Combine(GamePath, dir);
      full = ResolveCaseInsensitive(full) ?? full;
      var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false };
      return Directory.GetFiles(full, pattern, options)
          .Select(x => x[GamePath.Length..]);
    }

    // Directory (as found on disk) -> its entries, keyed ignoring case.
    private static readonly Dictionary<string, Dictionary<string, List<string>>> _dirIndex = new(StringComparer.Ordinal);

    private static Dictionary<string, List<string>> ListDirectory(string dir) {
      lock (_dirIndex) {
        if (_dirIndex.TryGetValue(dir, out var cached))
          return cached;
      }
      var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
      if (!Directory.Exists(dir))
        return index; // not cached: dir is a name read from a listing, so this is a real miss or an I/O hiccup
      foreach (string entry in Directory.EnumerateFileSystemEntries(dir)) {
        string name = Path.GetFileName(entry);
        if (!index.TryGetValue(name, out var names))
          index[name] = names = new List<string>();
        names.Add(name);
      }
      lock (_dirIndex)
        _dirIndex[dir] = index;
      return index;
    }

    /// <summary>
    /// Finds an existing file or folder under GamePath whose path matches ignoring case, or null.
    /// Each step uses a name read from the directory listing, never the name as given.
    /// </summary>
    private static string ResolveCaseInsensitive(string fullPath) {
      string root = Path.GetFullPath(GamePath).TrimEnd(Path.DirectorySeparatorChar);
      string target = Path.GetFullPath(fullPath);
      if (!target.StartsWith(root, StringComparison.Ordinal))
        return null;
      string current = root;
      foreach (string part in target[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
        if (!ListDirectory(current).TryGetValue(part, out var names))
          return null;
        current = Path.Combine(current, names.Contains(part) ? part : names[0]);
      }
      return current;
    }
  }
}
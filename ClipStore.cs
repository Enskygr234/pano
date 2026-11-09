using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Pano;

public enum ClipKind { Text, Image, Files }

public sealed class Clip : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string? ImageFile { get; set; }
    public int ImageW { get; set; }
    public int ImageH { get; set; }
    public string? Hash { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    bool _pinned;
    public bool Pinned { get => _pinned; set { _pinned = value; Raise(nameof(Pinned)); Raise(nameof(PinVisibility)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    int _number;
    [JsonIgnore] public int Number { get => _number; set { if (_number == value) return; _number = value; Raise(nameof(NumberText)); } }
    [JsonIgnore] public string NumberText => _number is > 0 and < 10 ? _number.ToString() : "";

    [JsonIgnore] public Visibility TextVisibility => Kind == ClipKind.Image ? Visibility.Collapsed : Visibility.Visible;
    [JsonIgnore] public Visibility ImageVisibility => Kind == ClipKind.Image ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility PinVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string Preview
    {
        get
        {
            if (Kind == ClipKind.Files)
            {
                var names = (Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileName);
                return string.Join(",  ", names);
            }
            var s = Text ?? "";
            if (s.Length > 600) s = s[..600];
            var sb = new StringBuilder(s.Length);
            bool ws = false;
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch)) { if (!ws && sb.Length > 0) sb.Append(' '); ws = true; }
                else { sb.Append(ch); ws = false; }
            }
            return sb.ToString().TrimEnd();
        }
    }

    [JsonIgnore]
    public string Meta
    {
        get
        {
            string kind = Kind switch
            {
                ClipKind.Image => $"Görsel · {ImageW}×{ImageH}",
                ClipKind.Files => $"{(Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} dosya",
                _ => Lines > 1 ? $"{Lines:N0} satır · {(Text?.Length ?? 0):N0} karakter" : $"{(Text?.Length ?? 0):N0} karakter",
            };
            return $"{kind} · {Ago(Created)}" + (Pinned ? " · sabit" : "");
        }
    }

    int Lines => Text == null ? 0 : Text.Count(c => c == '\n') + 1;

    BitmapImage? _thumb;
    [JsonIgnore]
    public BitmapImage? Thumb
    {
        get
        {
            if (_thumb != null || ImageFile == null) return _thumb;
            try
            {
                var path = Path.Combine(ClipStore.ImageDir, ImageFile);
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(path);
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                b.DecodePixelHeight = Math.Min(ImageH > 0 ? ImageH : 200, 200);
                b.EndInit();
                b.Freeze();
                _thumb = b;
            }
            catch { }
            return _thumb;
        }
    }

    public void RefreshMeta() => Raise(nameof(Meta));

    string? _folded;

    /// <summary>Accent- and case-insensitive match where Turkish letters also match their ASCII forms (butce ⇄ Bütçe).</summary>
    public bool Matches(string foldedQuery)
    {
        if (foldedQuery.Length == 0) return true;
        _folded ??= Fold(Kind == ClipKind.Image ? "görsel resim image" : Text ?? "");
        return _folded.Contains(foldedQuery, StringComparison.Ordinal);
    }

    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch
            {
                'ı' or 'I' or 'İ' => 'i',
                _ => char.ToLowerInvariant(ch),
            });
        }
        return sb.ToString();
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "az önce";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} dk önce";
        if (d.TotalHours < 24 && t.Date == DateTime.Today) return $"{(int)d.TotalHours} sa önce";
        if (t.Date == DateTime.Today.AddDays(-1)) return "dün " + t.ToString("HH:mm");
        return t.ToString(t.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.GetCultureInfo("tr-TR"));
    }
}

public sealed class ClipStore
{
    public const int MaxUnpinned = 300;
    public const int MaxTextLength = 500_000;

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pano");
    public static readonly string ImageDir = Path.Combine(Dir, "gorseller");
    static readonly string DataFile = Path.Combine(Dir, "gecmis.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public ObservableCollection<Clip> Items { get; } = new();
    readonly DispatcherTimer _saveTimer;

    public ClipStore()
    {
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
    }

    public void Load()
    {
        try
        {
            Directory.CreateDirectory(ImageDir);
            if (!File.Exists(DataFile)) return;
            var list = JsonSerializer.Deserialize<List<Clip>>(File.ReadAllText(DataFile), Json) ?? new();
            foreach (var c in list)
                if (c.Kind != ClipKind.Image || (c.ImageFile != null && File.Exists(Path.Combine(ImageDir, c.ImageFile))))
                    Items.Add(c);
        }
        catch
        {
            try { File.Copy(DataFile, DataFile + ".bozuk", true); } catch { }
        }
    }

    public void Save() { _saveTimer.Stop(); _saveTimer.Start(); }

    public void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items.ToList(), Json));
            File.Move(tmp, DataFile, true);
        }
        catch { }
    }

    public Clip? AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];
        var existing = Items.FirstOrDefault(c => c.Kind == ClipKind.Text && c.Text == text);
        if (existing != null) { Bump(existing); return existing; }
        return Insert(new Clip { Kind = ClipKind.Text, Text = text });
    }

    public Clip? AddFiles(IEnumerable<string> files)
    {
        var text = string.Join("\n", files);
        if (text.Length == 0) return null;
        var existing = Items.FirstOrDefault(c => c.Kind == ClipKind.Files && c.Text == text);
        if (existing != null) { Bump(existing); return existing; }
        return Insert(new Clip { Kind = ClipKind.Files, Text = text });
    }

    public Clip? AddImage(byte[] png, int w, int h)
    {
        var hash = Convert.ToHexString(SHA1.HashData(png));
        var existing = Items.FirstOrDefault(c => c.Kind == ClipKind.Image && c.Hash == hash);
        if (existing != null) { Bump(existing); return existing; }
        Directory.CreateDirectory(ImageDir);
        var file = hash[..16] + ".png";
        File.WriteAllBytes(Path.Combine(ImageDir, file), png);
        return Insert(new Clip { Kind = ClipKind.Image, ImageFile = file, ImageW = w, ImageH = h, Hash = hash });
    }

    public void Bump(Clip c)
    {
        c.Created = DateTime.Now;
        int i = Items.IndexOf(c);
        if (i > 0) Items.Move(i, 0);
        c.RefreshMeta();
        Save();
    }

    Clip Insert(Clip c)
    {
        Items.Insert(0, c);
        Prune();
        Save();
        return c;
    }

    void Prune()
    {
        var unpinned = Items.Where(c => !c.Pinned).ToList();
        for (int i = MaxUnpinned; i < unpinned.Count; i++) Remove(unpinned[i], save: false);
    }

    public void Remove(Clip c, bool save = true)
    {
        Items.Remove(c);
        if (c.ImageFile != null)
            try { File.Delete(Path.Combine(ImageDir, c.ImageFile)); } catch { }
        if (save) Save();
    }

    public int ClearUnpinned()
    {
        var list = Items.Where(c => !c.Pinned).ToList();
        foreach (var c in list) Remove(c, save: false);
        Save();
        return list.Count;
    }
}

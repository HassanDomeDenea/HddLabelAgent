using System.Text.Json;
using System.Text.Json.Serialization;

namespace HddLabelAgent;

public sealed class SettingsStore
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HddLabelAgent");

    public static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // Keeps Arabic readable in the file instead of \uXXXX.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private AgentSettings _current;

    public event Action? Changed;

    private SettingsStore(AgentSettings current) => _current = current;

    /// <summary>A snapshot; it is replaced, never mutated, so readers need no lock of their own.</summary>
    public AgentSettings Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public static SettingsStore Load()
    {
        AgentSettings? settings = null;
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(FilePath), Json);
        }
        catch (Exception ex)
        {
            // A broken file must not keep the agent from starting; keep it aside for inspection.
            Log.Write("settings.json is unreadable, starting from defaults: " + ex.Message);
            try
            {
                File.Copy(FilePath, FilePath + ".broken", true);
            }
            catch
            {
                // best effort
            }
        }

        var isNew = settings is null;
        settings ??= new AgentSettings();
        Normalize(settings);
        var store = new SettingsStore(settings);
        if (isNew) store.Write(settings);
        return store;
    }

    public void Save(AgentSettings settings)
    {
        Normalize(settings);
        lock (_lock)
        {
            Write(settings);
            _current = settings;
        }

        Changed?.Invoke();
    }

    public void Update(Action<AgentSettings> mutate)
    {
        var copy = Clone(Current);
        mutate(copy);
        Save(copy);
    }

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;

    private void Write(AgentSettings settings)
    {
        Directory.CreateDirectory(Dir);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, FilePath, true);
    }

    public static void Normalize(AgentSettings s)
    {
        if (s.Port is < 1024 or > 65535) s.Port = AgentSettings.DefaultPort;

        s.AllowedOrigins = (s.AllowedOrigins ?? [])
            .Select(NormalizeOrigin)
            .Where(o => o.Length > 0)
            .Distinct()
            .ToList();

        s.Profiles ??= [];
        s.Profiles.RemoveAll(p => p is null);
        if (s.Profiles.Count == 0)
            s.Profiles.Add(new LabelProfile { Printer = { Name = Spooler.GuessLabelPrinter() ?? "" } });

        var seen = new HashSet<string>();
        foreach (var p in s.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || !seen.Add(p.Id))
            {
                p.Id = Guid.NewGuid().ToString("N")[..8];
                seen.Add(p.Id);
            }

            Normalize(p);
        }

        if (s.Profiles.All(p => p.Id != s.ActiveProfileId)) s.ActiveProfileId = s.Profiles[0].Id;
    }

    public static void Normalize(LabelProfile p)
    {
        p.Printer ??= new PrinterOptions();
        p.Paper ??= new PaperOptions();
        p.Layout ??= new LayoutOptions();

        var printer = p.Printer;
        printer.Name = (printer.Name ?? "").Trim();
        printer.Mode = printer.Mode == "driver" ? "driver" : "tspl";
        printer.Dpi = Math.Clamp(printer.Dpi, 150, 600);
        printer.Density = Math.Clamp(printer.Density, 0, 15);
        printer.Speed = Math.Clamp(printer.Speed, 1, 12);
        printer.Direction = printer.Direction == 0 ? 0 : 1;
        printer.Media = printer.Media is "blackmark" or "continuous" ? printer.Media : "gap";
        printer.GapMm = Math.Clamp(printer.GapMm, 0, 20);
        printer.OffsetXMm = Math.Clamp(printer.OffsetXMm, -20, 20);
        printer.OffsetYMm = Math.Clamp(printer.OffsetYMm, -20, 20);

        var paper = p.Paper;
        paper.WidthMm = Math.Clamp(paper.WidthMm, 10, 120);
        paper.HeightMm = Math.Clamp(paper.HeightMm, 8, 300);

        var lay = p.Layout;
        lay.MarginMm = Math.Clamp(lay.MarginMm, 0, Math.Min(paper.WidthMm, paper.HeightMm) / 3);
        lay.FontFamily = string.IsNullOrWhiteSpace(lay.FontFamily) ? "Tahoma" : lay.FontFamily.Trim();
        lay.MarketName ??= "";
        lay.MarketFontPt = Math.Clamp(lay.MarketFontPt, 4, 40);
        lay.NameFontPt = Math.Clamp(lay.NameFontPt, 4, 40);
        lay.PriceFontPt = Math.Clamp(lay.PriceFontPt, 4, 40);
        lay.CodeTextFontPt = Math.Clamp(lay.CodeTextFontPt, 4, 40);
        lay.NameMaxLines = Math.Clamp(lay.NameMaxLines, 1, 6);
        lay.CodeType = lay.CodeType is "auto" or "ean13" or "qr" or "none" ? lay.CodeType : "code128";
        lay.BarcodeHeightMm = Math.Clamp(lay.BarcodeHeightMm, 0, paper.HeightMm);
        lay.ModuleDots = Math.Clamp(lay.ModuleDots, 1, 8);
        lay.QrSide = lay.QrSide == "right" ? "right" : "left";
        lay.QrContent = string.IsNullOrWhiteSpace(lay.QrContent) ? "{barcode}" : lay.QrContent;

        if (string.IsNullOrWhiteSpace(p.Name))
            p.Name = FormattableString.Invariant($"{paper.WidthMm:0.#} × {paper.HeightMm:0.#} mm");
    }

    /// <summary>scheme://host[:port] in lower case, or "" when the text is not a web origin.</summary>
    public static string NormalizeOrigin(string? origin)
    {
        origin = (origin ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
        return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(SettingsStore.Dir, "agent.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.Dir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000) file.MoveTo(FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break printing
        }
    }
}

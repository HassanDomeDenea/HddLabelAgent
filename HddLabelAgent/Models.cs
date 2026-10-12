namespace HddLabelAgent;

/// <summary>Everything the agent remembers between runs (%AppData%\HddLabelAgent\settings.json).</summary>
public sealed class AgentSettings
{
    public const int DefaultPort = 17365;

    public int Port { get; set; } = DefaultPort;

    /// <summary>Web origins (scheme://host[:port]) the user allowed to print through this agent.</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    public string ActiveProfileId { get; set; } = "default";

    /// <summary>One profile per paper/printer combination; switching paper means switching profile.</summary>
    public List<LabelProfile> Profiles { get; set; } = [];

    public LabelProfile ActiveProfile() => FindProfile(ActiveProfileId) ?? Profiles[0];

    public LabelProfile? FindProfile(string? id) =>
        string.IsNullOrEmpty(id) ? null : Profiles.FirstOrDefault(p => p.Id == id);
}

public sealed class LabelProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "";
    public PrinterOptions Printer { get; set; } = new();
    public PaperOptions Paper { get; set; } = new();
    public LayoutOptions Layout { get; set; } = new();
}

public sealed class PrinterOptions
{
    /// <summary>Windows printer name.</summary>
    public string Name { get; set; } = "";

    /// <summary>"tspl" sends the label straight to the printer in its own language; "driver" prints through the Windows driver.</summary>
    public string Mode { get; set; } = "tspl";

    public int Dpi { get; set; } = 203;

    /// <summary>Darkness, 0-15.</summary>
    public int Density { get; set; } = 8;

    /// <summary>Inches per second.</summary>
    public int Speed { get; set; } = 4;

    /// <summary>0 or 1; flips the label 180°.</summary>
    public int Direction { get; set; } = 1;

    /// <summary>"gap" (die-cut labels), "blackmark" or "continuous".</summary>
    public string Media { get; set; } = "gap";

    public double GapMm { get; set; } = 2;

    /// <summary>Moves the whole print on the paper; for rolls that sit off-centre.</summary>
    public double OffsetXMm { get; set; }

    public double OffsetYMm { get; set; }

    /// <summary>For the rare printer that prints the bitmap as a negative.</summary>
    public bool InvertBitmap { get; set; }
}

public sealed class PaperOptions
{
    public double WidthMm { get; set; } = 50;
    public double HeightMm { get; set; } = 25;
}

public sealed class LayoutOptions
{
    public double MarginMm { get; set; } = 1.5;
    public string FontFamily { get; set; } = "Tahoma";

    /// <summary>Anti-aliased glyphs thresholded to black/white instead of hinted 1-bit glyphs.</summary>
    public bool SmoothText { get; set; }

    public bool ShowMarketName { get; set; }
    public string MarketName { get; set; } = "";
    public double MarketFontPt { get; set; } = 7;

    public bool ShowName { get; set; } = true;
    public double NameFontPt { get; set; } = 8;
    public int NameMaxLines { get; set; } = 2;
    public bool NameBold { get; set; } = true;

    public bool ShowPrice { get; set; } = true;
    public bool ShowCurrency { get; set; } = true;
    public double PriceFontPt { get; set; } = 9;
    public bool PriceBold { get; set; } = true;

    /// <summary>The digits under the bars.</summary>
    public bool ShowCodeText { get; set; } = true;
    public double CodeTextFontPt { get; set; } = 6.5;

    /// <summary>"code128", "auto" (EAN-13 when the digits are a valid one, else Code 128), "ean13", "qr" or "none".</summary>
    public string CodeType { get; set; } = "code128";

    /// <summary>0 = use all the room the text leaves.</summary>
    public double BarcodeHeightMm { get; set; }

    /// <summary>Widest a single bar module may be, in printer dots. It shrinks by itself when the barcode would not fit.</summary>
    public int ModuleDots { get; set; } = 3;

    /// <summary>"left" or "right".</summary>
    public string QrSide { get; set; } = "left";

    /// <summary>What the QR holds; {barcode}, {name} and {price} are replaced.</summary>
    public string QrContent { get; set; } = "{barcode}";
}

/// <summary>One label's data, as sent by the web app.</summary>
public sealed class LabelItem
{
    public string? Barcode { get; set; }
    public string? Name { get; set; }

    /// <summary>The number alone, already formatted ("10,000").</summary>
    public string? Price { get; set; }

    public string? Currency { get; set; }

    /// <summary>Number and currency the way the app writes them ("10,000 دينار", "$12.5"); wins over Price + Currency.</summary>
    public string? PriceWithCurrency { get; set; }

    /// <summary>Overrides the profile's QR content for this label.</summary>
    public string? Qr { get; set; }

    public int Copies { get; set; } = 1;
}

/// <summary>A problem the person printing can fix; its message is shown to them as is.</summary>
public sealed class LabelException(string message) : Exception(message);

public static class Units
{
    public static int MmToDots(double mm, int dpi) => (int)Math.Round(mm * dpi / 25.4);
}

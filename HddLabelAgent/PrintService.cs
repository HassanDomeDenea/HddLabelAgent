using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Globalization;
using System.Text;

namespace HddLabelAgent;

public sealed record PrintResult(string Printer, int Labels, IReadOnlyList<string> Warnings);

public sealed class PrintService
{
    public const int MaxLabelsPerRequest = 500;

    // One job at a time: two overlapping jobs to a label printer interleave their labels.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async Task<PrintResult> PrintAsync(LabelProfile profile, IReadOnlyList<LabelItem> items)
    {
        if (items.Count == 0) throw new LabelException("There is nothing to print.");
        var total = items.Sum(i => Math.Max(1, i.Copies));
        if (total > MaxLabelsPerRequest)
            throw new LabelException($"{total} labels in one go is over the limit of {MaxLabelsPerRequest}.");

        await _oneAtATime.WaitAsync();
        try
        {
            return await Task.Run(() => Print(profile, items, total));
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public async Task FeedAsync(LabelProfile profile)
    {
        if (profile.Printer.Mode != "tspl") throw new LabelException("Feeding a blank label only works in direct (TSPL) mode.");
        var printer = RequirePrinter(profile);
        await _oneAtATime.WaitAsync();
        try
        {
            var job = new MemoryStream();
            Tspl.WriteSetup(job, profile);
            Tspl.Line(job, "FORMFEED");
            Spooler.SendRaw(printer, job.ToArray(), "Label feed");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private static PrintResult Print(LabelProfile profile, IReadOnlyList<LabelItem> items, int total)
    {
        var printer = RequirePrinter(profile);
        var pages = items.Select(i => (Label: LabelRenderer.Render(profile, i), Copies: Math.Max(1, i.Copies))).ToList();
        var document = items.Count == 1 ? "Label " + (items[0].Barcode ?? items[0].Name) : $"{items.Count} labels";

        if (profile.Printer.Mode == "driver") DriverPrinter.Print(printer, profile, pages, document);
        else Spooler.SendRaw(printer, Tspl.Build(profile, pages), document);

        Log.Write($"printed {total} label(s) on \"{printer}\" ({profile.Printer.Mode}, profile \"{profile.Name}\")");
        return new PrintResult(printer, total, pages.SelectMany(p => p.Label.Warnings).Distinct().ToList());
    }

    private static string RequirePrinter(LabelProfile profile)
    {
        var name = profile.Printer.Name;
        if (name.Length == 0) throw new LabelException("No label printer is selected. Open the label printer settings and choose one.");

        var state = Spooler.GetState(name);
        if (!state.Found) throw new LabelException($"The printer \"{name}\" is not installed on this computer. Choose another one in the label printer settings.");
        if (state.Offline) throw new LabelException($"The printer \"{name}\" is offline. Check that it is switched on and its cable is connected.");
        return name;
    }
}

/// <summary>TSPL, the command language of Xprinter/TSC/Gprinter label printers. The whole label goes as one bitmap.</summary>
public static class Tspl
{
    public static byte[] Build(LabelProfile profile, IEnumerable<(RenderedLabel Label, int Copies)> pages)
    {
        var printer = profile.Printer;
        var shiftX = Units.MmToDots(printer.OffsetXMm, printer.Dpi);
        var shiftY = Units.MmToDots(printer.OffsetYMm, printer.Dpi);

        var job = new MemoryStream();
        WriteSetup(job, profile);
        foreach (var (label, copies) in pages)
        {
            // In a TSPL bitmap a 0 bit burns a dot and a 1 bit leaves the paper white.
            var rows = label.Pack(!printer.InvertBitmap, shiftX, shiftY, out var bytesPerRow);
            Line(job, "CLS");
            job.Write(Encoding.ASCII.GetBytes($"BITMAP 0,0,{bytesPerRow},{label.Height},0,"));
            job.Write(rows);
            Line(job, "");
            Line(job, $"PRINT 1,{copies}");
        }

        return job.ToArray();
    }

    public static void WriteSetup(Stream job, LabelProfile profile)
    {
        var printer = profile.Printer;
        Line(job, $"SIZE {Mm(profile.Paper.WidthMm)} mm,{Mm(profile.Paper.HeightMm)} mm");
        Line(job, printer.Media switch
        {
            "blackmark" => $"BLINE {Mm(printer.GapMm)} mm,0 mm",
            "continuous" => "GAP 0 mm,0 mm",
            _ => $"GAP {Mm(printer.GapMm)} mm,0 mm",
        });
        Line(job, $"DIRECTION {printer.Direction},0");
        Line(job, "REFERENCE 0,0");
        Line(job, $"DENSITY {printer.Density}");
        Line(job, $"SPEED {printer.Speed}");
        Line(job, "SET TEAR ON");
    }

    public static void Line(Stream job, string command) => job.Write(Encoding.ASCII.GetBytes(command + "\r\n"));

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Printing through the Windows driver, for printers that do not speak TSPL.</summary>
public static class DriverPrinter
{
    public static void Print(string printerName, LabelProfile profile, IReadOnlyList<(RenderedLabel Label, int Copies)> pages, string documentName)
    {
        var dpi = profile.Printer.Dpi;
        var shiftX = Units.MmToDots(profile.Printer.OffsetXMm, dpi);
        var shiftY = Units.MmToDots(profile.Printer.OffsetYMm, dpi);
        var queue = new Queue<RenderedLabel>(pages.SelectMany(p => Enumerable.Repeat(p.Label, p.Copies)));

        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = printerName;
        document.PrintController = new StandardPrintController(); // no "Printing..." window
        document.DocumentName = documentName;
        document.OriginAtMargins = false;
        // Paper sizes are in hundredths of an inch.
        document.DefaultPageSettings.PaperSize = new PaperSize("Label",
            (int)Math.Round(profile.Paper.WidthMm / 25.4 * 100), (int)Math.Round(profile.Paper.HeightMm / 25.4 * 100));
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        document.DefaultPageSettings.Landscape = false;

        document.PrintPage += (_, e) =>
        {
            var label = queue.Dequeue();
            using var bmp = label.ToBitmap(dpi, shiftX, shiftY);
            var g = e.Graphics!;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            // Page units are hundredths of an inch, measured from the printable area's corner.
            g.DrawImage(bmp, new RectangleF(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY,
                label.Width * 100f / dpi, label.Height * 100f / dpi));
            e.HasMorePages = queue.Count > 0;
        };

        try
        {
            document.Print();
        }
        catch (Exception ex) when (ex is InvalidPrinterException or System.ComponentModel.Win32Exception)
        {
            throw new LabelException($"Printing on \"{printerName}\" failed: {ex.Message}");
        }
    }
}

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace HddLabelAgent;

/// <summary>A label as the printer will burn it: one bit per printer dot.</summary>
public sealed class RenderedLabel(int width, int height, bool[] black)
{
    public int Width => width;
    public int Height => height;

    /// <summary>Dots per bar module (or per QR cell) actually used; 0 when the label has no code.</summary>
    public int ModuleDots { get; init; }

    public string CodeType { get; init; } = "none";
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Rows packed MSB first and padded to whole bytes. <paramref name="shiftX"/>/<paramref name="shiftY"/>
    /// move the picture on the paper, for rolls that do not sit where the printer thinks.
    /// </summary>
    public byte[] Pack(bool blackIsZero, int shiftX, int shiftY, out int bytesPerRow)
    {
        bytesPerRow = (width + 7) / 8;
        var data = new byte[bytesPerRow * height];
        if (blackIsZero) Array.Fill(data, (byte)0xFF);

        for (var y = 0; y < height; y++)
        {
            var sy = y - shiftY;
            if (sy < 0 || sy >= height) continue;
            for (var x = 0; x < width; x++)
            {
                var sx = x - shiftX;
                if (sx < 0 || sx >= width || !black[sy * width + sx]) continue;
                var i = y * bytesPerRow + (x >> 3);
                var bit = (byte)(0x80 >> (x & 7));
                if (blackIsZero) data[i] &= (byte)~bit;
                else data[i] |= bit;
            }
        }

        return data;
    }

    public Bitmap ToBitmap(int dpi, int shiftX = 0, int shiftY = 0)
    {
        var rows = Pack(true, shiftX, shiftY, out var stride);
        var bmp = new Bitmap(width, height, PixelFormat.Format1bppIndexed);
        var palette = bmp.Palette;
        palette.Entries[0] = Color.Black;
        palette.Entries[1] = Color.White;
        bmp.Palette = palette;

        var bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(rows, y * stride, bits.Scan0 + y * bits.Stride, stride);
        }
        finally
        {
            bmp.UnlockBits(bits);
        }

        bmp.SetResolution(dpi, dpi);
        return bmp;
    }

    public byte[] ToPng(int dpi)
    {
        using var bmp = ToBitmap(dpi);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

/// <summary>
/// Draws a label at the printer's own resolution. Text goes through Windows text shaping, so Arabic
/// comes out joined and right-to-left; bars are drawn a whole number of dots wide so they stay sharp.
/// </summary>
public static class LabelRenderer
{
    public static RenderedLabel Render(LabelProfile profile, LabelItem item)
    {
        var dpi = profile.Printer.Dpi;
        var width = Units.MmToDots(profile.Paper.WidthMm, dpi);
        var height = Units.MmToDots(profile.Paper.HeightMm, dpi);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.PageUnit = GraphicsUnit.Pixel;
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.TextRenderingHint = profile.Layout.SmoothText
            ? TextRenderingHint.AntiAliasGridFit
            : TextRenderingHint.SingleBitPerPixelGridFit;

        var margin = Units.MmToDots(profile.Layout.MarginMm, dpi);
        var area = new Rectangle(margin, margin, Math.Max(8, width - 2 * margin), Math.Max(8, height - 2 * margin));

        using var painter = new Painter(g, dpi, profile.Layout, item, width);
        painter.Draw(area);
        g.Flush();

        return new RenderedLabel(width, height, Threshold(bmp))
        {
            ModuleDots = painter.ModuleDots,
            CodeType = painter.CodeType,
            Warnings = painter.Warnings,
        };
    }

    private static bool[] Threshold(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var black = new bool[w * h];
        var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                for (var x = 0; x < w; x++)
                {
                    // BGRA
                    var luminance = (row[x * 4] * 114 + row[x * 4 + 1] * 587 + row[x * 4 + 2] * 299) / 1000;
                    black[y * w + x] = luminance < 140;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(bits);
        }

        return black;
    }

    private sealed record TextBlock(string Text, Font Font, StringFormat Format, int Height);

    private sealed class Painter(Graphics g, int dpi, LayoutOptions lay, LabelItem item, int labelWidth) : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public List<string> Warnings { get; } = [];
        public int ModuleDots { get; private set; }
        public string CodeType { get; private set; } = "none";

        private string Name => Clean(item.Name);
        private string Market => lay.ShowMarketName ? Clean(lay.MarketName) : "";
        private string Code => Clean(item.Barcode);

        public void Draw(Rectangle area)
        {
            if (lay.CodeType == "qr") DrawQrLabel(area);
            else DrawBarcodeLabel(area);
        }

        public void Dispose()
        {
            foreach (var d in _owned) d.Dispose();
        }

        // ---- layouts ---------------------------------------------------------------------------

        /// <summary>Name on top, bars in the middle taking the room that is left, digits and price below.</summary>
        private void DrawBarcodeLabel(Rectangle area)
        {
            var matrix = EncodeLinear();
            var gap = Math.Max(2, Units.MmToDots(0.4, dpi));
            var price = PriceText();

            for (var nameLines = Math.Max(1, lay.NameMaxLines);; nameLines--)
            {
                var top = new List<TextBlock>();
                var bottom = new List<TextBlock>();
                if (Market.Length > 0) top.Add(Fit(Market, lay.MarketFontPt, true, 1, area.Width));
                if (lay.ShowName && Name.Length > 0) top.Add(Fit(Name, lay.NameFontPt, lay.NameBold, nameLines, area.Width));
                if (matrix != null && lay.ShowCodeText) bottom.Add(Fit(Code, lay.CodeTextFontPt, false, 1, area.Width));
                if (price.Length > 0) bottom.Add(Fit(price, lay.PriceFontPt, lay.PriceBold, 1, area.Width));

                var gaps = matrix == null ? 0 : (top.Count > 0 ? gap : 0) + (bottom.Count > 0 ? gap : 0);
                var textHeight = top.Sum(b => b.Height) + bottom.Sum(b => b.Height);
                var codeHeight = 0;
                if (matrix != null)
                {
                    var room = area.Height - textHeight - gaps;
                    // A second name line gives way only when the bars would get too short to scan.
                    if (room < Units.MmToDots(4, dpi) && nameLines > 1) continue;

                    codeHeight = lay.BarcodeHeightMm > 0 ? Math.Min(room, Units.MmToDots(lay.BarcodeHeightMm, dpi)) : room;
                    if (codeHeight < Units.MmToDots(3, dpi))
                    {
                        Warnings.Add("There is very little room left for the bars. Use smaller text, fewer lines or taller paper.");
                        codeHeight = Math.Max(codeHeight, Units.MmToDots(2, dpi));
                    }
                }

                var y = area.Top + Math.Max(0, (area.Height - textHeight - gaps - codeHeight) / 2);
                foreach (var block in top) y = DrawText(block, area.Left, y, area.Width);
                if (matrix != null)
                {
                    if (top.Count > 0) y += gap;
                    DrawBars(matrix, area, y, codeHeight);
                    y += codeHeight;
                    if (bottom.Count > 0) y += gap;
                }

                foreach (var block in bottom) y = DrawText(block, area.Left, y, area.Width);
                return;
            }
        }

        /// <summary>QR square on one side, the text stacked beside it.</summary>
        private void DrawQrLabel(Rectangle area)
        {
            var content = QrContent();
            var price = PriceText();
            var hasText = Market.Length > 0 || (lay.ShowName && Name.Length > 0) || price.Length > 0 ||
                          (lay.ShowCodeText && Code.Length > 0);
            if (content.Length == 0)
            {
                Warnings.Add("Nothing to put in the QR code: this item has no barcode.");
                if (hasText) DrawTextStack(area, price);
                return;
            }

            var matrix = Encode(content, BarcodeFormat.QR_CODE);
            var cells = matrix.Width;
            var side = hasText ? Math.Min(area.Height, area.Width / 2) : Math.Min(area.Height, area.Width);
            // One cell of quiet zone each side inside the square; the label margin supplies the rest.
            var cell = side / (cells + 2);
            if (cell < 1) throw new LabelException("The QR content is too long for this label.");
            if (cell < 2) Warnings.Add("The QR cells are only 1 dot wide and may not scan. Shorten the content or use bigger paper.");

            var size = cells * cell;
            var left = lay.QrSide != "right";
            var squareLeft = !hasText ? area.Left + (area.Width - side) / 2 : left ? area.Left : area.Right - side;
            var qx = squareLeft + (side - size) / 2;
            var qy = area.Top + (area.Height - size) / 2;
            for (var row = 0; row < cells; row++)
            {
                var run = -1;
                for (var col = 0; col <= cells; col++)
                {
                    var on = col < cells && matrix[col, row];
                    if (on && run < 0) run = col;
                    else if (!on && run >= 0)
                    {
                        g.FillRectangle(Brushes.Black, qx + run * cell, qy + row * cell, (col - run) * cell, cell);
                        run = -1;
                    }
                }
            }

            ModuleDots = cell;
            CodeType = "qr";
            if (!hasText) return;

            var gap = Units.MmToDots(1, dpi);
            var textArea = left
                ? Rectangle.FromLTRB(area.Left + side + gap, area.Top, area.Right, area.Bottom)
                : Rectangle.FromLTRB(area.Left, area.Top, area.Right - side - gap, area.Bottom);
            DrawTextStack(textArea, price);
        }

        private void DrawTextStack(Rectangle area, string price)
        {
            for (var nameLines = Math.Max(1, lay.NameMaxLines);; nameLines--)
            {
                var blocks = new List<TextBlock>();
                if (Market.Length > 0) blocks.Add(Fit(Market, lay.MarketFontPt, true, 1, area.Width));
                if (lay.ShowName && Name.Length > 0) blocks.Add(Fit(Name, lay.NameFontPt, lay.NameBold, nameLines, area.Width));
                if (lay.ShowCodeText && Code.Length > 0) blocks.Add(Fit(Code, lay.CodeTextFontPt, false, 1, area.Width));
                if (price.Length > 0) blocks.Add(Fit(price, lay.PriceFontPt, lay.PriceBold, 1, area.Width));

                var total = blocks.Sum(b => b.Height);
                if (total > area.Height && nameLines > 1) continue;
                if (total > area.Height) Warnings.Add("The text does not all fit on this label. Use smaller text or bigger paper.");

                var y = area.Top + Math.Max(0, (area.Height - total) / 2);
                foreach (var block in blocks) y = DrawText(block, area.Left, y, area.Width);
                return;
            }
        }

        // ---- codes -----------------------------------------------------------------------------

        private BitMatrix? EncodeLinear()
        {
            if (lay.CodeType == "none") return null;
            if (Code.Length == 0)
            {
                Warnings.Add("This item has no barcode, so the label has no bars.");
                return null;
            }

            var ean = IsEan13(Code);
            if (lay.CodeType == "ean13" && !ean)
                Warnings.Add("This is not a valid EAN-13 number, so it is printed as Code 128.");

            var useEan = ean && lay.CodeType is "ean13" or "auto";
            CodeType = useEan ? "ean13" : "code128";
            return Encode(Code, useEan ? BarcodeFormat.EAN_13 : BarcodeFormat.CODE_128);
        }

        private static BitMatrix Encode(string content, BarcodeFormat format)
        {
            var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
            if (format == BarcodeFormat.QR_CODE)
            {
                hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
                hints[EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M;
            }
            else if (format == BarcodeFormat.CODE_128)
            {
                // Shortest possible symbol, so the bars can be as wide as the label allows.
                hints[EncodeHintType.CODE128_COMPACT] = true;
            }

            try
            {
                // Size 0 asks for the bare symbol: one pixel per module, no quiet zone.
                return new MultiFormatWriter().encode(content, format, 0, 0, hints);
            }
            catch (Exception ex) when (ex is ArgumentException or WriterException or System.FormatException)
            {
                throw new LabelException(format == BarcodeFormat.QR_CODE
                    ? "This text cannot be made into a QR code: " + ex.Message
                    : $"\"{content}\" cannot be printed as a barcode (only English letters, digits and symbols can). Use a QR code for other text.");
            }
        }

        private void DrawBars(BitMatrix matrix, Rectangle area, int y, int height)
        {
            const int quiet = 10; // blank modules a scanner wants on each side
            var modules = matrix.Width;
            var module = Math.Clamp(lay.ModuleDots, 1, 8);
            while (module > 1 && (modules * module > area.Width || (modules + 2 * quiet) * module > labelWidth)) module--;

            if (modules * module > area.Width)
                throw new LabelException("This barcode is too long to fit on the label. Use a QR code or wider paper.");
            if (module == 1)
                Warnings.Add("The bars had to be made 1 dot wide to fit, and many scanners will not read that. Use a QR code, a shorter barcode or wider paper.");

            var x = area.Left + (area.Width - modules * module) / 2;
            var run = -1;
            for (var i = 0; i <= modules; i++)
            {
                var on = i < modules && matrix[i, 0];
                if (on && run < 0) run = i;
                else if (!on && run >= 0)
                {
                    g.FillRectangle(Brushes.Black, x + run * module, y, (i - run) * module, height);
                    run = -1;
                }
            }

            ModuleDots = module;
        }

        private string QrContent()
        {
            var explicitContent = (item.Qr ?? "").Trim();
            if (explicitContent.Length > 0) return explicitContent;
            if (Code.Length == 0 && lay.QrContent.Contains("{barcode}")) return "";
            return lay.QrContent
                .Replace("{barcode}", Code)
                .Replace("{name}", Name)
                .Replace("{price}", Clean(item.Price))
                .Trim();
        }

        private static bool IsEan13(string code)
        {
            if (code.Length != 13 || !code.All(char.IsAsciiDigit)) return false;
            var sum = 0;
            for (var i = 0; i < 12; i++) sum += (code[i] - '0') * (i % 2 == 0 ? 1 : 3);
            return (10 - sum % 10) % 10 == code[12] - '0';
        }

        // ---- text ------------------------------------------------------------------------------

        private string PriceText()
        {
            if (!lay.ShowPrice) return "";
            var price = Clean(item.Price);
            var full = Clean(item.PriceWithCurrency);
            if (!lay.ShowCurrency) return price.Length > 0 ? price : full;
            if (full.Length > 0) return full;
            var currency = Clean(item.Currency);
            return price.Length > 0 && currency.Length > 0 ? $"{price} {currency}" : price;
        }

        /// <summary>
        /// Lays text out in at most <paramref name="maxLines"/> lines, shrinking the font a little before
        /// giving up and ending the text with an ellipsis.
        /// </summary>
        private TextBlock Fit(string text, double points, bool bold, int maxLines, int width)
        {
            var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.EllipsisCharacter,
            };
            format.FormatFlags |= StringFormatFlags.LineLimit;
            // The base direction decides where digits and Latin words land inside an Arabic line.
            if (HasRightToLeft(text)) format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
            _owned.Add(format);

            var size = (float)(points * dpi / 72.0);
            var smallest = Math.Max(9f, size * 0.7f);
            while (true)
            {
                var font = CreateFont(size, bold);
                var lineHeight = font.GetHeight(g);
                g.MeasureString(text, font, new SizeF(width, lineHeight * maxLines + 1), format, out var fitted, out var lines);
                if (fitted >= text.Length || size <= smallest)
                {
                    _owned.Add(font);
                    return new TextBlock(text, font, format, (int)Math.Ceiling(lineHeight * Math.Max(1, lines)));
                }

                font.Dispose();
                size -= 1f;
            }
        }

        private Font CreateFont(float pixels, bool bold)
        {
            try
            {
                return new Font(lay.FontFamily, pixels, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
                // The family has no such style (some display fonts are bold-only or regular-only).
                return new Font("Tahoma", pixels, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            }
        }

        private int DrawText(TextBlock block, int x, int y, int width)
        {
            g.DrawString(block.Text, block.Font, Brushes.Black, new RectangleF(x, y, width, block.Height + 1), block.Format);
            return y + block.Height;
        }

        private static string Clean(string? text) =>
            string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private static bool HasRightToLeft(string text)
        {
            foreach (var c in text)
            {
                if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
                    return true;
            }

            return false;
        }
    }
}

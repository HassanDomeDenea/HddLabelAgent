using System.ComponentModel;
using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace HddLabelAgent;

public sealed record PrinterState(string Name, bool Found, bool Offline, int Jobs, string Driver, string Port, bool IsDefault);

/// <summary>The Windows print spooler: listing printers, their state, and sending bytes untouched.</summary>
public static class Spooler
{
    public static List<string> PrinterNames() => PrinterSettings.InstalledPrinters.Cast<string>().ToList();

    public static List<PrinterState> Printers() => PrinterNames().Select(GetState).ToList();

    /// <summary>The first installed printer that looks like a label printer, for a sensible first-run default.</summary>
    public static string? GuessLabelPrinter()
    {
        string[] hints = ["xprinter", "xp-", "label", "barcode", "tsc", "zebra", "godex", "gprinter", "zdesigner", "rongta"];
        try
        {
            return PrinterNames().FirstOrDefault(n => hints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return null;
        }
    }

    public static PrinterState GetState(string name)
    {
        var isDefault = false;
        try
        {
            isDefault = new PrinterSettings().PrinterName == name;
        }
        catch
        {
            // no default printer
        }

        if (!OpenPrinter(name, out var handle, IntPtr.Zero)) return new PrinterState(name, false, true, 0, "", "", isDefault);
        try
        {
            GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return new PrinterState(name, true, false, 0, "", "", isDefault);

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinter(handle, 2, buffer, needed, out _)) return new PrinterState(name, true, false, 0, "", "", isDefault);
                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer);
                var offline = (info.Attributes & PRINTER_ATTRIBUTE_WORK_OFFLINE) != 0 || (info.Status & PRINTER_STATUS_OFFLINE) != 0;
                return new PrinterState(name, true, offline, (int)info.cJobs,
                    Marshal.PtrToStringUni(info.pDriverName) ?? "", Marshal.PtrToStringUni(info.pPortName) ?? "", isDefault);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>Hands bytes to the printer as they are, bypassing the driver's own rendering.</summary>
    public static void SendRaw(string printerName, byte[] data, string documentName)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            throw new LabelException($"Windows could not open the printer \"{printerName}\": {LastError()}");
        try
        {
            var doc = new DOC_INFO_1 { pDocName = documentName, pOutputFile = null, pDatatype = "RAW" };
            if (StartDocPrinter(handle, 1, ref doc) == 0)
                throw new LabelException($"The printer \"{printerName}\" refused the job: {LastError()}");
            try
            {
                if (!StartPagePrinter(handle)) throw new LabelException("The printer refused the page: " + LastError());
                try
                {
                    if (!WritePrinter(handle, data, data.Length, out var written) || written != data.Length)
                        throw new LabelException("Sending the label to the printer failed: " + LastError());
                }
                finally
                {
                    EndPagePrinter(handle);
                }
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    private const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x400;
    private const uint PRINTER_STATUS_OFFLINE = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_INFO_2
    {
        public IntPtr pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation, pDevMode,
            pSepFile, pPrintProcessor, pDatatype, pParameters, pSecurityDescriptor;

        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPrinter(IntPtr hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);
}

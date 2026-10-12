using System.Drawing.Text;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HddLabelAgent;

public sealed class RenderRequest
{
    public string? ProfileId { get; set; }

    /// <summary>An unsaved profile from the settings page, so changes can be tried before they are kept.</summary>
    public LabelProfile? Profile { get; set; }

    public LabelItem? Item { get; set; }
    public List<LabelItem>? Items { get; set; }
}

/// <summary>The HTTP endpoint on 127.0.0.1 that the web app (and the agent's own settings page) talks to.</summary>
public sealed class ApiServer(SettingsStore store, OriginGate gate, PrintService printing)
{
    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static readonly LabelItem Sample = new()
    {
        Barcode = "6281234567895",
        Name = "باراسيتامول 500 ملغ أقراص",
        Price = "2,500",
        Currency = "دينار",
        PriceWithCurrency = "2,500 دينار",
    };

    private WebApplication? _app;

    public int Port { get; private set; }

    public void Start()
    {
        Port = store.Current.Port;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Loopback only: the agent is never reachable from another computer.
            kestrel.ListenLocalhost(Port);
            kestrel.Limits.MaxRequestBodySize = 1_000_000;
        });
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString);

        var app = builder.Build();
        app.Use(Guard);
        Map(app);
        app.Start();
        _app = app;
    }

    public void Stop()
    {
        try
        {
            _app?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch
        {
            // shutting down anyway
        }
    }

    /// <summary>
    /// Decides who may talk to the agent. A web page can make the browser send requests here, so every
    /// request from a site the user has not approved is stopped, whatever CORS would let the page read.
    /// </summary>
    private async Task Guard(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var response = context.Response;

        // A request for any other host name is a DNS-rebinding attempt.
        if (request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var isWrite = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
        var originHeader = request.Headers.Origin.ToString();
        if (originHeader.Length > 0 && !IsOwnOrigin(originHeader))
        {
            var origin = SettingsStore.NormalizeOrigin(originHeader);
            if (origin.Length == 0)
            {
                await Refuse(response, StatusCodes.Status403Forbidden, "This page is not allowed to use the label printer.");
                return;
            }

            response.Headers.AccessControlAllowOrigin = originHeader;
            response.Headers.Vary = "Origin";
            if (HttpMethods.IsOptions(request.Method))
            {
                // The preflight only tells the browser it may send the real request; that one is checked below.
                response.Headers.AccessControlAllowMethods = "GET, POST, PUT, OPTIONS";
                response.Headers.AccessControlAllowHeaders = "Content-Type";
                response.Headers.AccessControlMaxAge = "600";
                response.Headers["Access-Control-Allow-Private-Network"] = "true";
                response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // /api/ping only says the agent is here, so a site can find it without bothering the user.
            if (!request.Path.Equals("/api/ping") && !gate.IsAllowed(origin) && !await gate.AskAsync(origin))
            {
                await Refuse(response, StatusCodes.Status403Forbidden,
                    "This site was not allowed to use the label printer. It can be allowed in the label printer settings.", "origin_refused");
                return;
            }
        }
        else if (originHeader.Length == 0 && isWrite)
        {
            // Browsers always name the origin on a cross-site write; one that arrives without it but is
            // marked cross-site is not from a page we can identify.
            var site = request.Headers["Sec-Fetch-Site"].ToString();
            if (site.Length > 0 && site is not ("same-origin" or "none"))
            {
                await Refuse(response, StatusCodes.Status403Forbidden, "This page is not allowed to use the label printer.");
                return;
            }
        }

        // JSON only, so a plain HTML form on some web page cannot post a job.
        if (isWrite && request.ContentLength > 0 && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            await Refuse(response, StatusCodes.Status415UnsupportedMediaType, "Send JSON.");
            return;
        }

        try
        {
            await next(context);
        }
        catch (LabelException ex)
        {
            if (!response.HasStarted) await Refuse(response, StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        catch (Exception ex) when (ex is not BadHttpRequestException)
        {
            Log.Write($"{request.Method} {request.Path} failed: {ex}");
            if (!response.HasStarted) await Refuse(response, StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    private bool IsOwnOrigin(string origin) =>
        origin == $"http://127.0.0.1:{Port}" || origin == $"http://localhost:{Port}" || origin == $"http://[::1]:{Port}";

    private static Task Refuse(HttpResponse response, int status, string error, string? code = null)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new { ok = false, error, code });
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(Page("index.html"), "text/html; charset=utf-8"));

        app.MapGet("/api/ping", () => new { app = "HddLabelAgent", version = Version });

        app.MapGet("/api/status", () =>
        {
            var settings = store.Current;
            var profile = settings.ActiveProfile();
            return new
            {
                app = "HddLabelAgent",
                version = Version,
                port = Port,
                activeProfile = Describe(profile),
                printer = profile.Printer.Name.Length > 0 ? Spooler.GetState(profile.Printer.Name) : null,
                profiles = settings.Profiles.Select(Describe),
            };
        });

        app.MapGet("/api/printers", () => Spooler.Printers());

        app.MapGet("/api/fonts", () =>
        {
            using var fonts = new InstalledFontCollection();
            return fonts.Families.Select(f => f.Name).Where(n => n.Length > 0).ToList();
        });

        app.MapGet("/api/settings", () => store.Current);

        app.MapPut("/api/settings", (AgentSettings settings) =>
        {
            store.Save(settings);
            return new { ok = true, settings = store.Current, restartNeeded = store.Current.Port != Port };
        });

        app.MapPost("/api/preview", (RenderRequest request) =>
        {
            var profile = ProfileFor(request);
            var label = LabelRenderer.Render(profile, request.Item ?? Sample);
            return new
            {
                ok = true,
                png = "data:image/png;base64," + Convert.ToBase64String(label.ToPng(profile.Printer.Dpi)),
                width = label.Width,
                height = label.Height,
                dpi = profile.Printer.Dpi,
                codeType = label.CodeType,
                moduleDots = label.ModuleDots,
                warnings = label.Warnings,
            };
        });

        app.MapPost("/api/print", async (RenderRequest request) =>
        {
            var items = request.Items ?? (request.Item != null ? [request.Item] : []);
            var result = await printing.PrintAsync(ProfileFor(request), items);
            return new { ok = true, printer = result.Printer, labels = result.Labels, warnings = result.Warnings };
        });

        app.MapPost("/api/feed", async (RenderRequest request) =>
        {
            await printing.FeedAsync(ProfileFor(request));
            return new { ok = true };
        });
    }

    private LabelProfile ProfileFor(RenderRequest request)
    {
        if (request.Profile != null)
        {
            SettingsStore.Normalize(request.Profile);
            return request.Profile;
        }

        var settings = store.Current;
        if (!string.IsNullOrEmpty(request.ProfileId))
            return settings.FindProfile(request.ProfileId) ?? throw new LabelException("That label profile no longer exists.");
        return settings.ActiveProfile();
    }

    private static object Describe(LabelProfile profile) => new
    {
        id = profile.Id,
        name = profile.Name,
        widthMm = profile.Paper.WidthMm,
        heightMm = profile.Paper.HeightMm,
        printer = profile.Printer.Name,
        mode = profile.Printer.Mode,
        codeType = profile.Layout.CodeType,
    };

    private static string Page(string name)
    {
        // Lets the page be edited without rebuilding while developing.
        var folder = Environment.GetEnvironmentVariable("HDDLABELAGENT_WWWROOT");
        if (!string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, name)))
            return File.ReadAllText(Path.Combine(folder, name));

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HddLabelAgent.wwwroot." + name)
                           ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

using Microsoft.AspNetCore.Authentication.Cookies;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Security;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Transform;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration --------------------------------------------------------
// Local-only overrides (gitignored: appsettings.*.local.json) for machine-specific values such as
// the Tesseract tessdata path or secrets — keeps the committed appsettings.json portable / CI-safe.
builder.Configuration.AddJsonFile(
    $"appsettings.{builder.Environment.EnvironmentName}.local.json", optional: true, reloadOnChange: true);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing connection string 'Default'.");

// ---- Data access (Dapper over a single connection factory) ----------------
builder.Services.AddSingleton(new SqlConnectionFactory(connectionString));
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<OcrRepository>();
builder.Services.AddScoped<IMappingRepository, MappingRepository>();
builder.Services.AddScoped<ProcessorRepository>();

// ---- Security -------------------------------------------------------------
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

// ---- Domain services ------------------------------------------------------
// Select OCR provider via config "Ocr:Provider" (Tesseract | GoogleDocAi).
builder.Services.Configure<GoogleDocAiOptions>(builder.Configuration.GetSection("Ocr:GoogleDocAi"));
builder.Services.Configure<TesseractOptions>(builder.Configuration.GetSection("Ocr:Tesseract"));
builder.Services.Configure<PaddleOptions>(builder.Configuration.GetSection("Ocr:Paddle"));
builder.Services.Configure<OcrPipeline.Web.Services.Zonal.LineItemConsolidationOptions>(builder.Configuration.GetSection("Ocr:LineItemConsolidation"));

// Shared, stateless OCR helpers (pure normalization + managed image preprocessing).
builder.Services.AddSingleton<OcrPipeline.Web.Services.Normalization.TextNormalizer>();
builder.Services.AddSingleton<ImagePreprocessor>();
builder.Services.AddSingleton<OcrPipeline.Web.Services.Imaging.PagePreviewRenderer>();
builder.Services.AddSingleton<DocumentAiMapper>();          // shared Document-proto -> OcrExtraction mapper
builder.Services.AddSingleton<IPdfPageCounter, PdfPageCounter>();

// Tesseract is always resolvable as a concrete service: it is the offline IOcrEngine/IRegionOcrEngine
// default AND the engine PaddleRegionOcrEngine degrades to when the sidecar is unreachable. The interface
// registrations below alias this single per-scope instance so it is never built twice in one scope.
builder.Services.AddScoped<TesseractOcrEngine>();

var ocrProvider = builder.Configuration["Ocr:Provider"] ?? "Tesseract";
if (string.Equals(ocrProvider, "GoogleDocAi", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IOcrEngine, GoogleDocumentAiEngine>();
else
    builder.Services.AddScoped<IOcrEngine>(sp => sp.GetRequiredService<TesseractOcrEngine>());

// Zonal (template-based) OCR reads cropped regions independently of the whole-document provider above
// (Document AI does not do per-zone cropping). Select via "Ocr:RegionProvider" (Tesseract | Paddle);
// Paddle posts crops to the PaddleOCR sidecar (ocr-service/) for far higher word accuracy, Tesseract
// stays the offline default so the app runs without Docker — and is the fallback when the sidecar is down.
var regionProvider = builder.Configuration["Ocr:RegionProvider"] ?? "Tesseract";
if (string.Equals(regionProvider, "Paddle", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IRegionOcrEngine, PaddleRegionOcrEngine>();
else
    builder.Services.AddScoped<IRegionOcrEngine>(sp => sp.GetRequiredService<TesseractOcrEngine>());

// Table-layout auto-detect for the zone designer (Option ③-B "rough-box → auto-columns"). Selected by
// "Ocr:TableDetect:Provider" (Paddle | None). Default None => the Auto-detect button is present but inert
// (returns a note), so an unconfigured deployment is unchanged and manual drawing is unaffected.
var tableDetectProvider = builder.Configuration["Ocr:TableDetect:Provider"] ?? "None";
if (string.Equals(tableDetectProvider, "Paddle", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<OcrPipeline.Web.Services.Zonal.ITableLayoutDetector, PaddleStructureTableDetector>();
else
    builder.Services.AddScoped<OcrPipeline.Web.Services.Zonal.ITableLayoutDetector, OcrPipeline.Web.Services.Zonal.NullTableLayoutDetector>();

builder.Services.AddScoped<ExtractionService>();
builder.Services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();

// Transformer plugins (Drupal-style preprocess). Register each implementation
// against IValueTransformer; the pipeline resolves them by Type.
builder.Services.AddScoped<IValueTransformer, TrimTransformer>();
builder.Services.AddScoped<IValueTransformer, CaseTransformer>();
builder.Services.AddScoped<IValueTransformer, RegexReplaceTransformer>();
builder.Services.AddScoped<IValueTransformer, NumberCleanTransformer>();
builder.Services.AddScoped<IValueTransformer, DateNormalizeTransformer>();
builder.Services.AddScoped<IValueTransformer, DefaultValueTransformer>();
builder.Services.AddScoped<IValueTransformer, AiSummaryTransformer>();
builder.Services.AddScoped<IValueTransformer, TranslateTransformer>();
builder.Services.AddScoped<TransformerPipeline>();

builder.Services.AddScoped<MappingEngine>();
builder.Services.AddScoped<OcrPipeline.Web.Services.Zonal.ZonalExtractionService>();
builder.Services.AddScoped<IPipelineRunner, PipelineService>();

// ---- Queue processing (off the request thread) ----------------------------
builder.Services.Configure<OcrPipeline.Web.Services.Queue.QueueOptions>(builder.Configuration.GetSection("Ocr:Queue"));
builder.Services.AddSingleton<OcrPipeline.Web.Services.Queue.IJobQueue, OcrPipeline.Web.Services.Queue.ChannelJobQueue>();
builder.Services.AddSingleton<OcrPipeline.Web.Services.Queue.JobRunner>();
builder.Services.AddHostedService<OcrPipeline.Web.Services.Queue.PipelineWorker>();

// ---- Export / Consumption (push the mapped model downstream) ---------------
builder.Services.Configure<OcrPipeline.Web.Services.Export.ExportOptions>(builder.Configuration.GetSection("Ocr:Export"));
builder.Services.AddHttpClient();
builder.Services.AddScoped<IExportRepository, ExportRepository>();
builder.Services.AddScoped<OcrPipeline.Web.Services.Export.IExportTarget, OcrPipeline.Web.Services.Export.RestWebhookExporter>();
builder.Services.AddScoped<OcrPipeline.Web.Services.Export.IExportTarget, OcrPipeline.Web.Services.Export.ErpExporter>();
builder.Services.AddScoped<OcrPipeline.Web.Services.Export.ExportService>();
builder.Services.AddSingleton<OcrPipeline.Web.Services.Export.IExportQueue, OcrPipeline.Web.Services.Export.ChannelExportQueue>();
builder.Services.AddHostedService<OcrPipeline.Web.Services.Export.ExportWorker>();

// ---- Auth (cookie) --------------------------------------------------------
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

builder.Services.AddControllersWithViews();
// Allow the visual mapper's fetch() POST to send the antiforgery token via header.
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers(); // attribute-routed API controllers (api/documents/...)
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Documents}/{action=Index}/{id?}");

app.Run();

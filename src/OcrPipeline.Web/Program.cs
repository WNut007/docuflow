using Microsoft.AspNetCore.Authentication.Cookies;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Security;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Transform;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration --------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing connection string 'Default'.");

// ---- Data access (Dapper over a single connection factory) ----------------
builder.Services.AddSingleton(new SqlConnectionFactory(connectionString));
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<OcrRepository>();
builder.Services.AddScoped<MappingRepository>();
builder.Services.AddScoped<ProcessorRepository>();

// ---- Security -------------------------------------------------------------
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

// ---- Domain services ------------------------------------------------------
// Select OCR provider via config "Ocr:Provider" (Tesseract | GoogleDocAi).
builder.Services.Configure<GoogleDocAiOptions>(builder.Configuration.GetSection("Ocr:GoogleDocAi"));
builder.Services.Configure<TesseractOptions>(builder.Configuration.GetSection("Ocr:Tesseract"));

// Shared, stateless OCR helpers (pure normalization + managed image preprocessing).
builder.Services.AddSingleton<OcrPipeline.Web.Services.Normalization.TextNormalizer>();
builder.Services.AddSingleton<ImagePreprocessor>();
builder.Services.AddSingleton<OcrPipeline.Web.Services.Imaging.PagePreviewRenderer>();

var ocrProvider = builder.Configuration["Ocr:Provider"] ?? "Tesseract";
if (string.Equals(ocrProvider, "GoogleDocAi", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IOcrEngine, GoogleDocumentAiEngine>();
else
    builder.Services.AddScoped<IOcrEngine, TesseractOcrEngine>();

builder.Services.AddScoped<ExtractionService>();

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
builder.Services.AddScoped<PipelineService>();

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

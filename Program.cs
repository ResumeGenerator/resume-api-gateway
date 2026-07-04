using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;

const long MaxResumeUploadBytes = 50L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxResumeUploadBytes;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("ocelot.json", optional: false, reloadOnChange: true);

var jwtSettings = builder.Configuration.GetSection("Jwt");
var authority = Environment.GetEnvironmentVariable("JWT_AUTHORITY") ?? jwtSettings["Authority"];
var audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? jwtSettings["Audience"];
var issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? jwtSettings["Issuer"];
var validAudiences = BuildAcceptedJwtValues(audience);
var validIssuers = BuildAcceptedJwtValues(issuer);
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? Environment.GetEnvironmentVariable("JWT_KEY")
    ?? jwtSettings["Secret"]
    ?? jwtSettings["Key"];
var corsAllowedOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS")?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var parserUrl = Environment.GetEnvironmentVariable("PARSER_URL") ?? "http://localhost:5001";
var templateApiUrl = Environment.GetEnvironmentVariable("TEMPLATE_API_URL") ?? "http://localhost:5002";
var authApiUrl = Environment.GetEnvironmentVariable("AUTH_API_URL") ?? "http://localhost:5003";
var gatewayBaseUrl = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL")
    ?? (Environment.GetEnvironmentVariable("RAILWAY_PUBLIC_DOMAIN") is { Length: > 0 } railwayDomain
        ? $"https://{railwayDomain}"
        : "http://localhost:5189");

var parserUri = new Uri(parserUrl);
var templateApiUri = new Uri(templateApiUrl);
var authApiUri = new Uri(authApiUrl);

ConfigureDownstreamRoute(builder.Configuration, "resume-parser-upload", parserUri);
ConfigureDownstreamRoute(builder.Configuration, "resume-parser-rephrase", parserUri);
ConfigureDownstreamRoute(builder.Configuration, "parser-api", parserUri);
ConfigureDownstreamRoute(builder.Configuration, "template-api", templateApiUri);
ConfigureDownstreamRoute(builder.Configuration, "auth-short", authApiUri);
ConfigureDownstreamRoute(builder.Configuration, "auth-api", authApiUri);
ConfigureDownstreamRoute(builder.Configuration, "template-api-catchall", templateApiUri);
builder.Configuration["GlobalConfiguration:BaseUrl"] = gatewayBaseUrl;

builder.Services.AddCors(options =>
{
    options.AddPolicy("GatewayCors", policy =>
    {
        policy.AllowAnyHeader()
            .AllowAnyMethod();

        if (corsAllowedOrigins is { Length: > 0 })
        {
            policy.WithOrigins(corsAllowedOrigins);
        }
        else
        {
            policy.AllowAnyOrigin();
        }
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var signingKey = string.IsNullOrWhiteSpace(jwtSecret)
            ? null
            : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

        if (signingKey is null && !string.IsNullOrWhiteSpace(authority))
        {
            options.Authority = authority;
            options.Audience = audience;
        }

        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = validIssuers.Length > 0,
            ValidateAudience = validAudiences.Length > 0,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = signingKey is not null,
            IssuerSigningKey = signingKey,
            ValidIssuers = validIssuers,
            ValidAudiences = validAudiences,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });

builder.Services.AddAuthorization();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxResumeUploadBytes;
});
builder.Services.AddOcelot(builder.Configuration)
    .AddDelegatingHandler<RephraseRequestSanitizingHandler>(true)
    .AddDelegatingHandler<DownstreamRequestLoggingHandler>(true);

var app = builder.Build();

app.Logger.LogInformation(
    "Gateway downstream targets configured. Parser={ParserUrl}; TemplateApi={TemplateApiUrl}; AuthApi={AuthApiUrl}",
    parserUri,
    templateApiUri,
    authApiUri);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors("GatewayCors");
app.Use(async (context, next) =>
{
    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});
app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health") || context.Request.Path == "/")
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            message = "API Gateway is running",
            note = "JWT validation is enabled and parser/template/auth routes are configured"
        });
        return;
    }

    await next();
});

await app.UseOcelot();

app.Run();

static string[] BuildAcceptedJwtValues(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return Array.Empty<string>();
    }

    var trimmed = value.Trim();
    var withoutTrailingSlash = trimmed.TrimEnd('/');

    return string.Equals(trimmed, withoutTrailingSlash, StringComparison.Ordinal)
        ? new[] { trimmed }
        : new[] { trimmed, withoutTrailingSlash };
}

static void ConfigureDownstreamRoute(IConfiguration configuration, string routeKey, Uri downstreamUri)
{
    var route = configuration.GetSection("Routes")
        .GetChildren()
        .FirstOrDefault(section => string.Equals(section["Key"], routeKey, StringComparison.OrdinalIgnoreCase));

    if (route is null)
    {
        throw new InvalidOperationException($"Ocelot route '{routeKey}' was not found.");
    }

    route["DownstreamScheme"] = downstreamUri.Scheme;
    route["DownstreamHostAndPorts:0:Host"] = downstreamUri.Host;
    route["DownstreamHostAndPorts:0:Port"] = downstreamUri.Port.ToString();
}

sealed class DownstreamRequestLoggingHandler(ILogger<DownstreamRequestLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Forwarding gateway request downstream: {Method} {DownstreamUrl}",
            request.Method,
            request.RequestUri);

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode >= StatusCodes.Status500InternalServerError)
            {
                logger.LogError(
                    "Downstream request failed: {Method} {DownstreamUrl} returned {StatusCode} {ReasonPhrase}",
                    request.Method,
                    request.RequestUri,
                    (int)response.StatusCode,
                    response.ReasonPhrase);
            }

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Downstream request threw before a response was received: {Method} {DownstreamUrl}",
                request.Method,
                request.RequestUri);
            throw;
        }
    }
}

sealed class RephraseRequestSanitizingHandler(ILogger<RephraseRequestSanitizingHandler> logger) : DelegatingHandler
{
    private const string RephrasePath = "/api/resumes/rephrase";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ShouldSanitize(request))
        {
            await SanitizeJsonBody(request, cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static bool ShouldSanitize(HttpRequestMessage request)
    {
        if (request.Content is null || request.RequestUri is null || request.Method != HttpMethod.Post)
        {
            return false;
        }

        if (!string.Equals(request.RequestUri.AbsolutePath, RephrasePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var mediaType = request.Content.Headers.ContentType?.MediaType;
        return mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task SanitizeJsonBody(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return;
        }

        var originalContentType = request.Content.Headers.ContentType;
        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        var sanitizedBody = body;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("text", out var text))
            {
                var removedNonTextFields = HasNonTextFields(document.RootElement);
                sanitizedBody = BuildTextOnlyJsonBody(text);

                if (removedNonTextFields)
                {
                    logger.LogInformation("Removed non-text fields from resume rephrase request before forwarding downstream.");
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Resume rephrase request body was not valid JSON; forwarding unchanged for downstream validation.");
        }

        request.Content = BuildReplacementContent(sanitizedBody, originalContentType);
    }

    private static bool HasNonTextFields(JsonElement rootElement)
    {
        foreach (var property in rootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "text", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildTextOnlyJsonBody(JsonElement text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("text");
            text.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static StringContent BuildReplacementContent(string body, MediaTypeHeaderValue? originalContentType)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = originalContentType is null
            ? new MediaTypeHeaderValue("application/json")
            : MediaTypeHeaderValue.Parse(originalContentType.ToString());

        return content;
    }
}

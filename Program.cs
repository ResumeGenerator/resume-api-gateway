using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("ocelot.json", optional: false, reloadOnChange: true);

var jwtSettings = builder.Configuration.GetSection("Jwt");
var authority = Environment.GetEnvironmentVariable("JWT_AUTHORITY") ?? jwtSettings["Authority"];
var audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? jwtSettings["Audience"];
var issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? jwtSettings["Issuer"];
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

builder.Configuration["Routes:0:DownstreamScheme"] = parserUri.Scheme;
builder.Configuration["Routes:0:DownstreamHostAndPorts:0:Host"] = parserUri.Host;
builder.Configuration["Routes:0:DownstreamHostAndPorts:0:Port"] = parserUri.Port.ToString();
builder.Configuration["Routes:1:DownstreamScheme"] = templateApiUri.Scheme;
builder.Configuration["Routes:1:DownstreamHostAndPorts:0:Host"] = templateApiUri.Host;
builder.Configuration["Routes:1:DownstreamHostAndPorts:0:Port"] = templateApiUri.Port.ToString();
builder.Configuration["Routes:2:DownstreamScheme"] = authApiUri.Scheme;
builder.Configuration["Routes:2:DownstreamHostAndPorts:0:Host"] = authApiUri.Host;
builder.Configuration["Routes:2:DownstreamHostAndPorts:0:Port"] = authApiUri.Port.ToString();
builder.Configuration["Routes:3:DownstreamScheme"] = authApiUri.Scheme;
builder.Configuration["Routes:3:DownstreamHostAndPorts:0:Host"] = authApiUri.Host;
builder.Configuration["Routes:3:DownstreamHostAndPorts:0:Port"] = authApiUri.Port.ToString();
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
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(issuer),
            ValidateAudience = !string.IsNullOrWhiteSpace(audience),
            ValidateLifetime = true,
            ValidateIssuerSigningKey = false,
            ValidIssuer = issuer,
            ValidAudience = audience
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddOcelot(builder.Configuration);

var app = builder.Build();

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

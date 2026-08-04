using System.Reflection;
using System.Text;
using ERP.Application.Accounting.Commands.CreateJournalEntry;
using ERP.Persistence;
using ERP.Persistence.Repositories.Accounting;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Logging: Serilog replaces the default provider entirely, structured JSON in
// production, readable console in development. Configured from appsettings so
// log levels can be tuned per-environment without a redeploy.
// ---------------------------------------------------------------------------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "ERP.WebAPI"));

// ---------------------------------------------------------------------------
// Persistence: EF Core against SQL Server + all Accounting repositories.
// Later increments call the equivalent AddXxxRepositories() for each module.
// ---------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

builder.Services.AddErpPersistence(connectionString);
builder.Services.AddAccountingRepositories();

// ---------------------------------------------------------------------------
// Application layer: MediatR (CQRS dispatch) + FluentValidation, scanning the
// Application assembly so every Command/Query/Validator in every module
// registers automatically as new modules are added — no per-module DI wiring
// needed in Program.cs going forward.
// ---------------------------------------------------------------------------
var applicationAssembly = typeof(CreateJournalEntryCommand).Assembly;
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(applicationAssembly));
builder.Services.AddValidatorsFromAssembly(applicationAssembly);

// ---------------------------------------------------------------------------
// Authentication: JWT bearer tokens issued by the Security/Identity module
// (next increment). Configured here so every controller can already declare
// [Authorize] / [Authorize(Policy=...)] against a working pipeline.
// ---------------------------------------------------------------------------
var jwtSection = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSection["Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured.")))
        };
    });

// ---------------------------------------------------------------------------
// Authorization: fine-grained policies rather than blanket [Authorize], since
// an ERP's highest-risk operations (posting entries, approving payments) must
// be gated on specific permission claims, not just "is logged in". These
// policy names are the contract the Security module's permission seed data
// (next increment) must populate.
// ---------------------------------------------------------------------------
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Accounting.Post", policy => policy.RequireClaim("permission", "Accounting.Post"));
    options.AddPolicy("Accounting.ViewReports", policy => policy.RequireClaim("permission", "Accounting.ViewReports"));
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ERP System API",
        Version = "v1",
        Description = "Enterprise ERP — Financial Accounting, Inventory, Sales, Purchasing and more."
    });
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("ErpFrontend", policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>())
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
app.UseCors("ErpFrontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.WebEncoders;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Resources;
using SimplCommerce.Infrastructure;
using SimplCommerce.Infrastructure.Data;
using SimplCommerce.Infrastructure.Modules;
using SimplCommerce.Infrastructure.Web;
using SimplCommerce.Module.Core.Data;
using SimplCommerce.Module.Core.Extensions;
using SimplCommerce.Module.Localization.Extensions;
using SimplCommerce.Module.Localization.TagHelpers;
using SimplCommerce.WebHost.Extensions;

var builder = WebApplication.CreateBuilder(args);
ConfigureService();
var app = builder.Build();
Configure();
app.Run();

void ConfigureService() 
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    builder.Configuration.AddEntityFrameworkConfig(options =>
    {
        options.UseSqlServer(connectionString);
    });

    GlobalConfiguration.WebRootPath = builder.Environment.WebRootPath;
    GlobalConfiguration.ContentRootPath = builder.Environment.ContentRootPath;

    builder.Services.AddModules();
    builder.Services.AddCustomizedDataStore(builder.Configuration);
    builder.Services.AddCustomizedIdentity(builder.Configuration);
    builder.Services.AddHealthChecks();
    builder.Services.AddHttpClient();
    builder.Services.AddTransient(typeof(IRepository<>), typeof(Repository<>));
    builder.Services.AddTransient(typeof(IRepositoryWithTypedId<,>), typeof(RepositoryWithTypedId<,>));
    builder.Services.AddScoped<SlugRouteValueTransformer>();

    builder.Services.AddCustomizedLocalization();
    builder.Services.AddCustomizedMvc(GlobalConfiguration.Modules);
    builder.Services.Configure<RazorViewEngineOptions>(
        options => { options.ViewLocationExpanders.Add(new ThemeableViewLocationExpander()); });
    builder.Services.Configure<WebEncoderOptions>(options =>
    {
        options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All);
    });
    builder.Services.AddScoped<ITagHelperComponent, LanguageDirectionTagHelperComponent>();
    builder.Services.AddTransient<IRazorViewRenderer, RazorViewRenderer>();
    builder.Services.AddAntiforgery(options => options.HeaderName = "X-XSRF-Token");
    builder.Services.AddCloudscribePagination();
    builder.Services.ConfigureModules();

    builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "SimplCommerce API", Version = "v1" });
    });

    // --- Observability: Azure Monitor via OpenTelemetry ---
    // Exports request/dependency/exception telemetry + logs to Application Insights
    // when a connection string is configured (env APPLICATIONINSIGHTS_CONNECTION_STRING,
    // injected from a k8s Secret). No-ops when unset, so local dev runs unchanged.
    var appInsightsConnectionString =
        builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
        ?? builder.Configuration["ApplicationInsights:ConnectionString"];
    if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: "simplcommerce", serviceNamespace: "rnd-dev",
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)
                }))
            .UseAzureMonitor(options => options.ConnectionString = appInsightsConnectionString);
    }
}

void Configure()
    { 
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
        app.UseMigrationsEndPoint();
    }
    else
    {
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase),
            a => a.UseExceptionHandler("/Home/Error")
        );
        app.UseHsts();
    }

    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase),
        a => a.UseStatusCodePagesWithReExecute("/Home/ErrorWithCode/{0}")
    );

    app.UseHttpsRedirection();
    app.UseCustomizedStaticFiles(builder.Environment);
    app.UseRouting();
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "SimplCommerce API V1");
    });
    app.UseCookiePolicy();
    app.UseCustomizedIdentity();
    app.UseCustomizedRequestLocalization();
    app.UseEndpoints(endpoints =>
    {
        // Liveness/readiness probe target (AB#1). Literal route wins over the
        // catch-all slug route below; anonymous + cheap by default.
        endpoints.MapHealthChecks("/health");

        // --- Demo fault injection (rnd-devops operations demo) ---
        // Routed under /api so the MVC error page + status-code re-execution (both
        // scoped to non-/api paths above) leave the response untouched — callers and
        // App Insights see the raw 5xx, which is what Azure Monitor alerts fire on.
        // Toggle off with DemoFaults:Enabled=false (env DEMOFAULTS__ENABLED=false).
        if (builder.Configuration.GetValue("DemoFaults:Enabled", true))
        {
            endpoints.MapGet("/api/demo/status/{code:int}", (int code, ILogger<Program> logger) =>
            {
                logger.LogError("Demo fault: returning injected HTTP {StatusCode}", code);
                return Results.StatusCode(code);
            });
            endpoints.MapGet("/api/demo/throw", (ILogger<Program> logger) =>
            {
                logger.LogError("Demo fault: throwing an unhandled exception to produce a 500");
                throw new InvalidOperationException(
                    "Demo 500 — injected unhandled exception for the rnd-devops operations demo.");
            });
        }

        endpoints.MapDynamicControllerRoute<SlugRouteValueTransformer>("/{**slug}");
        endpoints.MapControllerRoute(
            name: "areas",
            pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");
        endpoints.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");
    });

    var moduleInitializers = app.Services.GetServices<IModuleInitializer>();
    foreach (var moduleInitializer in moduleInitializers)
    {
        moduleInitializer.Configure(app, builder.Environment);
    }
}

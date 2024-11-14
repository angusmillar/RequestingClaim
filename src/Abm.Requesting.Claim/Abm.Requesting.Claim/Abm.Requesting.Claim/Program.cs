using Abm.Requesting.Claim.ContentFormatters;
using Abm.Requesting.Claim.Extensions;
using Abm.Requesting.Claim.FhirSupport;
using Abm.Requesting.Claim.Operation;
using Abm.Requesting.Claim.Settings;
using FhirNavigator;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using Serilog.Core;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(path: "./application-start-.log", rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    Log.Information("Starting up application {Environment}", builder.Environment.IsDevelopment() ? "(Is Development Environment)" : string.Empty); 

    //Setup log provider
    Logger serilogConfiguration = new LoggerConfiguration()
        .WriteTo.Console()
        .ReadFrom.Configuration(builder.Configuration)
        .CreateLogger();
    
    builder.Services.AddSerilog(serilogConfiguration);
    
    // Application Settings --------------------------------------------------------------------------------------------
    builder.Services.AddOptions<WebAppSettings>()
        .Bind(builder.Configuration.GetSection(WebAppSettings.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();
    
    // Fhir Serialization/DeSerialization ------------------------------------------------------------------------------
    builder.Services.AddSingleton<IFhirJsonSerializersOptions, FhirJsonSerializersOptions>();
    builder.Services.AddSingleton<IFhirSerializationSupport, FhirSerializationSupport>();
    builder.Services.AddSingleton<IFhirDeSerializationSupport, FhirDeSerializationSupport>();
    
    // Fhir Support ----------------------------------------------------------------------------------------------------
    builder.Services.AddSingleton<IOperationOutcomeSupport, OperationOutcomeSupport>();
    
    // FHIR Navigator Service ------------------------------------------------------------------------------------------
    FhirNavigatorSettings fhirNavigatorSettings = builder.Configuration.GetRequiredSection(FhirNavigatorSettings.SectionName)
        .Get<FhirNavigatorSettings>() ?? throw new NullReferenceException($"No {FhirNavigatorSettings.SectionName} settings found!");

    builder.Services.AddFhirNavigator(settings =>
    {
        settings.FhirRepositories = fhirNavigatorSettings.FhirRepositories;
        settings.Proxy = fhirNavigatorSettings.Proxy;
    });
    
    // Fhir $Claim Operation -------------------------------------------------------------------------------------------
    builder.Services.AddScoped<IClaimOperation, ClaimOperation>();
    
    // Fhir Controller setup ------------------------------------------------------------------------------
    builder.Services.AddSingleton<IFhirJsonSerializersOptions, FhirJsonSerializersOptions>();
    
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | 
            ForwardedHeaders.XForwardedProto | 
            ForwardedHeaders.XForwardedHost | 
            ForwardedHeaders.XForwardedPrefix;
        
        KnownProxiesSettings? knownProxiesSettings = builder.Configuration
            .GetRequiredSection(KnownProxiesSettings.SectionName)
            .Get<KnownProxiesSettings>();
        
        knownProxiesSettings?.ProxyIpAddressOrHostName.ForEach((proxy) => 
            proxy.ResolveIp(errorMessage: $"Invalid configuration for section {KnownProxiesSettings.SectionName}, " +
                                          $"IP or Hostname: {proxy}")
                .ToList().ForEach((ip) => options.KnownProxies.Add(ip)));
    });
    
    builder.Services.AddControllers();
    builder.Services.AddMvcCore(config =>
    {
        config.InputFormatters.Clear();
        //config.InputFormatters.Add(new XmlFhirInputFormatter());
        config.InputFormatters.Add(new JsonFhirInputFormatter());

        config.OutputFormatters.Clear();
        //config.OutputFormatters.Add(new XmlFhirOutputFormatter());
        config.OutputFormatters.Add(new JsonFhirOutputFormatter());
        
        // And include our custom content negotiator filter to handle the _format parameter
        // (from the FHIR spec:  http://hl7.org/fhir/http.html#mime-type )
        // https://docs.microsoft.com/en-us/aspnet/core/mvc/controllers/filters
        config.Filters.Add(new FhirFormatParameterFilter());
    });
    
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

// Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();
    app.MapControllers();
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception");
}
finally
{
    Log.Information($"Shut down complete");
    Log.CloseAndFlush();
}
using AuraUpBack.Application;
using AuraUpBack.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, enableMonitoringService: false);

var host = builder.Build();
host.Run();

using Karry.BuildingBlocks.Observability;

var builder = Host.CreateApplicationBuilder(args);
builder.AddKarryObservability("karry-workers");

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);

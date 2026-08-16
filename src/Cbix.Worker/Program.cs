using Cbix.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Inject the clock rather than letting code reach for DateTime.Now / DateTimeOffset.Now.
// Tests substitute a FakeTimeProvider, and every recorded timestamp stays UTC.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

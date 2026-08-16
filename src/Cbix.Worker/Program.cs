using Cbix.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Every registration lives in AddCbixWorker so the real composition can be built and asserted on by
// tests without launching this executable, and so this file stays what a host entry point should be:
// three lines that create a builder, compose it, and run it. Credential resolution and validation
// happen inside that call, before Build(), so a misconfigured deployment dies here rather than on
// its first model call.
builder.AddCbixWorker();

var host = builder.Build();
host.Run();

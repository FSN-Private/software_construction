using Platform.Api.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;

var factory = new OperationIdFactory();
var generated = factory.Resolve(null);
Assert(generated.StartsWith("op_", StringComparison.Ordinal), "Generated operation IDs must use the v1 prefix.");
Assert(generated.Length == 35, "Generated operation IDs must be stable-length opaque values.");

const string supplied = "op_valid-operation-id_01";
Assert(factory.Resolve(supplied) == supplied, "Valid caller-supplied IDs must be retained.");
Assert(factory.Resolve("invalid") != "invalid", "Invalid caller-supplied IDs must be replaced.");

var failedRequestMeasurements = 0;
using (var listener = new MeterListener())
{
    listener.InstrumentPublished = (instrument, meterListener) =>
    {
        if (instrument.Meter.Name == "Platform.Api" && instrument.Name == "platform.api.requests")
        {
            meterListener.EnableMeasurementEvents(instrument);
        }
    };
    listener.SetMeasurementEventCallback<long>((_, _, _, _) => failedRequestMeasurements++);
    listener.Start();

    var middleware = new PlatformTelemetryMiddleware(
        _ => throw new InvalidOperationException("Synthetic downstream failure."),
        NullLogger<PlatformTelemetryMiddleware>.Instance,
        factory);
    var context = new DefaultHttpContext();
    await AssertThrowsAsync(() => middleware.InvokeAsync(context), "Middleware must preserve downstream failures.");
}
Assert(failedRequestMeasurements == 1, "A failed request must still produce one normalized request metric.");

Console.WriteLine("Platform API telemetry foundation tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task AssertThrowsAsync(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

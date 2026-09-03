using Platform.Api.Telemetry;

var factory = new OperationIdFactory();
var generated = factory.Resolve(null);
Assert(generated.StartsWith("op_", StringComparison.Ordinal), "Generated operation IDs must use the v1 prefix.");
Assert(generated.Length == 35, "Generated operation IDs must be stable-length opaque values.");

const string supplied = "op_valid-operation-id_01";
Assert(factory.Resolve(supplied) == supplied, "Valid caller-supplied IDs must be retained.");
Assert(factory.Resolve("invalid") != "invalid", "Invalid caller-supplied IDs must be replaced.");

Console.WriteLine("Platform API telemetry foundation tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

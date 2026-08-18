using System.Text.Json;
using CTilde.LanguageServer;
using StreamJsonRpc;

var formatter = new SystemTextJsonFormatter
{
    JsonSerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    },
};
var handler = new HeaderDelimitedMessageHandler(Console.OpenStandardOutput(), Console.OpenStandardInput(), formatter);
var server = new LanguageServer();
using var rpc = new JsonRpc(handler);
server.Attach(rpc);
rpc.AddLocalRpcTarget(server);
rpc.StartListening();
await rpc.Completion.ConfigureAwait(false);

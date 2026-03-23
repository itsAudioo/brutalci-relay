using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "5100";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var app = builder.Build();
app.UseWebSockets();

// Session storage: access code -> Session
var sessions = new ConcurrentDictionary<string, RelaySession>();

app.MapGet("/", () => "Brutalci Relay Server v1.0 - OK");

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connections only");
        return;
    }

    var ws = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] New WebSocket connection");

    await HandleConnectionAsync(ws, sessions);
});

Console.WriteLine("=== Brutalci Relay Server ===");
Console.WriteLine("Listening on http://0.0.0.0:5100");
Console.WriteLine("WebSocket endpoint: ws://YOUR_IP:5100/ws");

app.Run();

static async Task HandleConnectionAsync(WebSocket ws, ConcurrentDictionary<string, RelaySession> sessions)
{
    var buffer = new byte[1024 * 64];
    var msgBuf = new StringBuilder();
    string? myCode = null;
    string? myRole = null;

    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;

            msgBuf.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var json = msgBuf.ToString();
            msgBuf.Clear();

            RelayMsg? msg;
            try { msg = JsonSerializer.Deserialize<RelayMsg>(json, RelayMsg.Opts); }
            catch { continue; }
            if (msg == null) continue;

            switch (msg.Type)
            {
                case "Register":
                    // User registering with access code
                    if (string.IsNullOrEmpty(msg.AccessCode)) break;
                    myCode = msg.AccessCode.ToUpperInvariant();
                    myRole = "user";
                    var session = sessions.GetOrAdd(myCode, _ => new RelaySession());
                    session.UserSocket = ws;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] User registered: {myCode}");
                    break;

                case "Connect":
                    // Admin connecting to a user via access code
                    if (string.IsNullOrEmpty(msg.AccessCode)) break;
                    var code = msg.AccessCode.ToUpperInvariant();
                    myCode = code;
                    myRole = "admin";

                    if (sessions.TryGetValue(code, out var s) && s.UserSocket?.State == WebSocketState.Open)
                    {
                        s.AdminSocket = ws;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Admin connected to: {code}");

                        // Notify both sides
                        var connMsg = new RelayMsg { Type = "Connected" };
                        await SendJsonAsync(s.UserSocket, connMsg);
                        await SendJsonAsync(s.AdminSocket, connMsg);
                    }
                    else
                    {
                        await SendJsonAsync(ws, new RelayMsg { Type = "Error", Error = "Invalid or expired access code." });
                    }
                    break;

                case "Ping":
                    await SendJsonAsync(ws, new RelayMsg { Type = "Pong" });
                    break;

                default:
                    // Relay message to the other party
                    if (myCode != null && sessions.TryGetValue(myCode, out var relay))
                    {
                        var target = myRole == "user" ? relay.AdminSocket : relay.UserSocket;
                        if (target?.State == WebSocketState.Open)
                        {
                            await SendRawAsync(target, json);
                        }
                    }
                    break;
            }
        }
    }
    catch (WebSocketException) { }
    finally
    {
        // Clean up on disconnect
        if (myCode != null && sessions.TryGetValue(myCode, out var sess))
        {
            var disconnectMsg = new RelayMsg { Type = "Disconnected" };

            if (myRole == "user")
            {
                sess.UserSocket = null;
                if (sess.AdminSocket?.State == WebSocketState.Open)
                    await SendJsonAsync(sess.AdminSocket, disconnectMsg);
                // Remove session when user disconnects
                sessions.TryRemove(myCode, out _);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] User disconnected, session {myCode} removed");
            }
            else if (myRole == "admin")
            {
                sess.AdminSocket = null;
                if (sess.UserSocket?.State == WebSocketState.Open)
                    await SendJsonAsync(sess.UserSocket, disconnectMsg);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Admin disconnected from {myCode}");
            }
        }
    }
}

static async Task SendJsonAsync(WebSocket ws, RelayMsg msg)
{
    var json = JsonSerializer.Serialize(msg, RelayMsg.Opts);
    await SendRawAsync(ws, json);
}

static async Task SendRawAsync(WebSocket ws, string data)
{
    if (ws.State != WebSocketState.Open) return;
    var bytes = Encoding.UTF8.GetBytes(data);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

class RelaySession
{
    public WebSocket? UserSocket { get; set; }
    public WebSocket? AdminSocket { get; set; }
}

class RelayMsg
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("accessCode")]
    public string? AccessCode { get; set; }

    [JsonPropertyName("sender")]
    public string? Sender { get; set; }

    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

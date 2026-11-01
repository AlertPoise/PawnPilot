using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PawnPilot;

// Test executable only. No Unity references and no production capability switch.
internal static class ModSimulator
{
    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        Converters = { new ArgumentsConverter() }
    };

    internal static async Task<int> Run(string[] args)
    {
        int port = int.Parse(args[1]);
        if (port < 1 || port > 65535) throw new ArgumentException("Invalid fixture port");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('e', 64));
        var gate = new ActionGate();
        var watch = Stopwatch.StartNew();
        string epoch = null;
        int frame = 0, invocationFrame = -1, revision = 1, invocationCount = 0;
        string priorSignature = null;
        bool done = false;
        // Console.In is a synchronized reader whose ReadLineAsync can block synchronously
        // on a redirected pipe. This test-only input thread never accesses game objects.
        Task<string> stop = Task.Run(() => Console.ReadLine());
        Emit(new { source = "fixture", kind = "ready", driver = "managed_action_gate" });
        while (!done && watch.Elapsed < TimeSpan.FromSeconds(45))
        {
            frame++;
            bool invoked = invocationFrame >= 0;
            bool uiClosed = invoked && frame - invocationFrame >= 2;
            bool effectConfirmed = invoked && frame - invocationFrame >= 24;
            string signature = invoked + ":" + uiClosed + ":" + effectConfirmed;
            if (priorSignature != null && priorSignature != signature) revision++;
            priorSignature = signature;
            Snapshot state = State(revision, !uiClosed, invoked && !effectConfirmed, effectConfirmed);
            gate.Advance(epoch, state, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), watch.ElapsedMilliseconds,
                _ => { invocationFrame = frame; invocationCount++; Emit(new { source = "fixture", kind = "invoked", frame, invocationCount }); },
                (command, before, after) => effectConfirmed && !after.menu_open
                    ? VerificationDecision.Succeeded()
                    : VerificationDecision.Pending());

            // An exchange every ~150ms while the same managed action waits across 24 frames.
            if (frame % 3 == 1)
            {
                var exchange = new Exchange { schema_version = 2, session_id = "managed-fixture-session",
                    broker_epoch = epoch, state = state, progress = gate.CurrentProgress,
                    has_result = gate.PendingResult != null, result = gate.PendingResult };
                using var response = await client.PostAsync("/exchange", new StringContent(
                    JsonSerializer.Serialize(exchange, Json), Encoding.UTF8, "application/json"));
                string raw = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("fixture_exchange " + (int)response.StatusCode + " " + raw);
                var reply = JsonSerializer.Deserialize<Reply>(raw, Json);
                if (reply == null || reply.schema_version != 2) throw new InvalidOperationException("fixture_schema");
                if (epoch != null && epoch != reply.broker_epoch) gate.OnBrokerEpochChanged(reply.broker_epoch, state);
                epoch = reply.broker_epoch;
                if (reply.acknowledged_result_id != null)
                {
                    if (!gate.Acknowledge(reply.acknowledged_result_id)) throw new InvalidOperationException("fixture_ack");
                    Emit(new { source = "fixture", kind = "acknowledged", invocationCount, frame });
                }
                if (reply.has_command)
                {
                    gate.Accept(reply.command, state, epoch, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Emit(new { source = "fixture", kind = "accepted", id = reply.command.id, frame });
                }
            }
            if (stop.IsCompleted) done = true;
            if (!done) await Task.Delay(50);
        }
        Emit(new { source = "fixture", kind = "stopped", invocationCount, frame });
        return 0;
    }

    private static Snapshot State(int revision, bool menuOpen, bool updating, bool confirmed) => new()
    {
        schema_version = 2, world_epoch = "managed-fixture-world", revision = revision,
        version = "managed-fixture-world:" + revision, loaded = true, ready = true,
        not_ready_reason = null, phase = "MORNING", scene = "InventoryScene", cash = 550,
        menu_open = menuOpen, can_close_menu = menuOpen && !updating,
        interaction = new InteractionObservation { phase = confirmed ? "needs_agent" : updating ? "fixture_animation" : "menu",
            consistency = updating ? "updating" : "unverified", reason = "fixture_only", operable = !updating },
        business = new BusinessObservation(),
        legal_actions = menuOpen && !updating ? new List<LegalAction> { new() { action = "close_menu", arguments = new ActionArguments() } } : new()
    };

    private static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));

    private sealed class ArgumentsConverter : JsonConverter<ActionArguments>
    {
        public override ActionArguments Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any())
                throw new JsonException("Fixture only supports empty close_menu args");
            return new ActionArguments();
        }
        public override void Write(Utf8JsonWriter writer, ActionArguments value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteEndObject();
        }
    }
}

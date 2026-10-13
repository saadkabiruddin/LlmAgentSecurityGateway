using System.Collections.Concurrent;
using System.Text.Json;
using Gateway;

// AgentGuard: every agent tool call goes through here before it reaches BankApi.

var builder = WebApplication.CreateBuilder(args);
var bankUrl = builder.Configuration["BankApiUrl"] ?? "http://localhost:5100";
builder.Services.AddHttpClient("bank", c => c.BaseAddress = new Uri(bankUrl));

var app = builder.Build();

var sessions = new ConcurrentDictionary<string, Session>();
var audit = new AuditLog("audit.log");

app.MapGet("/", () => "AgentGuard gateway is running. POST /gateway/session, then POST /gateway/call");

// Called by the APP when the user sends a message (not by the LLM).
app.MapPost("/gateway/session", (SessionRequest req) =>
{
    if (!Enum.TryParse<GuardMode>(req.Mode, ignoreCase: true, out var mode))
        return Results.BadRequest(new { error = "mode must be off, rbac or full" });

    var role = Identity.RoleOf(req.CustomerId);
    if (role is null) return Results.BadRequest(new { error = $"unknown customer {req.CustomerId}" });

    var session = new Session(
        Guid.NewGuid().ToString("N"), req.CustomerId, role, req.UserRequest,
        TaskClassifier.Classify(req.UserRequest), mode);
    sessions[session.Id] = session;

    return Results.Ok(new { sessionId = session.Id, task = session.TaskNames, mode = mode.ToString() });
});

// Called by the AGENT for every tool call.
app.MapPost("/gateway/call", async (ToolCallRequest req, IHttpClientFactory factory) =>
{
    if (!sessions.TryGetValue(req.SessionId, out var s))
        return Results.Json(new { error = "unknown session" }, statusCode: 401);

    var callArgs = req.Args ?? new Dictionary<string, JsonElement>();
    var decision = Policy.Check(s, req.Tool, callArgs);

    audit.Write(new
    {
        time = DateTime.UtcNow, session = s.Id, customer = s.CustomerId, task = s.TaskNames,
        mode = s.Mode.ToString(), tool = req.Tool, args = callArgs,
        allowed = decision.Allowed, check = decision.Check, reason = decision.Reason,
    });
    Console.WriteLine(decision.Allowed
        ? $"[ALLOW] mode={s.Mode} task={s.TaskNames} tool={req.Tool}"
        : $"[BLOCK] mode={s.Mode} task={s.TaskNames} tool={req.Tool} - {decision.Check}: {decision.Reason}");

    if (!decision.Allowed)
        return Results.Json(new { blocked = true, error = $"Blocked by AgentGuard ({decision.Check}): {decision.Reason}" },
                            statusCode: 403);

    // Forward to BankApi. The customer id always comes from the session, never from the LLM.
    var bank = factory.CreateClient("bank");
    HttpResponseMessage resp;
    switch (req.Tool)
    {
        case "get_my_balance":
            resp = await bank.GetAsync($"/tools/balance?customerId={s.CustomerId}");
            break;
        case "get_my_transactions":
            var count = Math.Clamp(ArgReader.Int(callArgs, "count") ?? 5, 1, 50);
            resp = await bank.GetAsync($"/tools/transactions?customerId={s.CustomerId}&count={count}");
            break;
        case "get_all_customers":
            resp = await bank.GetAsync("/tools/customers");
            break;
        case "transfer_money":
            resp = await bank.PostAsJsonAsync("/tools/transfer", new
            {
                customerId = s.CustomerId,
                fromAccount = ArgReader.Str(callArgs, "from_account") ?? "",
                toAccount = ArgReader.Str(callArgs, "to_account") ?? "",
                amount = ArgReader.Dec(callArgs, "amount") ?? 0m,
            });
            break;
        default:
            return Results.BadRequest(new { error = $"unknown tool '{req.Tool}'" });
    }

    var body = await resp.Content.ReadAsStringAsync();
    return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
});

app.Run();

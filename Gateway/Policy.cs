using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gateway;

/// <summary>Off = forward everything, Rbac = role checks only, Full = role + task scope + argument checks.</summary>
public enum GuardMode { Off, Rbac, Full }

public enum TaskType { ViewBalance, ViewTransactions, Transfer, ListCustomers, General }

/// <summary>
/// One user request. Created by the app (not the LLM) when the user sends a message.
/// The agent only ever holds the session id; it never chooses the customer or role.
/// </summary>
public record Session(string Id, int CustomerId, string Role, string UserRequest, TaskType Task, GuardMode Mode);

public record Decision(bool Allowed, string Check, string Reason)
{
    public static Decision Allow(string check) => new(true, check, "ok");
    public static Decision Deny(string check, string reason) => new(false, check, reason);
}

public record SessionRequest(int CustomerId, string UserRequest, string Mode);
public record ToolCallRequest(string SessionId, string Tool, Dictionary<string, JsonElement>? Args);

/// <summary>Stand-in for real authentication: in production the role comes from the user's login token.</summary>
public static class Identity
{
    static readonly Dictionary<int, string> Roles = new()
    {
        [1] = "customer", [2] = "customer", [3] = "customer", [4] = "admin", [5] = "customer",
    };

    public static string? RoleOf(int customerId) => Roles.TryGetValue(customerId, out var r) ? r : null;
}

/// <summary>
/// Decides what kind of task the user asked for. It reads ONLY the user's own
/// message (trusted input), never tool output, so injected text cannot widen the task.
/// Deliberately simple keyword rules - a known limitation, see README.
/// </summary>
public static class TaskClassifier
{
    public static TaskType Classify(string userRequest)
    {
        var t = userRequest.ToLowerInvariant();
        if (Regex.IsMatch(t, @"\b(transfer|send|pay)\b")) return TaskType.Transfer;
        if (t.Contains("all customers") || t.Contains("customer list")) return TaskType.ListCustomers;
        if (Regex.IsMatch(t, "transaction|history|statement")) return TaskType.ViewTransactions;
        if (Regex.IsMatch(t, "balance|how much")) return TaskType.ViewBalance;
        return TaskType.General;
    }
}

public static class Policy
{
    // Check 1 + 2: which tools exist and which role each needs.
    static readonly Dictionary<string, string> RequiredRole = new()
    {
        ["get_my_balance"] = "customer",
        ["get_my_transactions"] = "customer",
        ["transfer_money"] = "customer",
        ["get_all_customers"] = "admin",
    };

    // Check 3: which tools each task actually needs.
    static readonly Dictionary<TaskType, HashSet<string>> TaskTools = new()
    {
        [TaskType.ViewBalance] = ["get_my_balance"],
        [TaskType.ViewTransactions] = ["get_my_transactions"],
        [TaskType.Transfer] = ["get_my_balance", "transfer_money"],
        [TaskType.ListCustomers] = ["get_all_customers"],
        [TaskType.General] = ["get_my_balance", "get_my_transactions"],
    };

    /// <summary>Deny by default: a call is allowed only if every check for the mode passes.</summary>
    public static Decision Check(Session s, string tool, Dictionary<string, JsonElement> args)
    {
        if (s.Mode == GuardMode.Off) return Decision.Allow("off");

        // 1. Tool registry
        if (!RequiredRole.TryGetValue(tool, out var needed))
            return Decision.Deny("registry", $"unknown tool '{tool}'");

        // 2. Role
        if (needed == "admin" && s.Role != "admin")
            return Decision.Deny("role", $"'{tool}' requires role admin, user has role {s.Role}");

        if (s.Mode == GuardMode.Rbac) return Decision.Allow("rbac");

        // 3. Task scope
        if (!TaskTools[s.Task].Contains(tool))
            return Decision.Deny("task_scope", $"'{tool}' is not needed for task {s.Task} (user asked: \"{s.UserRequest}\")");

        // 4. Argument provenance: money can only go where the USER said, in the amount the USER said.
        if (tool == "transfer_money")
        {
            var to = ArgReader.Str(args, "to_account");
            var amount = ArgReader.Dec(args, "amount");

            if (string.IsNullOrWhiteSpace(to) || !s.UserRequest.Contains(to, StringComparison.OrdinalIgnoreCase))
                return Decision.Deny("argument_provenance", $"destination '{to}' does not appear in the user's request");

            if (amount is null || !AmountsIn(s.UserRequest).Any(n => n == amount.Value))
                return Decision.Deny("argument_provenance", $"amount '{amount}' does not appear in the user's request");
        }

        return Decision.Allow("full");
    }

    /// <summary>Numbers in the user's message, ignoring account numbers like ACC-1001.</summary>
    static List<decimal> AmountsIn(string text)
    {
        var withoutAccounts = Regex.Replace(text, @"ACC-\d+", "", RegexOptions.IgnoreCase);
        var result = new List<decimal>();
        foreach (Match m in Regex.Matches(withoutAccounts, @"\d[\d,]*(\.\d+)?"))
        {
            if (decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                result.Add(d);
        }
        return result;
    }
}

/// <summary>Reads tool arguments the LLM sent, whatever JSON type it used.</summary>
public static class ArgReader
{
    public static string? Str(Dictionary<string, JsonElement> a, string key) =>
        a.TryGetValue(key, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;

    public static decimal? Dec(Dictionary<string, JsonElement> a, string key)
    {
        if (!a.TryGetValue(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static int? Int(Dictionary<string, JsonElement> a, string key)
    {
        var d = Dec(a, key);
        return d is null ? null : (int)d.Value;
    }
}

/// <summary>Append-only JSON-lines log of every decision.</summary>
public class AuditLog(string path)
{
    readonly object _lock = new();

    public void Write(object entry)
    {
        var line = JsonSerializer.Serialize(entry);
        lock (_lock) File.AppendAllText(path, line + Environment.NewLine);
    }
}

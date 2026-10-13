namespace BankApi;

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Role { get; set; } = "customer"; // "customer" or "admin"
}

public class Account
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string AccountNumber { get; set; } = "";
    public decimal Balance { get; set; }
}

public class Transaction
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }          // negative = money out
    public string Description { get; set; } = ""; // untrusted text: may contain a prompt injection
}

public record InjectionRequest(string Text);

public record TransferRequest(int CustomerId, string FromAccount, string ToAccount, decimal Amount);

namespace AutoMapping.Tests;

public enum Status { Active = 1, Disabled = 2 }
public enum StatusDto { Active = 10, Disabled = 20 }

public class Address
{
    public string City { get; set; } = "";
    public string? Street { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateTime Created { get; set; }
    public Address? Address { get; set; }
    public List<Order> Orders { get; set; } = new();
    public Status Status { get; set; }
    public int? Age { get; set; }
    public string Score { get; set; } = "0";
    public Dictionary<string, Order> OrdersByKey { get; set; } = new();
    public string GetNickname() => FirstName.ToLowerInvariant();
}

public class OrderDto
{
    public int Id { get; set; }
    public double Total { get; set; }
}

public class CustomerDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public DateTime Created { get; set; }
    public string? AddressCity { get; set; }
    public List<OrderDto> Orders { get; set; } = new();
    public string Status { get; set; } = "";
    public long? Age { get; set; }
    public int Score { get; set; }
    public Dictionary<string, OrderDto> OrdersByKey { get; set; } = new();
    public string Nickname { get; set; } = "";
}

public class CustomerSummaryDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string? AddressCity { get; set; }
    public long? Age { get; set; }
    public List<OrderDto> Orders { get; set; } = new();
}

public class EnumHolder { public Status Status { get; set; } }
public class EnumHolderDto { public StatusDto Status { get; set; } }

public record PersonRecord(int Id, string Name);
public class PersonSource { public int Id { get; set; } public string Name { get; set; } = ""; }

public class Animal { public string Name { get; set; } = ""; }
public class Dog : Animal { public int Tricks { get; set; } }
public class AnimalDto { public string Name { get; set; } = ""; }
public class DogDto : AnimalDto { public int Tricks { get; set; } }

public class Node { public string Name { get; set; } = ""; public Node? Next { get; set; } }
public class NodeDto { public string Name { get; set; } = ""; public NodeDto? Next { get; set; } }

public class FuzzySource
{
    public string CustomerName { get; set; } = "";
    public string EmailAddress { get; set; } = "";
    public int Quantity { get; set; }
}

public class FuzzyDest
{
    public string Customer_Name { get; set; } = "";
    public string Email { get; set; } = "";
    public int Quantity { get; set; }
}

public class FlagSource { public string Name { get; set; } = ""; public int Count { get; set; } }
public class FlagDest { public string Name { get; set; } = ""; public int Count { get; set; } public string Extra { get; set; } = ""; }

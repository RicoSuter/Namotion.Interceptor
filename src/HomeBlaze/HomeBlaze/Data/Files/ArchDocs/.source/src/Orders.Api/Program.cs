using Orders.Contracts;

var order = new Order("sample-order", 42m);
Console.WriteLine($"Orders API accepted {order.Id} with amount {order.Amount}.");

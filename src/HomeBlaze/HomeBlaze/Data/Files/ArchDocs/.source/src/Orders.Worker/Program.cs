using Orders.Contracts;

var order = new Order("sample-order", 42m);
Console.WriteLine($"Orders worker processed {order.Id}.");

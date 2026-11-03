using Azure.Messaging.ServiceBus;

namespace AzureWorkbench.Models;

public record MessageItem(ServiceBusReceivedMessage Message);

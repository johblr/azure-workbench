using Avalonia.Controls;
using AzureWorkbench.Models;

namespace AzureWorkbench.Views;

/// <summary>
/// Shell services that tab views use to work with each other. Implemented by <see cref="MainWindow"/>;
/// views accept it as an optional constructor argument so they also work standalone (e.g. in checks).
/// </summary>
public interface IWorkbench
{
    /// <summary>Service Bus entities the explorer has discovered so far (queues, topics, subscriptions), for target pickers.</summary>
    IReadOnlyList<QueueRef> KnownEntities { get; }

    /// <summary>Configuration tabs currently open, for compare/copy between apps.</summary>
    IReadOnlyList<AppConfigView> OpenConfigurations { get; }

    /// <summary>Shows Application Insights logs for an operation/trace ID in an open log tab (asks which one if several).</summary>
    void FindInLogs(string operationId, Control anchor);

    /// <summary>Writes a message to the window status bar.</summary>
    void Notify(string message);
}

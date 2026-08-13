using static ECAssistant.EColor;

namespace ECAssistant.UI;

/// <summary>
/// Console-based implementation of EGuiBase.
/// Wraps the existing EColor ANSI helpers — one place to change coloring strategy.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    public override void WriteLine(string text)
         => Console.WriteLine(text);

    public override void WriteLineColored(string coloredText)
         => Console.WriteLine(coloredText);

    public override void WriteRaw(string text)
         => Console.Write(text);

    public override void BlankLine()
         => Console.WriteLine();

    // ── User Input ────────────────────────────────

    public override string? PromptColored(string labelAndText)
     {
        Console.Write(labelAndText);
        return Console.ReadLine();
     }

    public override string? PromptRaw(string label)
      {
          Console.Write(label);
          return Console.ReadLine();
       }

    // ── v10.19.1: Notification-aware prompt ──────

    /// <summary>
    /// Prompt the user while monitoring for background agent notifications.
    /// Uses a background task to poll the notification queue while Console.ReadLine blocks.
    /// Notifications are displayed immediately as they arrive, then the prompt continues waiting.
    /// </summary>
    public override string? PromptWithNotifications(string label)
    {
        if (NotificationQueue == null)
            return PromptRaw(label);

        // Drain pending notifications before showing prompt
        DrainAndDisplayNotifications();

        // Show prompt label
        Console.Write(label);

        // Background task: poll for notifications while waiting for input
        var inputTask = Task.Run(() => Console.ReadLine());
        var notifTask = Task.Run(async () =>
        {
            while (!inputTask.IsCompleted)
            {
                // Wait up to 500ms for notifications
                var notifications = await NotificationQueue.WaitForNotificationsAsync(500);
                foreach (var n in notifications)
                {
                    // Display notification above the current prompt line
                    var color = n.Priority == Engine.NotificationPriority.Critical ? "\x1b[31m\x1b[1m"
                               : n.Priority == Engine.NotificationPriority.Warning ? "\x1b[33m\x1b[1m"
                               : "\x1b[36m";
                    // Move to new line, show notification, then re-show prompt
                    Console.WriteLine();
                    Console.WriteLine($"{color}{n.ToDisplayString()}\x1b[0m");
                    Console.Write(label); // Re-show prompt
                }
            }
        });

        // Fix #1: Observe notifTask exceptions to prevent unobserved task exceptions
        notifTask.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

        // Wait for user input (blocking — this is the console read thread)
        var result = inputTask.Result;

        // Ensure notifTask has completed (it exits when inputTask completes)
        try { notifTask.Wait(1000); } catch { }

        // Drain any remaining notifications after input
        DrainAndDisplayNotifications();

        return result;
    }

    private void DrainAndDisplayNotifications()
    {
        if (NotificationQueue == null || !NotificationQueue.HasPending) return;

        foreach (var n in NotificationQueue.DrainAll())
        {
            var color = n.Priority == Engine.NotificationPriority.Critical ? "\x1b[31m\x1b[1m"
                       : n.Priority == Engine.NotificationPriority.Warning ? "\x1b[33m\x1b[1m"
                       : "\x1b[36m";
            Console.WriteLine($"{color}{n.ToDisplayString()}\x1b[0m");
        }
    }

    // ── Status / Info (caller pre-styles) ─────────

    public override void InfoColored(string coloredText)
         => Console.WriteLine(coloredText);

    public override void WarningColored(string coloredText)
         => Console.WriteLine(coloredText);

    // ── Low-level raw (for inference streaming etc.) ─

    public override void WriteRawDirect(string text)
        => Console.Write(text);
}
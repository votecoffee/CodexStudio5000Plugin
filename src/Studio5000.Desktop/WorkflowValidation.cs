namespace Studio5000.Desktop;

// Off-screen checks do not enter Application.Run. Install its UI synchronization behavior explicitly
// so awaited HTTP calls resume on the thread owning the form and its native control handles.
internal static class WorkflowValidation
{
    internal static void Run(Func<Task> exercise)
    {
        Control.CheckForIllegalCrossThreadCalls = true;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var task = exercise(); var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Generated GUI workflow exceeded 45 seconds.");
            Application.DoEvents(); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult(); Application.DoEvents();
    }
}

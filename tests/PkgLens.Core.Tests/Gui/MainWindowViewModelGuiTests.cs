using PkgLens.Gui.ViewModels;

namespace PkgLens.Core.Tests.Gui;

public sealed class MainWindowViewModelGuiTests
{
    [Fact]
    public async Task RunOperationAsync_CancelOperationResetsBusyState()
    {
        var viewModel = new MainWindowViewModel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task running = viewModel.RunOperationAsync("Testing operation…", "Test failed", async (token, _) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.CanCancel);
        Assert.False(viewModel.IsNotBusy);

        viewModel.CancelOperation();

        Assert.Equal("Cancelling…", viewModel.BusyMessage);
        Assert.False(viewModel.CanCancel);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsNotBusy);
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunOperationAsync_WhileBusyDoesNotStartSecondOperation()
    {
        var viewModel = new MainWindowViewModel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int secondStarts = 0;

        Task first = viewModel.RunOperationAsync("First…", "First failed", async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.RunOperationAsync("Second…", "Second failed", (_, _) =>
        {
            Interlocked.Increment(ref secondStarts);
            return Task.CompletedTask;
        });

        Assert.Equal(0, secondStarts);
        Assert.Equal("First…", viewModel.BusyMessage);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

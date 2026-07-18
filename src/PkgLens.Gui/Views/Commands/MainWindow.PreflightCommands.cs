using System;
using System.Linq;
using System.Threading.Tasks;
using PkgLens.Core.Shared;

namespace PkgLens.Gui.Views;

public partial class MainWindow
{
    private async Task<bool> ConfirmPreflightAsync(string message, Func<OperationPreflightReport> inspect,
        bool forceShow = false)
    {
        OperationPreflightReport? report = null;
        await RunOperationAsync(message, "Preflight failed", async (token, _) =>
        {
            report = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return inspect();
            }, token);
        });
        if (report is null)
            return false;

        if (forceShow || _showOperationPreflight)
            return await new OperationPreflightDialog(report).ShowDialog<bool>(this);

        if (report.CanProceed)
            return true;

        string details = string.Join(Environment.NewLine,
            report.Checks.Where(check => check.Status == PreflightCheckStatus.Error)
                .Select(check => $"{check.Name}: {check.Details}"));
        Vm.ReportError("Operation preflight failed", new InvalidOperationException(details));
        return false;
    }
}

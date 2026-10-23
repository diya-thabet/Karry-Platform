using Karry.Tools.LedgerVerifier;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

return await LedgerVerifierCli.RunAsync(args, Console.Out, cancel.Token).ConfigureAwait(false);

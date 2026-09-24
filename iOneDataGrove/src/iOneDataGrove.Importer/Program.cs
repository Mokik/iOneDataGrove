using var cancellationSource = new CancellationTokenSource();

ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;

    if (!cancellationSource.IsCancellationRequested)
    {
        Console.Error.WriteLine(
            "Interruzione richiesta: completo l'operazione database corrente e arresto l'importazione...");
        cancellationSource.Cancel();
    }
};

Console.CancelKeyPress += cancelHandler;

try
{
    return await iOneDataGrove.Importer.ImporterApplication.RunAsync(
        args,
        cancellationSource.Token);
}
catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
{
    Console.Error.WriteLine("Importazione interrotta in modo controllato.");
    return 130;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

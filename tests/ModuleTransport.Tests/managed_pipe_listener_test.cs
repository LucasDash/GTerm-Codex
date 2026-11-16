using System.Buffers.Binary;
using System.IO.Pipes;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static Delegate MakeEventHandler(EventInfo eventInfo, Action<object?, object?> callback)
{
    Type delegateType = eventInfo.EventHandlerType ?? throw new InvalidOperationException("Event has no delegate type.");
    ParameterInfo[] signature = delegateType.GetMethod("Invoke")!.GetParameters();
    ParameterExpression[] parameters = signature
        .Select(p => Expression.Parameter(p.ParameterType, p.Name))
        .ToArray();
    MethodInfo callbackInvoke = callback.GetType().GetMethod("Invoke")!;
    MethodCallExpression body = Expression.Call(
        Expression.Constant(callback), callbackInvoke,
        Expression.Convert(parameters[0], typeof(object)),
        Expression.Convert(parameters[1], typeof(object)));
    return Expression.Lambda(delegateType, body, parameters).Compile();
}

static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken = default)
{
    int offset = 0;
    while (offset < buffer.Length)
    {
        int count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
        if (count == 0) throw new EndOfStreamException();
        offset += count;
    }
}

static byte[] MakeLogFrame()
{
    using MemoryStream payload = new();
    using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
    {
        writer.Write(7);
        writer.Write(2);
        writer.Write(Encoding.UTF8.GetBytes("module-test"));
        writer.Write((byte)0);
        writer.Write(new byte[] { 20, 40, 60, 255 });
        writer.Write(Encoding.UTF8.GetBytes("pipe-auth-ok"));
        writer.Write((byte)0);
    }

    byte[] body = payload.ToArray();
    byte[] frame = new byte[4 + body.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)body.Length);
    body.CopyTo(frame.AsSpan(4));
    return frame;
}

try
{
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("SKIP: named-pipe client PID validation requires Windows.");
        return 0;
    }

    Assembly gterm = Assembly.Load("GTerm");
    Type listenerType = gterm.GetType("GTerm.Listeners.TcpLogListener", throwOnError: true)!;
    Type transportType = gterm.GetType("GTerm.Listeners.ModuleTransportKind", throwOnError: true)!;
    object pipeTransport = Enum.Parse(transportType, "WindowsNamedPipe");

    int port = 27631 + (Environment.ProcessId % 1000);
    string pipeName = $"GTerm.Module.{port}";
    int validationCount = 0;
    Func<int?> expectedServerPid = () =>
        Interlocked.Increment(ref validationCount) == 1
            ? Environment.ProcessId + 123456
            : Environment.ProcessId;

    ConstructorInfo constructor = listenerType.GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [typeof(int), transportType, typeof(Func<int?>)],
        modifiers: null) ?? throw new InvalidOperationException("Could not locate listener constructor.");
    object listener = constructor.Invoke([port, pipeTransport, expectedServerPid]);

    TaskCompletionSource<Exception> rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource<Exception> oversizedCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource<bool> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource<bool> logReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    EventInfo errorEvent = listenerType.GetEvent("OnError")!;
    EventInfo connectedEvent = listenerType.GetEvent("OnConnected")!;
    EventInfo logEvent = listenerType.GetEvent("OnLog")!;
    errorEvent.AddEventHandler(listener, MakeEventHandler(errorEvent, (_, args) =>
    {
        Exception error = (Exception)args!.GetType().GetMethod("GetException")!.Invoke(args, null)!;
        if (error is UnauthorizedAccessException) rejected.TrySetResult(error);
        if (error is InvalidDataException) oversizedCommand.TrySetResult(error);
    }));
    connectedEvent.AddEventHandler(listener, MakeEventHandler(connectedEvent, (_, _) => connected.TrySetResult(true)));
    logEvent.AddEventHandler(listener, MakeEventHandler(logEvent, (_, _) => logReceived.TrySetResult(true)));

    using NamedPipeServerStream firstServer = new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    Task firstAccept = firstServer.WaitForConnectionAsync();
    listenerType.GetMethod("Start")!.Invoke(listener, null);
    await firstAccept.WaitAsync(TimeSpan.FromSeconds(5));
    Exception rejection = await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(rejection.Message.Contains("does not match", StringComparison.OrdinalIgnoreCase),
        "wrong pipe-server PID was not rejected with the expected diagnostic");
    Console.WriteLine("PASS: GTerm rejects a named-pipe server with the wrong PID");
    firstServer.Dispose();

    using NamedPipeServerStream authorizedServer = new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    Task authorizedAccept = authorizedServer.WaitForConnectionAsync();
    await authorizedAccept.WaitAsync(TimeSpan.FromSeconds(8));
    await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("PASS: GTerm accepts the named-pipe server matching the selected process PID");

    await authorizedServer.WriteAsync(MakeLogFrame());
    await authorizedServer.FlushAsync();
    await logReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("PASS: framed log messages are received after PID validation");

    Task<string> commandRead = Task.Run(async () =>
    {
        byte[] commandHeader = new byte[4];
        await ReadExactlyAsync(authorizedServer, commandHeader);
        uint commandLength = BinaryPrimitives.ReadUInt32LittleEndian(commandHeader);
        byte[] commandBody = new byte[checked((int)commandLength)];
        await ReadExactlyAsync(authorizedServer, commandBody);
        return Encoding.UTF8.GetString(commandBody);
    });
    Task write = (Task)listenerType.GetMethod("WriteMessage")!.Invoke(listener, ["bridge-test"])!;
    await write.WaitAsync(TimeSpan.FromSeconds(5));
    string command = await commandRead.WaitAsync(TimeSpan.FromSeconds(5));
    Check(command == "bridge-test", "command payload mismatch");
    Console.WriteLine("PASS: framed commands are sent only after PID validation");

    await (Task)listenerType.GetMethod("WriteMessage")!.Invoke(listener, [new string('x', 1024 * 1024 + 1)])!;
    Exception sizeError = await oversizedCommand.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(sizeError.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase),
        "oversized client command was not rejected with a clear size diagnostic");
    using (CancellationTokenSource noDataTimeout = new(TimeSpan.FromMilliseconds(250)))
    {
        try
        {
            await ReadExactlyAsync(authorizedServer, new byte[1], noDataTimeout.Token);
            throw new InvalidOperationException("oversized command unexpectedly wrote a partial frame");
        }
        catch (OperationCanceledException) { }
    }
    Console.WriteLine("PASS: oversized command is rejected before any frame bytes are written");

    Task<string[]> parallelReads = Task.Run(async () =>
    {
        string[] messages = new string[2];
        for (int i = 0; i < messages.Length; i++)
        {
            byte[] parallelHeader = new byte[4];
            await ReadExactlyAsync(authorizedServer, parallelHeader);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(parallelHeader);
            byte[] body = new byte[checked((int)length)];
            await ReadExactlyAsync(authorizedServer, body);
            messages[i] = Encoding.UTF8.GetString(body);
        }
        return messages;
    });
    Task parallelWriteA = (Task)listenerType.GetMethod("WriteMessage")!.Invoke(listener, ["parallel-a"])!;
    Task parallelWriteB = (Task)listenerType.GetMethod("WriteMessage")!.Invoke(listener, ["parallel-b"])!;
    await Task.WhenAll(parallelWriteA, parallelWriteB).WaitAsync(TimeSpan.FromSeconds(5));
    string[] parallelMessages = await parallelReads.WaitAsync(TimeSpan.FromSeconds(5));
    Check(parallelMessages.Contains("parallel-a") && parallelMessages.Contains("parallel-b"),
        "concurrent command writes were not preserved as independent frames");
    Console.WriteLine("PASS: concurrent command writes remain frame-aligned");

    Check(validationCount >= 2, "listener did not validate both pipe server processes");
    Console.WriteLine("Managed named-pipe listener checks passed");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

using SingPlus.Apps.Snake;

// No RuntimeKernel is loaded into the child. Only primitive game requests/results
// cross inherited anonymous pipes; stdout is reserved for this binary protocol.
using var input = new BinaryReader(Console.OpenStandardInput());
using var output = new BinaryWriter(Console.OpenStandardOutput());
try
{
    var dispatch = SnakeBindings.CreateDispatcher(new int[SnakeBindings.Width * SnakeBindings.Height]);
    output.Write(0x534E4B31);
    output.Write(Environment.ProcessId);
    output.Flush();
    long expected = 1;
    while (true)
    {
        var sequence = input.ReadInt64();
        var direction = input.ReadInt32();
        if (sequence != expected++ || direction is < -1 or > 5) return 2;
        if (direction == -1) return 0;
        var frame = dispatch(new SnakeMove(direction));
        output.Write(sequence);
        output.Write(frame.Cells0);
        output.Write(frame.Cells1);
        output.Write(frame.Cells2);
        output.Write(frame.Head);
        output.Write(frame.Food);
        output.Write(frame.Score);
        output.Write(frame.Status);
        output.Flush();
    }
}
catch (EndOfStreamException) { return 0; }
catch (IOException) { return 3; }
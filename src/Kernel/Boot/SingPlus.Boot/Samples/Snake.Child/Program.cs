using SingPlus.Apps.Snake;

// No RuntimeKernel is loaded into the child. Only primitive game requests/results
// cross inherited anonymous pipes; stdout is reserved for this binary protocol.
using BinaryReader input = new BinaryReader(Console.OpenStandardInput());
using BinaryWriter output = new BinaryWriter(Console.OpenStandardOutput());
try
{
    Func<SnakeMove, SnakeFrame> dispatch = SnakeBindings.CreateDispatcher(new int[SnakeBindings.Width * SnakeBindings.Height]);
    output.Write(0x534E4B31);
    output.Write(Environment.ProcessId);
    output.Flush();
    long expected = 1;
    while (true)
    {
        long sequence = input.ReadInt64();
        int direction = input.ReadInt32();
        if (sequence != expected++ || direction is < -1 or > 5) return 2;
        if (direction == -1) return 0;
        SnakeFrame frame = dispatch(new SnakeMove(direction));
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
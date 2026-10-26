using SingPlus.Kernel;

namespace SingPlus.Boot;

/// <summary>
/// Host-side presentation layer for the emulator. Boot stages keep writing through
/// <see cref="KernelConsole"/>; interactive and script output goes through this helper
/// so tables, colors and error markers stay consistent.
/// </summary>
public static class EmulatorConsole
{
    private static readonly object Gate = new();

    public static bool ColorsEnabled => !Console.IsOutputRedirected;

    public static void Info(string text) => Write(text, ConsoleColor.Gray);

    public static void Detail(string text) => Write(text, ConsoleColor.DarkGray);

    public static void Ok(string text) => Write(text, ConsoleColor.Green);

    public static void Warn(string text) => Write(text, ConsoleColor.Yellow);

    public static void Error(string text) => Write(text, ConsoleColor.Red);

    public static void Prompt()
    {
        if (!ColorsEnabled)
        {
            Console.Out.Write("sing+> ");
            return;
        }

        lock (Gate)
        {
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Out.Write("sing+> ");
            Console.ForegroundColor = previous;
        }
    }

    public static void Write(string text, ConsoleColor color)
    {
        lock (Gate)
        {
            if (!ColorsEnabled)
            {
                Console.Out.Write(text);
                return;
            }

            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Out.Write(text);
            Console.ForegroundColor = previous;
        }
    }

    /// <summary>Writes a boot-stage detail line that aligns under the "[boot] name ..." line.</summary>
    public static void StageDetail(string text) =>
        KernelConsole.Write($"\r\n    {text}\r\n".AsSpan());

    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (headers.Count == 0) return;

        int[] widths = new int[headers.Count];
        for (int column = 0; column < headers.Count; column++)
            widths[column] = headers[column].Length;

        foreach (IReadOnlyList<string> row in rows)
            for (int column = 0; column < row.Count && column < widths.Length; column++)
                widths[column] = Math.Max(widths[column], row[column].Length);

        WriteLine(string.Join("  ", headers.Select((header, column) => header.PadRight(widths[column]))), ConsoleColor.Cyan);
        WriteLine(string.Join("  ", widths.Select(static width => new string('-', width))), ConsoleColor.DarkGray);
        foreach (IReadOnlyList<string> row in rows)
        {
            string[] cells = new string[headers.Count];
            for (int column = 0; column < cells.Length; column++)
                cells[column] = (column < row.Count ? row[column] : string.Empty).PadRight(widths[column]);
            WriteLine(string.Join("  ", cells), ConsoleColor.Gray);
        }
    }

    public static void WriteLine(string text, ConsoleColor color) => Write(text + "\r\n", color);
}

using System.Text;
using SingPlus.Boot.Shell;

namespace SingPlus.Boot;

public static class EmulatorShell
{
    public static int RunScript(EmulatorSession session, string path)
    {
        try
        {
            using StreamReader reader = File.OpenText(path);
            int lineNumber = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    continue;

                EmulatorConsole.Detail($"[script:{lineNumber}] {line}\r\n");
                if (!TryExecute(session, line, out bool exitRequested))
                {
                    EmulatorConsole.Error($"[script] stopped at line {lineNumber}\r\n");
                    return 1;
                }
                if (exitRequested) return 0;
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            EmulatorConsole.Error($"[script] cannot read '{path}': {exception.Message}\r\n");
            return 1;
        }
    }

    public static void RunInteractive(EmulatorSession session)
    {
        EmulatorConsole.Info("SingNextOS emulator shell. Type 'help' for commands, 'exit' to stop.\r\n");
        while (true)
        {
            EmulatorConsole.Prompt();
            string? line = Console.ReadLine();
            if (line is null)
            {
                Console.Out.WriteLine();
                return;
            }

            if (TryExecute(session, line, out bool exitRequested) && exitRequested)
                return;
        }
    }

    private static bool TryExecute(EmulatorSession session, string line, out bool exitRequested)
    {
        exitRequested = false;
        if (!TryTokenize(line, out List<string>? arguments))
        {
            EmulatorConsole.Error("[emulator] unterminated quoted argument\r\n");
            return false;
        }
        if (arguments.Count == 0) return true;

        string name = arguments[0];
        if (string.Equals(name, "help", StringComparison.OrdinalIgnoreCase))
        {
            WriteHelp();
            return true;
        }
        if (string.Equals(name, "exit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "quit", StringComparison.OrdinalIgnoreCase))
        {
            exitRequested = true;
            return true;
        }
        if (!EmulatorCommands.TryFind(name, out EmulatorCommands.Entry? command))
        {
            EmulatorConsole.Error($"[emulator] unknown command '{name}'; type 'help' for commands\r\n");
            return false;
        }

        return command.Handler(session, arguments.Skip(1).ToArray());
    }

    private static void WriteHelp()
    {
        EmulatorConsole.Info("Emulator commands (use 'exit' or 'quit' to leave):\r\n");
        EmulatorConsole.Table(
            ["command", "description"],
            EmulatorCommands.All
                .Select(static command => (IReadOnlyList<string>)[command.Usage, command.Description])
                .ToArray());
    }

    private static bool TryTokenize(string line, out List<string> arguments)
    {
        arguments = [];
        StringBuilder current = new StringBuilder();
        char quote = '\0';
        bool tokenStarted = false;

        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                    continue;
                }
                if (character == '\\' && index + 1 < line.Length &&
                    (line[index + 1] == quote || line[index + 1] == '\\'))
                {
                    current.Append(line[++index]);
                    continue;
                }
                current.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                tokenStarted = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (tokenStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }
            }
            else
            {
                current.Append(character);
                tokenStarted = true;
            }
        }

        if (quote != '\0') return false;
        if (tokenStarted) arguments.Add(current.ToString());
        return true;
    }
}
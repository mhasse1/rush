using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;

namespace Rush;

/// <summary>
/// Custom PSHostUserInterface that intercepts all PowerShell output
/// and routes it through Rush's rendering.
/// </summary>
public class RushHostUI : PSHostUserInterface
{
    private readonly RushHostRawUI _rawUI = new();

    public override PSHostRawUserInterface RawUI => _rawUI;

    public override void Write(string value)
    {
        Console.Write(value);
    }

    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value)
    {
        var prevFg = Console.ForegroundColor;
        var prevBg = Console.BackgroundColor;
        Console.ForegroundColor = foregroundColor;
        Console.BackgroundColor = backgroundColor;
        Console.Write(value);
        Console.ForegroundColor = prevFg;
        Console.BackgroundColor = prevBg;
    }

    public override void WriteLine(string value)
    {
        Console.WriteLine(value);
    }

    public override void WriteErrorLine(string value)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine(value);
        Console.ForegroundColor = prev;
    }

    public override void WriteWarningLine(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = Theme.Current.Warning;
        Console.WriteLine($"warning: {message}");
        Console.ForegroundColor = prev;
    }

    public override void WriteVerboseLine(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine(message);
        Console.ForegroundColor = prev;
    }

    public override void WriteDebugLine(string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine($"debug: {message}");
        Console.ForegroundColor = prev;
    }

    public override void WriteProgress(long sourceId, ProgressRecord record)
    {
        // Minimal progress display for now
        if (record.PercentComplete >= 0)
        {
            Console.Write($"\r[{record.PercentComplete}%] {record.StatusDescription}");
            if (record.RecordType == ProgressRecordType.Completed)
                Console.WriteLine();
        }
    }

    public override string ReadLine()
    {
        return Console.ReadLine() ?? string.Empty;
    }

    public override SecureString ReadLineAsSecureString()
    {
        var secure = new SecureString();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && secure.Length > 0)
            {
                secure.RemoveAt(secure.Length - 1);
                Console.Write("\b \b");
            }
            else if (key.KeyChar != '\0')
            {
                secure.AppendChar(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return secure;
    }

    public override Dictionary<string, PSObject> Prompt(
        string caption, string message, System.Collections.ObjectModel.Collection<FieldDescription> descriptions)
    {
        var results = new Dictionary<string, PSObject>();
        if (!string.IsNullOrEmpty(caption)) WriteLine(caption);
        if (!string.IsNullOrEmpty(message)) WriteLine(message);

        foreach (var desc in descriptions)
        {
            Write($"{desc.Name}: ");
            var input = ReadLine();
            results[desc.Name] = PSObject.AsPSObject(input);
        }
        return results;
    }

    public override int PromptForChoice(
        string caption, string message,
        System.Collections.ObjectModel.Collection<ChoiceDescription> choices,
        int defaultChoice)
    {
        if (!string.IsNullOrEmpty(caption)) WriteLine(caption);
        if (!string.IsNullOrEmpty(message)) WriteLine(message);

        for (int i = 0; i < choices.Count; i++)
        {
            var marker = i == defaultChoice ? "*" : " ";
            WriteLine($"  [{marker}] {i}: {choices[i].Label} - {choices[i].HelpMessage}");
        }

        Write("Choice: ");
        var input = ReadLine();
        return int.TryParse(input, out var choice) ? choice : defaultChoice;
    }

    public override PSCredential PromptForCredential(
        string caption, string message, string userName, string targetName)
    {
        if (!string.IsNullOrEmpty(caption)) WriteLine(caption);
        if (!string.IsNullOrEmpty(message)) WriteLine(message);

        if (string.IsNullOrEmpty(userName))
        {
            Write("Username: ");
            userName = ReadLine();
        }

        Write("Password: ");
        var password = ReadLineAsSecureString();
        return new PSCredential(userName, password);
    }

    public override PSCredential PromptForCredential(
        string caption, string message, string userName, string targetName,
        PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
    {
        return PromptForCredential(caption, message, userName, targetName);
    }
}

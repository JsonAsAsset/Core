using System;
using System.Text.RegularExpressions;

using Core.Models.Enums;

namespace Core.Models.Plugins;

/* One line of build output, already classified so the view is only picking colours */
public partial class BuildLogLine
{
    public string Counter { get; private init; } = string.Empty;
    public string Text { get; private init; } = string.Empty;
    public EBuildLogKind Kind { get; private init; }

    public bool HasCounter => Counter.Length > 0;

    public bool IsProgress => Kind == EBuildLogKind.Progress;
    public bool IsWarning => Kind == EBuildLogKind.Warning;
    public bool IsError => Kind == EBuildLogKind.Error;
    public bool IsSuccess => Kind == EBuildLogKind.Success;
    public bool IsNotice => Kind == EBuildLogKind.Notice;

    /* UnrealBuildTool prefixes each compiled file with its position in the batch */
    [GeneratedRegex(@"^\[(\d+)/(\d+)\]\s*(.*)$")]
    private static partial Regex ProgressPattern { get; }

    /* Compiler diagnostics are "file(12): error C2065: ..." and toolchain ones
     * "ERROR: ...", both of which have to miss counts like "0 errors" */
    [GeneratedRegex(@"(?i)(^\s*(fatal\s+)?error\b|:\s*(fatal\s+)?error\b)")]
    private static partial Regex ErrorPattern { get; }

    [GeneratedRegex(@"(?i)(^\s*warning\b|:\s*warning\b)")]
    private static partial Regex WarningPattern { get; }

    public static BuildLogLine Parse(string raw)
    {
        var text = raw.TrimEnd();

        if (ProgressPattern.Match(text) is { Success: true } progress)
        {
            return new BuildLogLine
            {
                Counter = $"{progress.Groups[1].Value}/{progress.Groups[2].Value}",
                Text = progress.Groups[3].Value,
                Kind = EBuildLogKind.Progress
            };
        }

        return new BuildLogLine { Text = text, Kind = Classify(text) };
    }

    /* Lines the installer writes itself, rather than anything the toolchain said */
    public static BuildLogLine Notice(string text)
        => new() { Text = text, Kind = EBuildLogKind.Notice };

    public static BuildLogLine Outcome(string text, bool succeeded)
        => new() { Text = text, Kind = succeeded ? EBuildLogKind.Success : EBuildLogKind.Error };

    private static EBuildLogKind Classify(string text)
    {
        if (text.Contains("Result: Succeeded", StringComparison.Ordinal)) return EBuildLogKind.Success;
        if (text.Contains("Result: Failed", StringComparison.Ordinal)) return EBuildLogKind.Error;

        if (ErrorPattern.IsMatch(text)) return EBuildLogKind.Error;
        if (WarningPattern.IsMatch(text)) return EBuildLogKind.Warning;

        return EBuildLogKind.Normal;
    }
}

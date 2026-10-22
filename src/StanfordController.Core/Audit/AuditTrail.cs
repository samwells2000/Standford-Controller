using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StanfordController.Core.Audit;

/// <summary>What an audit record is about. Short, because they appear in a column.</summary>
public static class AuditActions
{
    public const string Changed = "Changed";
    public const string SetupApplied = "Setup applied";
    public const string Reset = "Reset";
    public const string Recalled = "Recalled from memory";
    public const string SavedToMemory = "Saved to memory";
    public const string Triggered = "Triggered";
    public const string Armed = "Armed";
    public const string Command = "Command";
    public const string Connected = "Connected";
    public const string Disconnected = "Disconnected";
    public const string ConnectionLost = "Connection lost";
    public const string SelfTest = "Self test";
    public const string AutoCalibration = "Auto calibration";
    public const string StatusCleared = "Status cleared";
    public const string InterfaceReset = "Interface reset";
    public const string LocalControl = "Local control";
    public const string RemoteControl = "Remote control";
    public const string Lock = "Interface lock";
    public const string Fault = "Fault";
    public const string NotResponding = "Not responding";
    public const string RespondingAgain = "Responding again";
    public const string SetupSaved = "Setup saved";
    public const string SetupKept = "Instrument settings kept";
    public const string InstrumentAdded = "Instrument added";
    public const string InstrumentEdited = "Instrument edited";
    public const string InstrumentRemoved = "Instrument removed";
    public const string ChannelLabelled = "Channel labelled";
}

/// <summary>One entry in the audit trail: who did what to which instrument, when, and why.</summary>
public sealed record AuditRecord
{
    public DateTimeOffset Time { get; init; }

    /// <summary>The computer account, e.g. LAB\jsmith.</summary>
    public string User { get; init; } = string.Empty;

    public string Computer { get; init; } = string.Empty;

    /// <summary>The instrument's saved name.</summary>
    public string Instrument { get; init; } = string.Empty;

    public string? Serial { get; init; }

    public string? Address { get; init; }

    public string Action { get; init; } = string.Empty;

    public string? Setting { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    /// <summary>The reason the user gave for the change, if any.</summary>
    public string? Reason { get; init; }

    public string? Details { get; init; }

    /// <summary>The program and version that made the record, e.g. "Tempo 1.0.21".</summary>
    public string Software { get; init; } = string.Empty;

    /// <summary>SHA-256 of this record and the hash of the record before it in the same file. Set when written.</summary>
    public string? Hash { get; init; }
}

/// <summary>The result of checking one audit trail file for altered, inserted or deleted records.</summary>
public sealed record AuditIntegrity(string File, int Records, bool IsIntact, int? FirstBadLine, string Message);

/// <summary>
/// The audit trail on disk: one CSV file per computer per month, one record per line. Every record ends with a SHA-256
/// hash of itself and of the record before it, so a record that is changed, inserted or deleted breaks the chain from
/// that point on and <see cref="Verify"/> finds it. Files are only ever appended to, and are marked read-only between
/// writes so they are not changed by accident. The files open in a spreadsheet.
/// </summary>
public sealed class AuditTrailStore(string folder)
{
    public const string FilePrefix = "Tempo audit trail ";
    public const string FileExtension = ".csv";
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    public static readonly IReadOnlyList<string> Columns =
    [
        "Time", "User", "Computer", "Instrument", "Serial number", "Address", "Action", "Setting",
        "Old value", "New value", "Reason", "Details", "Software", "Record hash",
    ];

    private const string FirstHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly string Header = string.Join(",", Columns);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary>e.g. "Tempo audit trail 2026-10 LABPC1.csv". Each computer writes its own file.</summary>
    public string FilePathFor(DateTimeOffset time, string computer) =>
        Path.Combine(Folder, $"{FilePrefix}{time.ToString("yyyy-MM", Inv)} {SafeFileName(computer)}{FileExtension}");

    /// <summary>The audit trail files in the folder, newest month first.</summary>
    public IReadOnlyList<string> Files() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder, FilePrefix + "*" + FileExtension).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>The months that have records, e.g. "2026-10", newest first.</summary>
    public IReadOnlyList<string> Months() =>
        Files().Select(MonthOf).OfType<string>().Distinct().OrderDescending(StringComparer.Ordinal).ToList();

    /// <summary>"2026-10" for "Tempo audit trail 2026-10 LABPC1.csv".</summary>
    public static string? MonthOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length >= FilePrefix.Length + 7 && name.StartsWith(FilePrefix, StringComparison.Ordinal)
            ? name.Substring(FilePrefix.Length, 7)
            : null;
    }

    /// <summary>Every record from every computer for one month, oldest first.</summary>
    public IReadOnlyList<AuditRecord> ReadMonth(string month) =>
        Files().Where(f => MonthOf(f) == month).SelectMany(ReadFile).OrderBy(r => r.Time).ToList();

    /// <summary>
    /// Appends a record to this computer's file for the record's month, chained to the last record in that file.
    /// Waits briefly if another program has the file open. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> if it cannot be written.
    /// </summary>
    public AuditRecord Append(AuditRecord record)
    {
        Directory.CreateDirectory(Folder);
        var path = FilePathFor(record.Time, record.Computer);
        AuditRecord written;
        using (var stream = OpenExclusive(path))
        {
            string previous;
            if (stream.Length == 0)
            {
                stream.Write(Encoding.UTF8.GetPreamble());
                Write(stream, Header + "\r\n");
                previous = FirstHash;
            }
            else
            {
                var last = ReadLastLine(stream, out var endsWithNewLine);
                previous = HashOf(last) ?? FirstHash;
                stream.Seek(0, SeekOrigin.End);
                if (!endsWithNewLine)
                {
                    // An earlier write was cut short: start the new record on a line of its own.
                    Write(stream, "\r\n");
                }
            }

            var body = string.Join(",", Fields(record).Select(Escape));
            var hash = ComputeHash(previous, body);
            Write(stream, body + "," + hash + "\r\n");
            stream.Flush(flushToDisk: true);
            written = record with { Hash = hash };
        }

        TrySetReadOnly(path, true);
        return written;
    }

    /// <summary>Reads the records in one file, skipping lines that are not complete records.</summary>
    public static IReadOnlyList<AuditRecord> ReadFile(string path)
    {
        var records = new List<AuditRecord>();
        foreach (var line in ReadLines(path).Skip(1))
        {
            var fields = SplitCsv(line);
            if (fields.Count != Columns.Count
                || !DateTimeOffset.TryParseExact(fields[0], TimeFormat, Inv, DateTimeStyles.None, out var time))
            {
                continue;
            }

            records.Add(new AuditRecord
            {
                Time = time,
                User = fields[1],
                Computer = fields[2],
                Instrument = fields[3],
                Serial = NullIfEmpty(fields[4]),
                Address = NullIfEmpty(fields[5]),
                Action = fields[6],
                Setting = NullIfEmpty(fields[7]),
                OldValue = NullIfEmpty(fields[8]),
                NewValue = NullIfEmpty(fields[9]),
                Reason = NullIfEmpty(fields[10]),
                Details = NullIfEmpty(fields[11]),
                Software = fields[12],
                Hash = NullIfEmpty(fields[13]),
            });
        }

        return records;
    }

    /// <summary>
    /// Checks that no record in the file has been changed, and none inserted or deleted before the last one, by
    /// recomputing the chain of hashes. (Deleting records from the end of a file leaves the chain intact.)
    /// </summary>
    public static AuditIntegrity Verify(string path)
    {
        var name = Path.GetFileName(path);
        var lines = ReadLines(path);
        if (lines.Count == 0 || lines[0] != Header)
        {
            return new AuditIntegrity(name, 0, false, 1, $"{name}: the first line is not the audit trail's column headings.");
        }

        var previous = FirstHash;
        var count = 0;
        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var hash = HashOf(line);
            if (hash is null || ComputeHash(previous, line[..^65]) != hash)
            {
                return new AuditIntegrity(name, count, false, i + 1,
                    $"{name}, line {i + 1}: this record has been changed, or a record before it has been added or removed.");
            }

            previous = hash;
            count++;
        }

        return new AuditIntegrity(name, count, true, null, $"{name}: all {count} records are intact.");
    }

    private static IEnumerable<string?> Fields(AuditRecord r) =>
    [
        r.Time.ToString(TimeFormat, Inv), r.User, r.Computer, r.Instrument, r.Serial, r.Address, r.Action, r.Setting,
        r.OldValue, r.NewValue, r.Reason, r.Details, r.Software,
    ];

    /// <summary>One line per record: line breaks become "; ". Text a spreadsheet would run as a formula is defused.</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        value = value.Replace("\r\n", "; ").Replace('\r', ' ').Replace("\n", "; ");
        if (value[0] == '-')
        {
            value = "−" + value[1..]; // a minus sign, not a hyphen-minus, so "-0.8 V" is not taken as a formula
        }
        else if (value[0] is '=' or '+' or '@' or '\t')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"']) >= 0 || value != value.Trim()
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    private static string ComputeHash(string previous, string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previous + "\n" + body)));

    /// <summary>The hash at the end of a record line, or null if the line does not end with one.</summary>
    private static string? HashOf(string line) =>
        line.Length > 65 && line[^65] == ',' && line[^64..].All(char.IsAsciiHexDigitLower) ? line[^64..] : null;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "computer" : safe;
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.UTF8.GetBytes(text));

    private static string ReadLastLine(FileStream stream, out bool endsWithNewLine)
    {
        var length = (int)Math.Min(stream.Length, 64 * 1024);
        var buffer = new byte[length];
        stream.Seek(-length, SeekOrigin.End);
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer);
        endsWithNewLine = text.EndsWith('\n');
        text = text.TrimEnd('\r', '\n');
        var start = text.LastIndexOf('\n');
        return start < 0 ? text.TrimStart('﻿') : text[(start + 1)..];
    }

    private static List<string> ReadLines(string path)
    {
        using var stream = Retry(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static FileStream OpenExclusive(string path)
    {
        TrySetReadOnly(path, false);
        return Retry(() => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    /// <summary>Another program (Tempo on this computer, a viewer, a backup) may have the file open for a moment.</summary>
    private static FileStream Retry(Func<FileStream> open)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return open();
            }
            catch (IOException ex) when (attempt < 20 && ex is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void TrySetReadOnly(string path, bool readOnly)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            var updated = readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
            if (updated != attributes)
            {
                File.SetAttributes(path, updated);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Marking the file read-only is a courtesy; the hash chain is what protects it.
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 OpenCue contributors.
// Original, deliberately limited data parser; it never executes APO commands.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenCue;

public sealed record CorrectionData(string NormalizedText, double PositiveGainBound,
    int FilterCount, double MaximumFrequency);

public static class CorrectionService
{
    private const int MaxBytes = 100 * 1024;
    private const int MaxCommands = 128;
    private const string Numeric = @"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?";
    private const RegexOptions Options = RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking;
    private static readonly Regex Preamp = new(
        @"^Preamp\s*:\s*(?<gain>" + Numeric + @")\s+dB\s*$", Options);
    private static readonly Regex Filter = new(
        @"^Filter(?:\s+[0-9]+)?\s*:\s*ON\s+(?<kind>PK|LS|HS|LSC|HSC)\s+Fc\s+(?<frequency>" +
        Numeric + @")\s+Hz\s+Gain\s+(?<gain>" + Numeric +
        @")\s+dB(?:\s+Q\s+(?<q>" + Numeric + @"))?\s*$", Options);
    private static readonly Regex Graphic = new(@"^GraphicEQ\s*:\s*(?<points>.+)$", Options);
    private static readonly Regex Pair = new(
        @"^\s*(?<frequency>" + Numeric + @")\s+(?<gain>" + Numeric + @")\s*$", Options);

    /// <summary>
    /// Accepts one optional non-positive Preamp and numeric ON PK/low/high shelf
    /// filters or GraphicEQ lines. FilterCount counts EQ commands, not graph
    /// points. PositiveGainBound ignores negative Preamp credit and sums each
    /// positive filter gain and each graph's maximum positive gain.
    /// </summary>
    public static CorrectionData Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            throw new InvalidDataException("The correction file exceeds the 100 KB limit.");
        if (text.IndexOf('\0') >= 0)
            throw new InvalidDataException("The correction contains a NUL character.");

        var commands = new List<string>();
        double positiveGain = 0, maximumFrequency = 0;
        int count = 0, parametricCount = 0, lineNumber = 0;
        bool hasPreamp = false;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            ++lineNumber;
            // There are no quoted values in this format. Discard all comments;
            // only reconstructed, validated ASCII data commands are returned.
            int comment = raw.IndexOf('#');
            var line = (comment >= 0 ? raw[..comment] : raw).Trim().TrimStart('\uFEFF').Trim();
            if (line.Length == 0) continue;

            var preamp = Preamp.Match(line);
            if (preamp.Success)
            {
                if (hasPreamp) throw Error(lineNumber, "Only one optional Preamp is supported.");
                double gain = Number(preamp.Groups["gain"].Value, -40, 0, "Preamp dB", lineNumber);
                commands.Add($"Preamp: {Format(gain)} dB");
                hasPreamp = true;
                continue;
            }

            var filter = Filter.Match(line);
            if (filter.Success)
            {
                var kind = filter.Groups["kind"].Value.ToUpperInvariant();
                double frequency = Number(filter.Groups["frequency"].Value, 10, 24000, "Frequency Hz", lineNumber);
                maximumFrequency = Math.Max(maximumFrequency, frequency);
                double gain = Number(filter.Groups["gain"].Value, -30, 18, "Gain dB", lineNumber);
                bool hasQ = filter.Groups["q"].Success;
                if (kind == "PK" && !hasQ)
                    throw Error(lineNumber, "A PK filter needs a Q value.");
                double q = hasQ ? Number(filter.Groups["q"].Value, 0.01,
                    kind == "PK" ? 100 : Math.Sqrt(0.5), "Q", lineNumber) : 0;
                // Restrict shelves to non-resonant Q: otherwise their response
                // can overshoot Gain and invalidate this simple headroom bound.
                commands.Add($"Filter {++parametricCount}: ON {kind} Fc {Format(frequency)} Hz Gain {Format(gain)} dB" +
                    (hasQ ? $" Q {Format(q)}" : string.Empty));
                positiveGain += Math.Max(0, gain);
            }
            else
            {
                var graphic = Graphic.Match(line);
                if (!graphic.Success)
                    throw Error(lineNumber, "Unsupported or malformed correction. Use numeric ON PK/LS/HS/LSC/HSC filters, GraphicEQ, and an optional non-positive Preamp. Routing, paths, plug-ins and expressions are not accepted.");
                var pieces = graphic.Groups["points"].Value.Split(';');
                int length = pieces.Length;
                if (length > 0 && string.IsNullOrWhiteSpace(pieces[^1])) --length;
                if (length < 2 || length > 2048)
                    throw Error(lineNumber, "GraphicEQ needs 2–2048 frequency/gain pairs.");
                var normalized = new string[length];
                double previousFrequency = 0, maxGain = 0;
                for (int i = 0; i < length; ++i)
                {
                    var pair = Pair.Match(pieces[i]);
                    if (!pair.Success) throw Error(lineNumber, "A GraphicEQ point needs exactly two numeric values: frequency and gain.");
                    double frequency = Number(pair.Groups["frequency"].Value, 10, 24000, "Frequency Hz", lineNumber);
                    double gain = Number(pair.Groups["gain"].Value, -30, 18, "Gain dB", lineNumber);
                    if (frequency <= previousFrequency)
                        throw Error(lineNumber, "GraphicEQ frequencies must be strictly increasing.");
                    previousFrequency = frequency;
                    maximumFrequency = Math.Max(maximumFrequency, frequency);
                    maxGain = Math.Max(maxGain, gain);
                    normalized[i] = $"{Format(frequency)} {Format(gain)}";
                }
                commands.Add("GraphicEQ: " + string.Join("; ", normalized));
                positiveGain += maxGain;
            }
            if (++count > MaxCommands) throw Error(lineNumber, "The correction exceeds 128 EQ commands.");
            if (positiveGain > 24)
                throw Error(lineNumber, "The correction's conservative positive-gain bound exceeds 24 dB. Reduce its boosts before importing.");
        }
        if (count == 0) throw new InvalidDataException("No supported EQ filters or GraphicEQ data were found.");
        return new CorrectionData("# Imported headphone correction: validated numeric EQ data.\r\n" +
            string.Join("\r\n", commands) + "\r\n", positiveGain, count, maximumFrequency);
    }

    private static double Number(string input, double min, double max, string label, int line)
    {
        if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value) || value < min || value > max)
            throw Error(line, $"{label} must be finite and between {Format(min)} and {Format(max)}.");
        return value;
    }

    private static string Format(double value) =>
        (value == 0 ? 0 : value).ToString("G", CultureInfo.InvariantCulture);
    private static InvalidDataException Error(int line, string message) => new($"Line {line}: {message}");
}

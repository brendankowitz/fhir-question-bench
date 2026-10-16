using System.Text;

namespace FhirAgentBench.Import;

/// <summary>RFC 4180 CSV: comma-separated, fields optionally double-quoted, quotes doubled inside quoted fields, which may span lines.</summary>
public static class Csv
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadRecords(string text)
    {
        var rows = ReadRows(text.TrimStart('﻿'));
        if (rows.Count == 0)
        {
            throw new InvalidDataException("The CSV is empty; expected a header row.");
        }

        var header = rows[0];
        var records = new List<IReadOnlyDictionary<string, string>>(rows.Count - 1);
        for (var i = 1; i < rows.Count; i++)
        {
            if (rows[i].Count != header.Count)
            {
                throw new InvalidDataException($"CSV record {i} has {rows[i].Count} fields; the header has {header.Count}.");
            }

            records.Add(header.Select((name, column) => (name, value: rows[i][column]))
                .ToDictionary(pair => pair.name, pair => pair.value, StringComparer.Ordinal));
        }

        return records;
    }

    private static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var position = 0;
        while (position < text.Length)
        {
            var c = text[position++];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (position < text.Length && text[position] == '"')
                {
                    field.Append('"');
                    position++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r' when position < text.Length && text[position] == '\n':
                    break;
                case '\r' or '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("The CSV ends inside a quoted field.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}

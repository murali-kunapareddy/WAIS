using System.Text;

namespace InviteApp;

public static class CsvHelper
{
    public static List<(string Honorific, string Name, string Phone, string Groups, string Notes)> ParseContactsCsv(string csvText)
    {
        var results = new List<(string Honorific, string Name, string Phone, string Groups, string Notes)>();
        if (string.IsNullOrWhiteSpace(csvText)) return results;

        var rows = ParseCsvRows(csvText);
        if (rows.Count == 0) return results;

        int startRow = 0;
        int colHonorific = 0, colName = 1, colPhone = 2, colGroups = 3, colNotes = 4;

        // Detect header row if present
        var firstRow = rows[0];
        bool hasHeader = firstRow.Any(cell =>
            cell.Equals("Phone", StringComparison.OrdinalIgnoreCase) ||
            cell.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
            cell.Equals("Honorific", StringComparison.OrdinalIgnoreCase) ||
            cell.Equals("Groups", StringComparison.OrdinalIgnoreCase) ||
            cell.Equals("Notes", StringComparison.OrdinalIgnoreCase) ||
            cell.Equals("Note", StringComparison.OrdinalIgnoreCase));

        if (hasHeader)
        {
            startRow = 1;
            colHonorific = -1;
            colName = -1;
            colPhone = -1;
            colGroups = -1;
            colNotes = -1;

            for (int i = 0; i < firstRow.Count; i++)
            {
                var h = firstRow[i].Trim();
                if (h.Equals("Honorific", StringComparison.OrdinalIgnoreCase) || h.Equals("Title", StringComparison.OrdinalIgnoreCase))
                    colHonorific = i;
                else if (h.Equals("Name", StringComparison.OrdinalIgnoreCase) || h.Equals("FullName", StringComparison.OrdinalIgnoreCase))
                    colName = i;
                else if (h.Equals("Phone", StringComparison.OrdinalIgnoreCase) || h.Equals("Mobile", StringComparison.OrdinalIgnoreCase) || h.Equals("Number", StringComparison.OrdinalIgnoreCase))
                    colPhone = i;
                else if (h.Equals("Groups", StringComparison.OrdinalIgnoreCase) || h.Equals("Group", StringComparison.OrdinalIgnoreCase) || h.Equals("Tags", StringComparison.OrdinalIgnoreCase))
                    colGroups = i;
                else if (h.Equals("Notes", StringComparison.OrdinalIgnoreCase) || h.Equals("Note", StringComparison.OrdinalIgnoreCase) || h.Equals("Remarks", StringComparison.OrdinalIgnoreCase) || h.Equals("Comment", StringComparison.OrdinalIgnoreCase) || h.Equals("Comments", StringComparison.OrdinalIgnoreCase))
                    colNotes = i;
            }
        }

        for (int r = startRow; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace)) continue;

            string GetCol(int idx) => idx >= 0 && idx < row.Count ? row[idx].Trim() : string.Empty;

            var honorific = GetCol(colHonorific);
            var name = GetCol(colName);
            var phone = GetCol(colPhone);
            var groups = GetCol(colGroups);
            var notes = GetCol(colNotes);

            // Fallback if 2 or 3 columns without explicit header
            if (!hasHeader && row.Count == 2)
            {
                honorific = string.Empty;
                name = row[0].Trim();
                phone = row[1].Trim();
                groups = string.Empty;
                notes = string.Empty;
            }
            else if (!hasHeader && row.Count == 3)
            {
                honorific = string.Empty;
                name = row[0].Trim();
                phone = row[1].Trim();
                groups = row[2].Trim();
                notes = string.Empty;
            }

            results.Add((honorific, name, phone, groups, notes));
        }

        return results;
    }

    public static string ExportContactsCsv(IEnumerable<Contact> contacts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Honorific,Name,Phone,Groups,Notes");
        foreach (var c in contacts)
        {
            sb.Append(EscapeCsvField(c.Honorific)).Append(',');
            sb.Append(EscapeCsvField(c.Name)).Append(',');
            sb.Append(EscapeCsvField(c.Phone)).Append(',');
            sb.Append(EscapeCsvField(c.Groups)).Append(',');
            sb.Append(EscapeCsvField(c.Notes)).AppendLine();
        }
        return sb.ToString();
    }

    private static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    private static List<List<string>> ParseCsvRows(string text)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        currentField.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentField.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    if (currentRow.Count > 1 || !string.IsNullOrWhiteSpace(currentRow[0]))
                    {
                        rows.Add(currentRow);
                    }
                    currentRow = new List<string>();
                }
                else
                {
                    currentField.Append(c);
                }
            }
        }

        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString());
            if (currentRow.Count > 1 || !string.IsNullOrWhiteSpace(currentRow[0]))
            {
                rows.Add(currentRow);
            }
        }

        return rows;
    }
}

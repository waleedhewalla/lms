using System.Globalization;
using System.Text;

namespace EduNexus.Api.Connectors;

/// <summary>
/// Reads a course list exported from any system. Columns (header row, any order, case ignored): code and title are required;
/// externalId (defaults to code), term, startsOn, endsOn (yyyy-MM-dd), enrolled (a number) and instructors (names separated by ';') are optional.
/// The whole file is rejected if any row is wrong, so a half-read export never replaces a good catalogue.
/// </summary>
public static class CourseCsv
{
    public const int MaxRows = 20_000;

    public sealed record Result(List<CatalogCourse> Courses, List<string> Errors);

    public static Result Parse(string csv)
    {
        var errors = new List<string>();
        var rows = Rows(csv);
        if (rows.Count == 0) return new([], ["The file is empty."]);
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        var (cCode, cTitle) = (Col("code"), Col("title"));
        if (cCode < 0 || cTitle < 0) return new([], ["The first row must name the columns, including 'code' and 'title'."]);
        var (cId, cTerm, cStart, cEnd, cEnrolled, cInst) = (Col("externalid"), Col("term"), Col("startson"), Col("endson"), Col("enrolled"), Col("instructors"));
        if (rows.Count - 1 > MaxRows) return new([], [$"More than {MaxRows} rows."]);

        var courses = new List<CatalogCourse>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            string Cell(int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
            var line = i + 1;
            var code = Cell(cCode); var title = Cell(cTitle);
            if (code.Length == 0 || title.Length == 0) { errors.Add($"Row {line}: code and title are required."); continue; }
            var id = Cell(cId).Length > 0 ? Cell(cId) : code;
            if (!seen.Add(id)) { errors.Add($"Row {line}: '{id}' appears twice."); continue; }
            if (code.Length > 100 || title.Length > 400 || id.Length > 200) { errors.Add($"Row {line}: a value is too long."); continue; }
            DateTimeOffset? start = null, end = null;
            if (Cell(cStart).Length > 0) { if (TryDate(Cell(cStart), out var d)) start = d; else errors.Add($"Row {line}: startsOn must be yyyy-MM-dd."); }
            if (Cell(cEnd).Length > 0) { if (TryDate(Cell(cEnd), out var d)) end = d; else errors.Add($"Row {line}: endsOn must be yyyy-MM-dd."); }
            int? enrolled = null;
            if (Cell(cEnrolled).Length > 0) { if (int.TryParse(Cell(cEnrolled), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) enrolled = n; else errors.Add($"Row {line}: enrolled must be a whole number."); }
            var names = Cell(cInst).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            courses.Add(new CatalogCourse(id, code, title, Cell(cTerm) is { Length: > 0 } t ? t : null, start, end, enrolled, names, true));
        }
        return new(courses, errors);
    }

    private static bool TryDate(string s, out DateTimeOffset d) =>
        DateTimeOffset.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d);

    /// <summary>RFC 4180: quoted cells may hold commas, doubled quotes and line breaks.</summary>
    private static List<List<string>> Rows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        text = text.TrimStart('﻿');
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"' when cell.Length == 0: quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; break;
                default: cell.Append(ch); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}

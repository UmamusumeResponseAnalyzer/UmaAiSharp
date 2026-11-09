using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace UmaAiSharp.Cli.Evidence;

public static class MasterAudit
{
    public static MasterInventory Read(string path, TextWriter rows)
    {
        using var connection = EvidenceAudit.OpenReadOnly(path);
        using var transaction = connection.BeginTransaction(deferred: true);
        var names = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }

        var tables = new List<MasterTable>();
        var domains = new List<MasterDomain>();
        var exported = 0L;
        foreach (var name in names)
        {
            var columns = new List<MasterColumn>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA table_info({Quote(name)})";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    columns.Add(new(reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.GetInt32(5)));
            }
            long count;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"SELECT count(*) FROM {Quote(name)}";
                count = (long)command.ExecuteScalar()!;
            }
            var domainColumns = columns.Where(column => IsMechanismColumn(name, column.Name)).ToArray();
            // These are source-data inventories, not a claim that every discovered table applies to Pioneer.
            var export = domainColumns.Length > 0 || name.StartsWith("single_mode_", StringComparison.Ordinal)
                || name.StartsWith("support_card_", StringComparison.Ordinal)
                || name.StartsWith("skill_", StringComparison.Ordinal)
                || name.StartsWith("succession_", StringComparison.Ordinal)
                || name.StartsWith("race_", StringComparison.Ordinal)
                || name is "chara_data" or "card_data";
            tables.Add(new(name, count, columns, export));
            foreach (var column in domainColumns)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"SELECT {Quote(column.Name)}, count(*) FROM {Quote(name)} GROUP BY {Quote(column.Name)} ORDER BY {Quote(column.Name)}";
                using var reader = command.ExecuteReader();
                var values = new List<ValueCount>();
                while (reader.Read())
                    values.Add(new(reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetInt64(1)));
                domains.Add(new(name, column.Name, "Uninterpreted", values));
            }
            if (!export) continue;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                var order = columns.Where(column => column.PrimaryKeyPosition > 0)
                    .OrderBy(column => column.PrimaryKeyPosition).Select(column => Quote(column.Name)).ToArray();
                command.CommandText = $"SELECT * FROM {Quote(name)} ORDER BY {string.Join(",", order.Length > 0 ? order : columns.Select(column => Quote(column.Name)))}";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.WriteLine(JsonSerializer.Serialize(new { table = name, row }, EvidenceJson.LineOptions));
                    exported++;
                }
            }
        }
        return new(tables, domains, exported);
    }

    public static bool IsMechanismColumn(string table, string column) =>
        column.Contains("condition", StringComparison.OrdinalIgnoreCase)
        || column.Contains("effect", StringComparison.OrdinalIgnoreCase)
        || (table.Contains("effect", StringComparison.OrdinalIgnoreCase)
            && (column == "type" || column.StartsWith("type_", StringComparison.Ordinal)))
        || column is "target_type" or "result_state" or "command_type";

    static string Quote(string name) => '"' + name.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

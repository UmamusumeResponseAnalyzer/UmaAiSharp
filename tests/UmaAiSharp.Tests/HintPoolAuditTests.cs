using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Microsoft.Data.Sqlite;
using UmaAiSharp.MCP.Evidence;

namespace UmaAiSharp.Tests;

public sealed class HintPoolAuditTests
{
    [Theory]
    [InlineData(false, 2, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 6, false)]
    public void RebuildsPoolFromRawExportAndRejectsContaminatedOrInvalidOutcomes(bool learnedOutcome, int level, bool learnedChange)
    {
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-Hint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "analysis-02"));
        try
        {
            var master = Path.Combine(directory, "master.sqlite");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = master, Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE skill_data(id INTEGER, group_id INTEGER, rarity INTEGER);
                    INSERT INTO skill_data VALUES(1,10,1),(2,10,2),(3,20,1);
                    CREATE TABLE single_mode_hint_gain(support_card_id INTEGER,hint_gain_type INTEGER,hint_group INTEGER,hint_value_1 INTEGER,hint_value_2 INTEGER);
                    INSERT INTO single_mode_hint_gain VALUES(7,0,0,1,0),(7,0,0,3,0),(7,1,1,1,6);
                    """;
                command.ExecuteNonQuery();
            }
            var before = JsonNode.Parse("""
                {"scenario_id":11,"single_mode_chara_id":1000,"turn":1,
                 "speed":100,"stamina":100,"power":100,"guts":100,"wiz":100,"skill_point":0,
                 "max_speed":1200,"max_stamina":1200,"max_power":1200,"max_guts":1200,"max_wiz":1200,
                 "skill_array":[{"skill_id":2}],"skill_tips_array":[],"support_card_array":[{"position":1,"support_card_id":7}]}
                """)!.AsObject();
            var after = (JsonObject)before.DeepClone();
            after["skill_tips_array"] = new JsonArray(new JsonObject { ["group_id"] = learnedOutcome ? 10 : 20, ["rarity"] = 1, ["level"] = level });
            if (learnedChange) after["skill_array"]!.AsArray().Add(new JsonObject { ["skill_id"] = 3 });
            var responseBefore = Response(before, new JsonArray(new JsonObject
            {
                ["event_id"] = 20099, ["chara_id"] = 0, ["story_id"] = 801000003,
                ["event_contents_info"] = new JsonObject { ["tips_training_partner_id"] = 1 }
            }));
            var responseAfter = Response(after, []);
            var request = MessagePackSerializer.ConvertFromJson("""
                {"single_mode_check_event_request_common":{"event_id":20099,"chara_id":0,"choice_number":0,"current_turn":1}}
                """);
            var actor = Hash(Encoding.UTF8.GetBytes("viewer\n42"));
            var rawPath = Path.Combine(directory, "hints.bin.gz");
            using (var file = File.Create(rawPath))
            using (var gzip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new BinaryWriter(gzip))
            {
                WriteCapture(101, "exec_command", "old", "chain", MessagePackSerializer.ConvertFromJson("{}"), responseBefore);
                WriteCapture(102, "check_event", "chain", "next", request, responseAfter);
                void WriteCapture(long id, string endpoint, string requestSid, string nextSid, byte[] q, byte[] a)
                {
                    var meta = JsonSerializer.SerializeToUtf8Bytes(new { id, endpoint = "/umamusume/single_mode_pioneer/" + endpoint, actor, requestSid, nextSid });
                    foreach (var part in new[] { meta, q, a }) { writer.Write(part.Length); writer.Write(part); }
                }
            }
            var locator = new { id = 102, preId = 101, cardId = 7, turn = 1, actor,
                runKey = Hash(Encoding.UTF8.GetBytes(actor + "\n1000")), responseHash = Hash(responseAfter), ordering = "SidChain" };
            var casesPath = Path.Combine(directory, "analysis-02", "rewards.jsonl");
            File.WriteAllText(casesPath, JsonSerializer.Serialize(locator) + "\n");
            var timeline = Path.Combine(directory, "timeline.bin.gz");
            File.WriteAllBytes(timeline, [1, 2, 3]);
            File.WriteAllText(Path.Combine(directory, "comparison-verified.json"), JsonSerializer.Serialize(new
            {
                inputs = new[] { rawPath, casesPath, timeline }.Select(path => new { path, sha256 = Hash(File.ReadAllBytes(path)) })
            }));
            var report = HintPoolAudit.Run(directory, master, Path.Combine(directory, "output"));
            if (learnedOutcome || level > 5 || learnedChange)
            {
                Assert.Equal(1, report.Counts["RejectedRewards"]);
                Assert.DoesNotContain("VerifiedRewards", report.Counts.Keys);
            }
            else
            {
                Assert.Equal(1, report.Counts["VerifiedRewards"]);
                Assert.Equal(1, report.Counts["LearnedOverlapRewards"]);
                Assert.Empty(report.Issues);
            }
            Assert.Throws<InvalidDataException>(() => HintPoolAudit.Run(directory, master, Path.Combine(directory, "output")));
        }
        finally { Directory.Delete(directory, true); }
    }

    static byte[] Response(JsonObject chara, JsonArray events) => MessagePackSerializer.ConvertFromJson(new JsonObject
    {
        ["data_headers"] = new JsonObject { ["viewer_id"] = 42, ["result_code"] = 1, ["servertime"] = 100 },
        ["data"] = new JsonObject { ["chara_info"] = chara, ["unchecked_event_array"] = events }
    }.ToJsonString());

    static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

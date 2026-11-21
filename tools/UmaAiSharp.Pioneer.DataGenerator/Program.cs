using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Base.Inheritance;
using UmaAiSharp.Pioneer.Runtime;

if (args.Length != 4)
{
    Console.Error.WriteLine("用法：UmaAiSharp.Pioneer.DataGenerator <master.mdb> <新输出文件.br> <turn-calibration.json> <inheritance-calibration.json>");
    return 2;
}

try
{
    var output = Path.GetFullPath(args[1]);
    if (File.Exists(output)) throw new IOException($"输出已存在，拒绝覆盖：{output}");
    var json = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) },
    };
    using var fitted = File.OpenRead(args[2]);
    var calibration = JsonSerializer.Deserialize<PioneerTurnCalibration>(fitted, json) ?? throw new InvalidDataException("回合校准文件为空。");
    using var fittedInheritance = File.OpenRead(args[3]);
    var inheritance = JsonSerializer.Deserialize<InheritanceCalibration>(fittedInheritance, json)
        ?? throw new InvalidDataException("继承校准文件为空。");
    var rules = PioneerMasterReader.ReadRules(args[0], calibration, inheritance);
    using var resource = new MemoryStream();
    rules.Write(resource);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    using (var file = new FileStream(output, FileMode.CreateNew)) resource.WriteTo(file);
    Console.WriteLine(output);
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or JsonException
    or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

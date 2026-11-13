using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Pioneer.Game;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.MCP.Evidence;

public sealed record TrainingFormulaSample(string Source, long PacketId, int Turn, int CommandId,
    TrainingFormulaInput Input, IReadOnlyList<int> CharacterEffects, int MasterFailureCoefficient, int FailureBias,
    IReadOnlyList<int> FailureReductions, int PredictedFailureRate, int ObservedFailureRate,
    TrainingFormulaPrediction? Prediction, TrainingValues ObservedCommon, TrainingValues? ObservedScenario,
    bool FailureMatches, bool? CommonMatches, bool? ScenarioMatches, IReadOnlyList<TrainingFormulaResidual> Residuals);
public sealed record TrainingFormulaResidual(int TargetType, int CommonResidual, int? ScenarioResidual,
    string? ObservedMinusExactNumerator, string? ObservedMinusExactDenominator);
public sealed record TrainingFormulaCoverage(string Dimension, string Value, int Count);
public sealed record TrainingFormulaAuditReport(string SchemaVersion, string GainCandidateVersion, string FailureRuleVersion,
    IReadOnlyList<SourceFingerprint> Inputs, IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<TrainingFormulaCoverage> Coverage, IReadOnlyList<EvidenceIssue> Issues,
    IReadOnlyList<string> ApplicabilityLimits, string GainStatus, string FailurePreviewStatus);

public static class TrainingFormulaAudit
{
    public static TrainingFormulaAuditReport Run(string inputDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observationPath = Path.Combine(inputDirectory, "pioneer-observations.jsonl");
        var exchangesPath = Path.Combine(inputDirectory, "exchanges.jsonl");
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new InvalidDataException("训练公式核验输出目录非空，拒绝覆盖已有证据。");
        var sources = new[] { (observationPath, "observations"), (exchangesPath, "exchanges"), (masterPath, "master") };
        var fingerprints = sources.Select(source => Fingerprint(source.Item1, source.Item2)).ToArray();
        var master = new PioneerMasterCompiler(PioneerMasterReader.ReadTraining(masterPath));
        var exchanges = File.ReadLines(exchangesPath).Select(line => JsonSerializer.Deserialize<ExchangeSummary>(line, EvidenceJson.ReadOptions)
            ?? throw new InvalidDataException("请求响应关联记录不能为null。")).ToDictionary(value => value.Key, StringComparer.Ordinal);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var coverage = new Dictionary<(string Dimension, string Value), int>();
        var issues = new List<EvidenceIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Count(string name) => counts[name] = counts.GetValueOrDefault(name) + 1;
        void Cover(string dimension, object value)
        {
            var key = (dimension, value.ToString()!);
            coverage[key] = coverage.GetValueOrDefault(key) + 1;
        }
        Directory.CreateDirectory(outputDirectory);
        using var samples = new StreamWriter(new FileStream(Path.Combine(outputDirectory, "training-formula-samples.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        using var counterexamples = new StreamWriter(new FileStream(Path.Combine(outputDirectory, "training-formula-counterexamples.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        foreach (var line in File.ReadLines(observationPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = JsonSerializer.Deserialize<PioneerObservation>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("育成观测不能为null。");
            Count("Observations");
            if (!exchanges.TryGetValue(observation.ExchangeKey, out var exchange) || exchange.Status != "Paired"
                || exchange.Endpoint != observation.Endpoint || !exchange.Responses.Contains(new(observation.Source, observation.PacketId)))
            {
                Count("ExchangeNotPaired");
                continue;
            }
            var common = PioneerNormalizer.GetCommon(observation.Visible, observation.Endpoint);
            if (common?["chara_info"] is not JsonObject chara || common["home_info"] is not JsonObject home
                || home["command_info_array"] is not JsonArray commands || observation.Visible["pioneer_data_set"] is not JsonObject pioneer)
            {
                Count("IncompleteFrame");
                continue;
            }
            foreach (var command in Objects(commands, "command_info_array"))
            {
                if (Int(command, "command_type") != 1 || Int(command, "is_enable") != 1
                    || command["params_inc_dec_info_array"] is not JsonArray) continue;
                Count("EnabledTrainingPreviews");
                try
                {
                    if (master.CharacterId(Int(chara, "card_id")) == 1007)
                        throw new UnknownMechanismException("黄金船需要本回合事件历史；请使用training-modes核验入口。");
                    var commandId = Int(command, "command_id");
                    var level = Int(command, "level");
                    if (!master.Trainings.TryGetValue((commandId, level), out var training))
                    {
                        Count("MissingCommandLevelMaster");
                        continue;
                    }
                    var characterEffects = Integers(Array(chara, "chara_effect_id_array"), "chara_effect_id_array");
                    if (characterEffects.Distinct().Count() != characterEffects.Length)
                        throw new InvalidDataException("chara_effect_id_array包含重复效果，拒绝重复计入失败率偏移。");
                    var bias = TrainingFailureRate.StatusBias(characterEffects);
                    var input = master.Input(chara, command, pioneer);
                    var resolved = input.Supports.Select(card => TrainingSupportEffects.Resolve(card,
                        training.BaseCommandId, level, input.Fans)).ToArray();
                    var reductions = resolved.SelectMany(card => card.FailureReductions).ToArray();
                    var failure = TrainingFailureRate.Calculate(training.FailureRate, input.Current.Vital, reductions, bias);
                    var observed = Values(command);
                    var scenarioCommand = Objects(Array(pioneer, "command_info_array"), "pioneer.command_info_array")
                        .SingleOrDefault(value => Int(value, "command_type") == 1 && Int(value, "command_id") == commandId);
                    var observedScenario = scenarioCommand?["params_inc_dec_info_array"] is JsonArray ? Values(scenarioCommand) : (TrainingValues?)null;
                    var key = JsonSerializer.Serialize(new { input, characterEffects, coefficient = training.FailureRate, bias,
                        observed, observedScenario, observedFailure = Int(command, "failure_rate") }, EvidenceJson.LineOptions);
                    if (!seen.Add(key)) { Count("RepeatedInputsAndOutputs"); continue; }
                    Count("UniquePreviews");
                    Count("FailureChecked");
                    var failureMatches = failure == Int(command, "failure_rate");
                    Count(failureMatches ? "FailureMatched" : "FailureMismatch");
                    TrainingFormulaPrediction? prediction = null;
                    if (commandId is 101 or 102 or 103 or 105 or 106)
                    {
                        prediction = TrainingFormulaCandidate.GenerateOrdinary(input);
                        Count("CommonChecked");
                        Count(prediction.Common == observed ? "CommonMatched" : "CommonMismatch");
                        if (observedScenario is { } upper)
                        {
                            Count("ScenarioChecked");
                            Count(prediction.Scenario == upper ? "ScenarioMatched" : "ScenarioMismatch");
                        }
                        else Count("ScenarioPreviewMissing");
                    }
                    else Count("NonOrdinaryGainNotImplemented");
                    var sample = new TrainingFormulaSample(observation.Source, observation.PacketId, Int(chara, "turn"), commandId,
                        input, characterEffects, training.FailureRate, bias, reductions, failure, Int(command, "failure_rate"),
                        prediction, observed, observedScenario, failureMatches, prediction is null ? null : prediction.Common == observed,
                        prediction is null || observedScenario is null ? null : prediction.Scenario == observedScenario,
                        prediction is null ? [] : Residuals(input, prediction, observed, observedScenario));
                    var serialized = JsonSerializer.Serialize(sample, EvidenceJson.LineOptions);
                    samples.WriteLine(serialized);
                    if (!failureMatches || sample.CommonMatches == false || sample.ScenarioMatches == false)
                        counterexamples.WriteLine(serialized);
                    Cover("CommandLevel", $"{commandId}:{level}");
                    Cover("Vital", input.Current.Vital);
                    Cover("Motivation", input.Motivation);
                    Cover("FailureRate", sample.ObservedFailureRate);
                    Cover("FailureBias", bias);
                    Cover("EvaluationTrainingPercent", input.EvaluationTrainingPercent);
                    Cover("GuestCount", input.GuestCount);
                    foreach (var effect in characterEffects) Cover("CharacterEffect", effect);
                    foreach (var support in input.Supports)
                    {
                        Cover("ParticipatingCardLevel", $"{support.CardId}:{support.Level}");
                        Cover("ParticipatingCardBond", $"{support.CardId}:{support.Bond}");
                    }
                }
                catch (Exception error) when (error is UnknownMechanismException or InvalidDataException or InvalidOperationException
                    or ArgumentException or OverflowException or KeyNotFoundException)
                {
                    Count("UnsupportedInput");
                    issues.Add(new("TrainingFormulaUnsupportedInput", observation.Source, observation.PacketId, error.Message));
                }
            }
        }
        foreach (var source in sources)
            if (Fingerprint(source.Item1, source.Item2).Sha256 != fingerprints.Single(value => value.Alias == source.Item2).Sha256)
                throw new InvalidDataException("核验期间输入文件改变，不能把本次输出作为稳定证据。");
        var report = new TrainingFormulaAuditReport("1", TrainingFormulaCandidate.Version, TrainingFailureRate.Version, fingerprints,
            counts, coverage.OrderBy(value => value.Key.Dimension, StringComparer.Ordinal).ThenBy(value => value.Key.Value, StringComparer.Ordinal)
                .Select(value => new TrainingFormulaCoverage(value.Key.Dimension, value.Key.Value, value.Value)).ToArray(), issues,
            ["LocalMasterAcceptedWithoutStrictResourceVersionBinding", "GainFormulaIsCandidateNotSimulationExecutionRule",
                "QuantizationSelectedOnFittingCorpusNotUniqueServerFormula", "ThisAuditChecksOrdinaryGainOnlyUseTrainingModesForAllModes",
                "GoldShipRequiresTrainingModesEventHistory", "KnownSupportUniqueTypesOnly", "CardPlacementDistributionNotImplemented", "FailurePreviewDoesNotValidateOutcomeProbabilityOrPenalty",
                "ObservedCoverageIsNotExhaustive", "ReferenceConstantsAndPacketDerivedCorrectionsAreDistinctEvidence"],
            counts.GetValueOrDefault("CommonChecked") == 0 ? "NoEligibleSamples"
                : counts.GetValueOrDefault("CommonMismatch") + counts.GetValueOrDefault("ScenarioMismatch") > 0 ? "CandidateCounterexamplesFound" : "CandidateObservedInputsMatched",
            counts.GetValueOrDefault("FailureChecked") == 0 ? "NoEligibleSamples"
                : counts.GetValueOrDefault("FailureMismatch") > 0 ? "CounterexamplesFound" : "ObservedDisplayRatesMatched");
        File.WriteAllText(Path.Combine(outputDirectory, "training-formula-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static TrainingFormulaResidual[] Residuals(TrainingFormulaInput input, TrainingFormulaPrediction prediction,
        TrainingValues observed, TrainingValues? scenario)
    {
        var observedValues = new[] { observed.Speed, observed.Stamina, observed.Power, observed.Guts, observed.Wisdom, observed.SkillPoints };
        var predictedValues = new[] { prediction.Common.Speed, prediction.Common.Stamina, prediction.Common.Power, prediction.Common.Guts, prediction.Common.Wisdom, prediction.Common.SkillPoints };
        var before = new[] { input.Current.Speed, input.Current.Stamina, input.Current.Power, input.Current.Guts, input.Current.Wisdom };
        var limits = new[] { input.Limits.Speed, input.Limits.Stamina, input.Limits.Power, input.Limits.Guts, input.Limits.Wisdom };
        var upper = scenario is { } value ? new[] { value.Speed, value.Stamina, value.Power, value.Guts, value.Wisdom, value.SkillPoints } : null;
        var predictedUpper = new[] { prediction.Scenario.Speed, prediction.Scenario.Stamina, prediction.Scenario.Power, prediction.Scenario.Guts, prediction.Scenario.Wisdom, prediction.Scenario.SkillPoints };
        return prediction.RawGains.Select((raw, index) =>
        {
            var unconverted = raw.TruncatedBeforeLimit < 100 && (index == 5
                || before[index] + Math.Max(raw.TruncatedBeforeLimit, observedValues[index]) < Math.Min(1200, limits[index]));
            return new TrainingFormulaResidual(raw.TargetType, observedValues[index] - predictedValues[index],
                upper is null ? null : upper[index] - predictedUpper[index], unconverted
                    ? (observedValues[index] * BigInteger.Parse(raw.Denominator) - BigInteger.Parse(raw.Numerator)).ToString() : null,
                unconverted ? raw.Denominator : null);
        }).ToArray();
    }

    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, alias == "master" ? "GameMasterSqlite" : "NormalizedPioneerEvidence",
            Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }

}

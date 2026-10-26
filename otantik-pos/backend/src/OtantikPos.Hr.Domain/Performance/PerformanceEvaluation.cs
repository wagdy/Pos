using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Performance;

// One KPI's score in an evaluation, 1 to 5, with the KPI's name as it was then.
public sealed class KpiScore
{
    private KpiScore() { }

    internal KpiScore(Guid kpiId, string kpiName, int score)
    {
        KpiId = kpiId;
        KpiName = kpiName;
        Score = score;
    }

    public Guid KpiId { get; private set; }
    public string KpiName { get; private set; } = string.Empty;
    public int Score { get; private set; }
}

// A manager's evaluation of an employee (تقييم أداء) on a day: a score of 1 to 5 for each KPI it
// takes, and its total, their average as a percent (all fives, 100; all threes, 60), kept as
// worked out then. A record of the employee's: kept as made.
public sealed class PerformanceEvaluation : AggregateRoot
{
    public const int MinScore = 1;
    public const int MaxScore = 5;
    public const int MaxNotesLength = 1000;

    private readonly List<KpiScore> _scores = [];

    private PerformanceEvaluation() { }

    public PerformanceEvaluation(
        Guid id, Guid employeeId, DateOnly date, IEnumerable<(PerformanceKpi Kpi, int Score)> scores, string? notes, string reviewerId, string reviewerName)
    {
        var given = scores.ToList();
        if (given.Count == 0)
            throw new DomainException("Score at least one KPI.");
        if (given.Select(s => s.Kpi.Id).Distinct().Count() != given.Count)
            throw new DomainException("Each KPI is scored once.");
        if (given.FirstOrDefault(s => !s.Kpi.IsActive) is { Kpi: { } retired })
            throw new DomainException($"{retired.Name} is retired: it is not scored any more.");
        if (given.Any(s => s.Score is < MinScore or > MaxScore))
            throw new DomainException($"A KPI is scored {MinScore} to {MaxScore}.");
        if (string.IsNullOrWhiteSpace(reviewerId))
            throw new DomainException("Say who evaluated them.");
        var trimmed = notes?.Trim();
        if (trimmed is { Length: > MaxNotesLength })
            throw new DomainException($"Keep the notes under {MaxNotesLength} characters.");

        Id = id;
        EmployeeId = employeeId;
        Date = date;
        _scores.AddRange(given.Select(s => new KpiScore(s.Kpi.Id, s.Kpi.Name, s.Score)));
        TotalScore = Math.Round((decimal)given.Average(s => s.Score) / MaxScore * 100, 1, MidpointRounding.AwayFromZero);
        Notes = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        ReviewerId = reviewerId;
        ReviewerName = reviewerName;
        RecordedAtUtc = DateTime.UtcNow;
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public IReadOnlyList<KpiScore> Scores => _scores;
    // A percent, to one decimal place.
    public decimal TotalScore { get; private set; }
    public string? Notes { get; private set; }
    // The manager's till account, and their name.
    public string ReviewerId { get; private set; } = string.Empty;
    public string ReviewerName { get; private set; } = string.Empty;
    public DateTime RecordedAtUtc { get; private set; }
}

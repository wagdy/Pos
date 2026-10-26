using Otantik.BuildingBlocks;
using OtantikPos.Hr.Domain.Performance;

namespace Otantik.Domain.Tests;

// Performance & Disciplinary (تقييم الأداء والإنذارات): evaluations scored on a manager's KPIs,
// and disciplinary actions, a penalty deduction among them reaching the payslip through
// Corrections.
public class PerformanceTests
{
    private static readonly Guid Hassan = Guid.NewGuid();
    private static readonly DateOnly Monday = new(2026, 10, 26);

    private static readonly PerformanceKpi Punctuality = new(Guid.NewGuid(), " Punctuality ");
    private static readonly PerformanceKpi Hygiene = new(Guid.NewGuid(), "Hygiene");
    private static readonly PerformanceKpi Service = new(Guid.NewGuid(), "Service");

    private static DisciplinaryAction Action(SeverityLevel level, decimal? amount = null, string violation = "Negligence") =>
        new(Guid.NewGuid(), Hassan, Monday, violation, level, "Left the walk-in open", amount, "staff-omar", "Omar");

    [Fact]
    public void A_kpi_is_named_and_retired_rather_than_deleted()
    {
        Assert.Equal(("Punctuality", true), (Punctuality.Name, Punctuality.IsActive));
        Assert.Throws<DomainException>(() => new PerformanceKpi(Guid.NewGuid(), " "));
        Assert.Throws<DomainException>(() => new PerformanceKpi(Guid.NewGuid(), new string('x', PerformanceKpi.MaxNameLength + 1)));
        var teamwork = new PerformanceKpi(Guid.NewGuid(), "Teamwork");
        teamwork.Retire();
        Assert.False(teamwork.IsActive);
        teamwork.Restore();
        Assert.True(teamwork.IsActive);
    }

    [Fact]
    public void An_evaluation_scores_each_kpi_1_to_5_and_totals_their_average_as_a_percent()
    {
        var evaluation = new PerformanceEvaluation(
            Guid.NewGuid(), Hassan, Monday, [(Punctuality, 5), (Hygiene, 4), (Service, 3)], "  Good month  ", "staff-omar", "Omar");
        Assert.Equal((80.0m, "Good month", "staff-omar", "Omar"), (evaluation.TotalScore, evaluation.Notes, evaluation.ReviewerId, evaluation.ReviewerName));
        Assert.Equal([("Punctuality", 5), ("Hygiene", 4), ("Service", 3)], evaluation.Scores.Select(s => (s.KpiName, s.Score)));
        Assert.Equal(66.7m, new PerformanceEvaluation(Guid.NewGuid(), Hassan, Monday, [(Punctuality, 4), (Hygiene, 3), (Service, 3)], null, "staff-omar", "Omar").TotalScore);

        Assert.Throws<DomainException>(() => new PerformanceEvaluation(Guid.NewGuid(), Hassan, Monday, [], null, "staff-omar", "Omar"));
        Assert.Throws<DomainException>(() => new PerformanceEvaluation(Guid.NewGuid(), Hassan, Monday, [(Punctuality, 6)], null, "staff-omar", "Omar"));
        Assert.Throws<DomainException>(() => new PerformanceEvaluation(Guid.NewGuid(), Hassan, Monday, [(Punctuality, 0)], null, "staff-omar", "Omar"));
        Assert.Equal("Each KPI is scored once.",
            Assert.Throws<DomainException>(() => new PerformanceEvaluation(Guid.NewGuid(), Hassan, Monday, [(Punctuality, 3), (Punctuality, 4)], null, "staff-omar", "Omar")).Message);
        var retired = new PerformanceKpi(Guid.NewGuid(), "Teamwork");
        retired.Retire();
        Assert.Contains("retired", Assert.Throws<DomainException>(() => new PerformanceEvaluation(Guid.NewGuid(), Hassan, Monday, [(retired, 3)], null, "staff-omar", "Omar")).Message);
        Assert.Throws<DomainException>(() => new PerformanceEvaluation(Guid.NewGuid(), Hassan, Monday, [(Punctuality, 3)], null, " ", "Omar"));
    }

    [Fact]
    public void Only_a_penalty_deduction_has_an_amount_and_it_makes_its_own_penalty_on_its_day()
    {
        var warning = Action(SeverityLevel.FirstWritten);
        Assert.True(warning.IsWarning);
        Assert.Null(warning.PenaltyAmount);
        Assert.Null(warning.Penalty());
        Assert.Equal("Only a penalty deduction has an amount.", Assert.Throws<DomainException>(() => Action(SeverityLevel.SecondWritten, 100)).Message);
        Assert.Equal("A penalty deduction has its amount.", Assert.Throws<DomainException>(() => Action(SeverityLevel.PenaltyDeduction)).Message);
        Assert.Throws<DomainException>(() => Action(SeverityLevel.PenaltyDeduction, 0));
        Assert.Throws<DomainException>(() => Action(SeverityLevel.PenaltyDeduction, 10.255m));
        Assert.Throws<DomainException>(() => Action(SeverityLevel.VerbalWarning, violation: " "));
        Assert.Throws<DomainException>(() => Action((SeverityLevel)9));

        var deduction = Action(SeverityLevel.PenaltyDeduction, 250, " Absence ");
        Assert.False(deduction.IsWarning);
        var penalty = deduction.Penalty()!;
        Assert.Equal((Hassan, Monday, 0m, (decimal?)250m, (Guid?)deduction.Id, "Penalty deduction: Absence", "Omar"),
            (penalty.EmployeeId, penalty.Date, penalty.Hours, penalty.Amount, penalty.DisciplinaryActionId, penalty.Reason, penalty.SetBy));

        var termination = Action(SeverityLevel.Termination, violation: "Theft");
        Assert.Null(termination.Penalty());
    }

    [Fact]
    public void An_employees_record_counts_each_level_and_says_which_comes_next()
    {
        var none = DisciplinaryRecord.Of([]);
        Assert.Equal((0, SeverityLevel.VerbalWarning), (none.Warnings, none.Next));

        var verbal = DisciplinaryRecord.Of([Action(SeverityLevel.VerbalWarning)]);
        Assert.Equal(SeverityLevel.FirstWritten, verbal.Next);
        var written = DisciplinaryRecord.Of([Action(SeverityLevel.VerbalWarning), Action(SeverityLevel.FirstWritten)]);
        Assert.Equal((2, SeverityLevel.SecondWritten), (written.Warnings, written.Next));
        var second = DisciplinaryRecord.Of([Action(SeverityLevel.VerbalWarning), Action(SeverityLevel.FirstWritten), Action(SeverityLevel.SecondWritten)]);
        Assert.Equal((3, 1, SeverityLevel.PenaltyDeduction), (second.Warnings, second.SecondWritten, second.Next));
        var penalised = DisciplinaryRecord.Of([Action(SeverityLevel.PenaltyDeduction, 100), Action(SeverityLevel.Termination)]);
        Assert.Equal((0, 1, 1, SeverityLevel.PenaltyDeduction), (penalised.Warnings, penalised.PenaltyDeductions, penalised.Terminations, penalised.Next));
    }
}

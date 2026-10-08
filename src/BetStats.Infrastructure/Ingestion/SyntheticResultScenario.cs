using System.Globalization;
using BetStats.Application.Ingestion;

namespace BetStats.Infrastructure.Ingestion;

// Business dates are a function of an explicit clock; database clocks still own provenance.
public sealed record SyntheticResultScenario(FootballImportScope Scope, string Csv, string Correction, DateOnly HistoryStart, DateOnly TargetDate)
{
    public static SyntheticResultScenario Create(TimeProvider clock)
    {
        var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        string Date(int offset) => day.AddDays(offset).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var header = SyntheticFootballResultsDemo.Header;
        return new(new("FICT", "clock-controlled-fiction"), header +
            $"FICT,{Date(-3)},Amber Comets,Cobalt Owls,clock-result-1,Finished,RegulationTime,2,1,1,0,\n" +
            $"FICT,{Date(-2)},Silver Foxes,Violet Herons,clock-result-2,Finished,RegulationTime,0,0,0,0,\n" +
            $"FICT,{Date(-1)},Amber Comets,Silver Foxes,clock-result-3,Postponed,Unknown,,,,,\n" +
            $"FICT,{Date(2)},Amber Comets,Cobalt Owls,clock-target,Scheduled,Unknown,,,,,\n",
            header + $"FICT,{Date(-3)},Amber Comets,Cobalt Owls,clock-result-1,Finished,RegulationTime,2,2,1,0,\n",
            day.AddDays(-3), day.AddDays(2));
    }
}

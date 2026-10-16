namespace ToolCallingScratch
{
    enum TestGrade
    {
        A = 0,  // Perfect. Requested a tool call with correct payload.
        B = 1,  // Acceptable. Output wasn't a proper tool call, but payload can be used with brute-force parsing hacks.
        // C, unused for single score
        // D, unused for single score
        E = 4,   // Unacceptable. Requested a tool call but the payload is unusable (e.g. bad function name or missing args). 
        F = 5,   // Unacceptable. No discernable attempt to make a tool call, or threw an exception, or timed out.
        Ineligible = 99
    }

    enum AverageTestGrade
    {
        A, B, C, D, E, F,
        Ineligible = 99
    }

    class TestSuiteResults(MatchInfo filterResult)
    {
        public record class IndividualTestResult(TestGrade Grade, TimeSpan TotalTime);

        public MatchInfo FilterResult { get; } = filterResult;
        public List<IndividualTestResult> TestResults { get; } = [];
        public List<IndividualTestResult> EligibleTestResults => [.. TestResults.Where(x => x.Grade != TestGrade.Ineligible)];
        public AverageTestGrade AverageGrade => (AverageTestGrade)(int)((float)TestResults.Sum(x => (int)x.Grade) / TestResults.Count + 0.8f); // +0.8f to nudge grade downwards.
        public TimeSpan AverageTime => TimeSpan.FromMilliseconds(TestResults.Sum(x => x.TotalTime.TotalMilliseconds) / TestResults.Count);
    }

    record class ToolCallingTestMetadata(string UserPrompt, string ExpectedFunctionName, Dictionary<string, string> ExpectedArguments);
}
